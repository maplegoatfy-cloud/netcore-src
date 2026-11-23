using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RaceSync;

// 报文类型，1 是带 652 字节原生包的 Data，2 和 3 是零负载的探测与应答
public enum DirectBattleKind : byte
{
    Data = 1,
    Probe = 2,
    Ack = 3,
}

// 解析后的报头，收发席位都在 1 到 4 之间，序号从 1 起
public readonly record struct DirectBattleHeader(
    DirectBattleKind Kind,
    byte Sender,
    byte Recipient,
    uint Sequence,
    ulong TimestampMicroseconds);

/// 四席直连 UDP 报文的编解码与认证，报头与负载一起参与 HMAC
public static class DirectBattleDatagram
{
    // 小端写出后正好是 ASCII 的 W6R2
    private const uint Magic = 0x32523657;
    // 格式版本，与 magic 一起做首包甄别
    private const byte Revision = 2;

    // 报头 32 字节，标签 16 字节，房间 cookie 12 字节，对端密钥 32 字节
    public const int HeaderSize = 32;
    public const int TagSize = 16;
    public const int RoomCookieSize = 12;
    public const int KeySize = 32;
    // 原生对战包固定 652 字节，数据报 700 字节，探测与应答 48 字节
    public const int NativePacketSize = 652;
    public const int MaximumDatagramSize = HeaderSize + NativePacketSize + TagSize;

    /// 按报头类型写出整包，参数不合法时不写目标缓冲区
    public static bool TryWrite(
        DirectBattleHeader header,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> roomCookie,
        ReadOnlySpan<byte> peerKey,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        // 席位，负载长度，cookie 长度，密钥长度任一不符都直接拒绝
        if (!IsValid(header) || payload.Length != PayloadSize(header.Kind) ||
            roomCookie.Length != RoomCookieSize || peerKey.Length != KeySize)
        {
            return false;
        }

        int bodySize = HeaderSize + payload.Length;
        int totalSize = bodySize + TagSize;
        // 放不下或与输入重叠时拒绝，避免把调用方的 cookie 和密钥写坏
        if (destination.Length < totalSize ||
            destination[..totalSize].Overlaps(roomCookie) ||
            destination[..totalSize].Overlaps(peerKey))
        {
            return false;
        }

        payload.CopyTo(destination[HeaderSize..bodySize]);
        // 报头偏移固定，改布局必须同步改 TryRead
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        destination[4] = Revision;
        destination[5] = (byte)header.Kind;
        destination[6] = header.Sender;
        destination[7] = header.Recipient;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], header.TimestampMicroseconds);
        // cookie 也在摘要范围内，改了它标签就对不上
        roomCookie.CopyTo(destination[20..HeaderSize]);
        // 摘要在栈上算，每包不产生托管分配
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(peerKey, destination[..bodySize], digest);
        // 只发送摘要前 16 字节，校验侧按同样长度截断
        digest[..TagSize].CopyTo(destination[bodySize..totalSize]);
        written = totalSize;
        return true;
    }

    /// 校验并解析，返回的负载是输入缓冲区的切片，用完之前不要复用该缓冲区
    public static bool TryRead(
        ReadOnlySpan<byte> datagram,
        byte expectedSender,
        byte localSeat,
        ReadOnlySpan<byte> roomCookie,
        ReadOnlySpan<byte> peerKey,
        out DirectBattleHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        // 先做长度，magic，版本，cookie 的廉价检查，再算 HMAC
        if (datagram.Length < HeaderSize + TagSize || datagram.Length > MaximumDatagramSize ||
            roomCookie.Length != RoomCookieSize || peerKey.Length != KeySize ||
            BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic ||
            datagram[4] != Revision || !datagram[20..HeaderSize].SequenceEqual(roomCookie))
        {
            return false;
        }

        var candidate = new DirectBattleHeader(
            (DirectBattleKind)datagram[5],
            datagram[6],
            datagram[7],
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(datagram[12..]));
        // 负载长度由类型决定，长度对不上说明报文被截断或拼接
        int payloadSize = PayloadSize(candidate.Kind);
        // 席位或方向不对同样丢弃，避免同一密钥下互相冒充
        if (!IsValid(candidate) || candidate.Sender != expectedSender || candidate.Recipient != localSeat ||
            payloadSize < 0 || datagram.Length != HeaderSize + payloadSize + TagSize)
        {
            return false;
        }

        int bodySize = HeaderSize + payloadSize;
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(peerKey, datagram[..bodySize], digest);
        // 定长比较，失败时不透露是哪一位不匹配
        if (!CryptographicOperations.FixedTimeEquals(digest[..TagSize], datagram[bodySize..]))
        {
            return false;
        }

        header = candidate;
        payload = datagram[HeaderSize..bodySize];
        return true;
    }

    // 席位限定 1 到 4，自己发给自己和序号 0 都算非法
    private static bool IsValid(DirectBattleHeader header) =>
        header.Sender is >= 1 and <= 4 &&
        header.Recipient is >= 1 and <= 4 &&
        header.Sender != header.Recipient &&
        header.Sequence != 0;

    // 未知类型返回 -1，调用方按长度不符处理
    private static int PayloadSize(DirectBattleKind kind) => kind switch
    {
        DirectBattleKind.Data => NativePacketSize,
        DirectBattleKind.Probe or DirectBattleKind.Ack => 0,
        _ => -1,
    };
}
