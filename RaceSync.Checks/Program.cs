using System.Numerics;
using RaceSync;

// 自检覆盖缓冲区的边界条件与位置修正的参考数值，用法与正式接入一致
double tick = BattlePacketBuffer.TickMilliseconds;
var buffer = new BattlePacketBuffer();
var ready = new List<BattlePacket>();
var bytes = new byte[BattlePacketBuffer.PacketSize];

// 席号越界，序号为 0，长度不足，时钟非有限值都应拒收
Check(!buffer.Push(11, 1, bytes, 0), "seat outside room limit");
Check(!buffer.Push(1, 0, bytes, 0), "invalid sequence");
Check(!buffer.Push(1, 1, bytes.AsSpan(1), 0), "incompatible packet size");
Check(!buffer.Push(1, 1, bytes, double.NaN), "invalid clock");
bytes[11] = 91;
Check(buffer.Push(1, 1, bytes, 1000), "first packet accepted");
// 同一序号再推一次按重复包丢弃
bytes[11] = 0;
Check(!buffer.Push(1, 1, bytes, 1000), "duplicate rejected");
buffer.Poll(1000, ready);
Check(ready.Count == 0, "one-tick playout delay");
buffer.Poll(1000 + tick, ready);
// 包内是副本，改原缓冲区后已入队的数据仍是 91
Check(ready.Count == 1 && ready[0].Sequence == 1 && ready[0].Payload.Span[11] == 91, "independent packet copy and due time");

// 两个座位交错发送，每步每座位只放一条，按座位加入顺序输出
buffer.Clear();
ready.Clear();
foreach (int peer in new[] { 2, 1 })
{
    for (int sequence = 1; sequence <= 3; sequence++)
        Check(buffer.Push(peer, sequence, bytes, 1000 + (sequence - 1) * tick), "ordered packets accepted");
}
buffer.Poll(1000 + 3 * tick, ready);
Check(ready.Count == 2 && ready.Select(packet => packet.EntityId).SequenceEqual(new[] { 2, 1 }), "one packet per peer per tick");
Check(ready.All(packet => packet.Sequence == 1), "first chronological tick");
ready.Clear();
buffer.Poll(1000 + 3 * tick, ready);
Check(ready.Count == 2 && ready.All(packet => packet.Sequence == 2), "second tick can share wall time");
buffer.ClearPeer(2);
Check(!buffer.GetStatistics().ContainsKey(2), "leaving seat clears pacing state");

// 队列上限 8，多出的从最旧开始丢，统计里应记 4 次跳过
buffer.Clear();
ready.Clear();
for (int sequence = 1; sequence <= 12; sequence++)
    Check(buffer.Push(1, sequence, bytes, 1000 + (sequence - 1) * tick), "queue input");
BattlePeerStats stats = buffer.GetStatistics()[1];
Check(stats.Queued == 8 && stats.Skipped == 4 && stats.MaxQueued == 8, "bounded queue drops oldest packets");
buffer.Poll(1000 + 12 * tick, ready);
Check(ready.Count == 1 && ready[0].Sequence == 8, "stale sequence backlog is skipped");

// 间隔 400 ms 触发重置，空洞的 3 个序号计入统计
buffer.Clear();
ready.Clear();
Check(buffer.Push(1, 1, bytes, 1000), "reset input");
Check(buffer.Push(1, 5, bytes, 1400), "packet after arrival gap");
stats = buffer.GetStatistics()[1];
Check(stats.Resets == 1 && stats.Skipped == 1 && stats.SequenceGaps == 3, "arrival gap resets clock and accounts for skipped sequences");
buffer.Poll(1400 + tick, ready);
Check(ready.Count == 1 && ready[0].Sequence == 5, "new timeline emits only latest packet");

// 滞留超过 120 ms 的包作废，不再播出
buffer.Clear();
ready.Clear();
Check(buffer.Push(1, 1, bytes, 1000), "stale input");
buffer.Poll(1120.01, ready);
Check(ready.Count == 0 && buffer.GetStatistics()[1].Skipped == 1, "old arrival expires");

// 最后一个包晚 30 ms 到达，抖动应把目标延迟抬到 46.7 ms 左右
buffer.Clear();
ready.Clear();
for (int sequence = 1; sequence <= 8; sequence++)
    Check(buffer.Push(1, sequence, bytes, 1000 + (sequence - 1) * tick + (sequence == 8 ? 30 : 0)), "jitter sample");
Check(Math.Abs(buffer.GetStatistics()[1].TargetMilliseconds - 46.7) < 0.001, "observed jitter extends target playout delay");

// 本地卡顿后不补播，只保留最新一条
buffer.Clear();
buffer.Poll(1000, ready);
Check(buffer.Push(1, 1, bytes, 1000), "local stall start");
buffer.Poll(1001, ready);
Check(buffer.Push(1, 2, bytes, 1010), "first pending packet");
Check(buffer.Push(1, 3, bytes, 1090), "second pending packet");
buffer.Poll(1110, ready);
stats = buffer.GetStatistics()[1];
Check(stats.Skipped == 2 && stats.Resets == 1 && ready.Single().Sequence == 3, "catch-up does not replay packets after a local stall");

// 位置修正，远车 32 米外满权重，14 米内强制归零
var correction = new BattlePoseCorrection();
Vector3 distant = new(100, 0, 0);
Check(correction.OffsetFor(Vector3.Zero, distant) == Vector3.Zero, "first sample initializes pose");
Vector3 shift = correction.OffsetFor(Vector3.UnitX, distant);
Check(Math.Abs(shift.X - (-0.576f)) < 0.0001f, "small far correction follows reference math");
Check(correction.OffsetFor(new Vector3(2, 0, 0), new Vector3(16, 0, 0)) == Vector3.Zero, "native pose used at collision radius");
Check(correction.OffsetFor(new Vector3(27, 0, 0), distant) == Vector3.Zero, "teleport clears correction history");
// 匀速行驶时残差归零，偏移收敛到 0，不产生恒定拖影
correction.Clear();
correction.OffsetFor(Vector3.Zero, distant);
for (int index = 1; index <= 100; index++)
{
    Vector3 offset = correction.OffsetFor(new Vector3(index, 0, 0), distant + new Vector3(index, 0, 0));
    Check(offset.Length() < BattlePoseCorrection.MaxErrorMeters + 0.0001f, "bounded presentation displacement");
    if (index == 100)
        Check(offset.Length() < 0.0001f, "steady motion has no constant correction lag");
}

DirectBattleDatagramChecks.Run();
Console.WriteLine("RaceSync checks passed.");

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}
