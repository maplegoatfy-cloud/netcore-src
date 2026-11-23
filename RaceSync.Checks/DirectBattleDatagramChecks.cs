using RaceSync;

internal static class DirectBattleDatagramChecks
{
    public static void Run()
    {
        // 合成凭据，密钥与 cookie 都取连续字节，方便对拍
        byte[] key = Enumerable.Range(192, 32).Select(i => (byte)i).ToArray();
        byte[] cookie = Enumerable.Range(160, 12).Select(i => (byte)i).ToArray();
        byte[] payload = Enumerable.Range(0, DirectBattleDatagram.NativePacketSize)
            .Select(i => (byte)((i * 7 + 3) & 255)).ToArray();
        byte[] buffer = new byte[DirectBattleDatagram.MaximumDatagramSize];
        const ulong timestamp = 0x0807060504030201;
        var dataHeader = new DirectBattleHeader(DirectBattleKind.Data, 1, 3, 0x01020304, timestamp);
        Require(DirectBattleDatagram.TryWrite(dataHeader, payload, cookie, key, buffer, out int size) &&
                size == 700, "native data size");

        // 线上客户端 _encode 产出的字节，用它固定报头布局与标签算法
        Require(buffer.AsSpan(0, 32).SequenceEqual(Convert.FromHexString(
            "5736523202010103040302010102030405060708a0a1a2a3a4a5a6a7a8a9aaab")), "Godot data header");
        Require(buffer.AsSpan(size - 16, 16).SequenceEqual(Convert.FromHexString(
            "e83f8c1bda1297627e0d8bd46e3fdc02")), "Godot data authentication tag");
        Require(DirectBattleDatagram.TryRead(buffer, 1, 3, cookie, key, out var decoded, out var body) &&
                decoded == dataHeader && body.SequenceEqual(payload), "data round trip");
        // 方向，长度，内容任一被改都应认证失败
        Require(!DirectBattleDatagram.TryRead(buffer, 2, 3, cookie, key, out _, out _), "incorrect sender");
        Require(!DirectBattleDatagram.TryRead(buffer, 1, 2, cookie, key, out _, out _), "incorrect recipient");
        Require(!DirectBattleDatagram.TryRead(buffer.AsSpan(0, size - 1), 1, 3, cookie, key, out _, out _),
            "truncated datagram");
        buffer[32] ^= 0x10;
        Require(!DirectBattleDatagram.TryRead(buffer, 1, 3, cookie, key, out _, out _), "modified data");

        // 探测包没有负载，整包 48 字节
        var probeHeader = new DirectBattleHeader(DirectBattleKind.Probe, 1, 3, 0x11223344, timestamp);
        Require(DirectBattleDatagram.TryWrite(probeHeader, ReadOnlySpan<byte>.Empty, cookie, key, buffer, out size) &&
                size == 48, "probe size");
        Require(buffer.AsSpan(0, size).SequenceEqual(Convert.FromHexString(
            "5736523202020103443322110102030405060708a0a1a2a3a4a5a6a7a8a9aaabdecb1cf599cf702584a9a8a3850b9ef5")),
            "Godot probe datagram");

        // 应答走同一套编解码，只改类型与方向，换一位密钥就必须失败
        var ackHeader = probeHeader with { Kind = DirectBattleKind.Ack, Sender = 3, Recipient = 1 };
        Require(DirectBattleDatagram.TryWrite(ackHeader, ReadOnlySpan<byte>.Empty, cookie, key, buffer, out size) &&
                DirectBattleDatagram.TryRead(buffer.AsSpan(0, size), 3, 1, cookie, key, out var ack, out var empty) &&
                ack == ackHeader && empty.IsEmpty, "ack round trip");
        key[0] ^= 1;
        Require(!DirectBattleDatagram.TryRead(buffer.AsSpan(0, size), 3, 1, cookie, key, out _, out _), "incorrect key");
    }

    private static void Require(bool passed, string name)
    {
        if (!passed)
        {
            throw new InvalidOperationException($"Direct datagram check failed: {name}");
        }
    }
}
