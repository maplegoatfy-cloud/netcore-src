namespace RaceSync;

// 一条待播放的原生对战包，Payload 是独立副本，可以脱离接收缓冲区长期持有
public readonly record struct BattlePacket(
    int EntityId, long Sequence, ReadOnlyMemory<byte> Payload, double ArrivalMilliseconds);

// 单个座位的统计快照，两个延迟字段单位是毫秒并已取整
public readonly record struct BattlePeerStats(
    double BufferMilliseconds, double TargetMilliseconds, int Queued, int MaxQueued,
    long Received, long Emitted, long Skipped, long SequenceGaps, long Rejected,
    long Resets, long LastEmitted);

/// 把原生 60 Hz 对战包按时间轴排队后放出，只调节奏不改内容
public sealed class BattlePacketBuffer
{
    public const int PacketSize = 652;
    // 房间座位上限，直连报文只认 4 席，两处口径不同
    public const int MaxRoomSeat = 10;
    // 60 Hz 的一步，序号乘以它得到名义发送时刻
    public const double TickMilliseconds = 1000.0 / 60.0;

    // 播放延迟上限，再等就不如直接丢
    private const double MaxDelayMilliseconds = 100.0;
    // 到达间隔超过这个值判为断流，重建时间轴
    private const double ResetGapMilliseconds = 250.0;
    // 排队超过这个时长的包丢弃，旧状态不如不播
    private const double MaxAgeMilliseconds = 120.0;
    // 每座位最多积压 8 包，满了丢最旧的
    private const int MaxQueue = 8;
    // 用最近 64 个 offset 样本估计基准与抖动
    private const int OffsetWindow = 64;

    private readonly List<Peer> _peers = new(MaxRoomSeat);

    /// 收到一包就调用，时钟用单调毫秒，必须与 Poll 同一个时钟
    public bool Push(int entityId, long sequence, ReadOnlySpan<byte> payload, double nowMilliseconds)
    {
        // 席号，序号，长度，时钟任一非法都拒收
        if (entityId is < 1 or > MaxRoomSeat || sequence < 1 || payload.Length != PacketSize
            || !double.IsFinite(nowMilliseconds))
            return false;

        // offset 把序号映射回本地时钟，等价于时钟偏差加路径延迟
        double offset = nowMilliseconds - sequence * TickMilliseconds;
        Peer? peer = FindPeer(entityId);
        if (peer is null)
        {
            // 首次见到这个座位，用当前包建立基准
            peer = new Peer(entityId, nowMilliseconds, offset);
            _peers.Add(peer);
        }

        // 序号不前进的一律丢弃，重复包与乱序包走同一条路，不做重排
        if (sequence <= peer.LastReceived)
        {
            peer.Rejected++;
            return false;
        }

        // 断流超过 250 ms 或基准跳变超过 500 ms 就重建时间轴
        if (nowMilliseconds - peer.LastArrival > ResetGapMilliseconds
            || Math.Abs(offset - peer.TargetOffset) > 500.0)
        {
            // 积压的包全部作废，延迟回到一个 tick
            peer.Skipped += peer.Queue.Count;
            peer.Queue.Clear();
            peer.Offsets.Clear();
            peer.Offset = peer.TargetOffset = offset;
            peer.Delay = peer.TargetDelay = TickMilliseconds;
            peer.Resets++;
        }

        // 序号空洞计入统计，丢包与重连都会体现在这里
        if (peer.LastReceived > 0)
            peer.SequenceGaps += sequence - peer.LastReceived - 1;
        peer.LastReceived = sequence;
        peer.LastArrival = nowMilliseconds;
        peer.Received++;

        // 样本窗口按到达顺序滚动，超容量时先丢最旧的
        if (peer.Offsets.Count == OffsetWindow)
            peer.Offsets.Dequeue();
        peer.Offsets.Enqueue(offset);
        // 每 8 包重估一次，样本不足 8 个时不动
        if (peer.Offsets.Count >= 8 && peer.Received % 8 == 0)
        {
            Span<double> offsets = stackalloc double[peer.Offsets.Count];
            int index = 0;
            foreach (double recentOffset in peer.Offsets)
                offsets[index++] = recentOffset;
            offsets.Sort();
            // 最小值对应最快的那批包，拿它当基准不会把平均延迟算进去
            peer.TargetOffset = offsets[0];
            int percentile = (int)Math.Ceiling(offsets.Length * 0.95) - 1;
            // p95 与最小值之差是抖动带宽，再加一个 tick 作基础延迟
            double spread = offsets[percentile] - offsets[0];
            peer.TargetDelay = Math.Clamp(spread + TickMilliseconds, TickMilliseconds, MaxDelayMilliseconds);
        }

        // 队列满时丢最旧的，宁可少一帧也不要越积越旧
        if (peer.Queue.Count == MaxQueue)
        {
            peer.Queue.Dequeue();
            peer.Skipped++;
        }
        // 存的副本，接收缓冲区可以立刻回收
        peer.Queue.Enqueue(new BattlePacket(entityId, sequence, payload.ToArray(), nowMilliseconds));
        peer.MaxQueued = Math.Max(peer.MaxQueued, peer.Queue.Count);
        return true;
    }

    /// 每个原生物理步调用一次，每座位最多追加一条，集合可复用
    public void Poll(double nowMilliseconds, ICollection<BattlePacket> ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        if (!double.IsFinite(nowMilliseconds))
            return;

        foreach (Peer peer in _peers)
        {
            // 第一拍没有上次采样，按一个 tick 计
            double elapsed = peer.LastPoll < 0.0
                ? TickMilliseconds
                : Math.Max(0.0, nowMilliseconds - peer.LastPoll);
            // 本地卡顿超过 100 ms 时只留最新一条，其余计入跳过
            if (elapsed > MaxDelayMilliseconds && peer.Queue.Count != 0)
            {
                while (peer.Queue.Count > 1)
                {
                    peer.Queue.Dequeue();
                    peer.Skipped++;
                }
                peer.Resets++;
            }
            peer.LastPoll = nowMilliseconds;
            // 平滑步长按两个 tick 截断，避免一帧很长把时间轴拉飞
            elapsed = Math.Min(elapsed, TickMilliseconds * 2.0);
            // 每毫秒最多纠 15%，慢一点不容易被看出来
            peer.Offset = MoveToward(peer.Offset, peer.TargetOffset, elapsed * 0.15);
            // 延迟上调快下调慢，涨按 20% 落按千分之三，避免在边界来回抖
            double rate = peer.TargetDelay > peer.Delay ? 0.20 : 0.003;
            peer.Delay = MoveToward(peer.Delay, peer.TargetDelay, elapsed * rate);
            // 换算成当前该播到哪个序号，序号原点已经被 offset 吸收
            double cutoff = (nowMilliseconds - peer.Offset - peer.Delay) / TickMilliseconds;

            while (peer.Queue.TryPeek(out BattlePacket head))
            {
                // 滞留超时的包直接丢，已经没有参考价值
                if (nowMilliseconds - head.ArrivalMilliseconds > MaxAgeMilliseconds)
                {
                    peer.Queue.Dequeue();
                    peer.Skipped++;
                    continue;
                }
                // 队头还没到期就停下，后面的只会更晚
                if (head.Sequence > cutoff + 0.00001)
                    break;

                peer.Queue.Dequeue();
                // 落后超过 4 个 tick 就继续往前跳，只播最新的一条
                if (peer.Queue.Count > 0 && cutoff - head.Sequence > 4.0)
                {
                    peer.Skipped++;
                    continue;
                }
                // 同一序号只播一次
                if (head.Sequence > peer.LastEmitted)
                {
                    peer.LastEmitted = head.Sequence;
                    peer.Emitted++;
                    ready.Add(head);
                }
                break;
            }
        }
    }

    /// 座位离开时清掉它的全部节奏状态
    public void ClearPeer(int entityId) => _peers.RemoveAll(peer => peer.EntityId == entityId);

    public void Clear() => _peers.Clear();

    /// 统计只读，延迟取整到 0.1 ms，方便记日志
    public IReadOnlyDictionary<int, BattlePeerStats> GetStatistics()
    {
        var result = new Dictionary<int, BattlePeerStats>(_peers.Count);
        foreach (Peer peer in _peers)
        {
            result.Add(peer.EntityId, new BattlePeerStats(
                SnapTenths(peer.Delay), SnapTenths(peer.TargetDelay), peer.Queue.Count, peer.MaxQueued,
                peer.Received, peer.Emitted, peer.Skipped, peer.SequenceGaps, peer.Rejected,
                peer.Resets, peer.LastEmitted));
        }
        return result;
    }

    private Peer? FindPeer(int entityId)
    {
        // 座位数上限只有 10，线性扫描足够
        foreach (Peer peer in _peers)
        {
            if (peer.EntityId == entityId)
                return peer;
        }
        return null;
    }

    private static double MoveToward(double current, double target, double limit)
    {
        // 每次最多走 limit，正负对称
        return current + Math.Clamp(target - current, -limit, limit);
    }

    // 取整到 0.1 ms，压掉浮点尾数
    private static double SnapTenths(double value) => Math.Floor(value / 0.1 + 0.5) * 0.1;

    private sealed class Peer(int entityId, double nowMilliseconds, double offset)
    {
        public readonly int EntityId = entityId;
        // 到达顺序队列，元素序号递增
        public readonly Queue<BattlePacket> Queue = new(MaxQueue);
        // 最近的 offset 样本，用于估计基准与抖动
        public readonly Queue<double> Offsets = new(OffsetWindow);
        // 当前使用的偏移与平滑目标
        public double Offset = offset;
        public double TargetOffset = offset;
        // 当前播放延迟与目标延迟
        public double Delay = TickMilliseconds;
        public double TargetDelay = TickMilliseconds;
        // 最近一次到达与最近一次 Poll 的时刻
        public double LastArrival = nowMilliseconds;
        public double LastPoll = -1.0;
        // 下面几个是去重与统计用的计数器
        public long LastReceived;
        public long LastEmitted;
        public long Received;
        public long Emitted;
        public long Skipped;
        public long SequenceGaps;
        public long Rejected;
        public long Resets;
        public int MaxQueued;
    }
}
