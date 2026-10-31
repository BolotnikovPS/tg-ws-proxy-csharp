using System.Buffers.Binary;
using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Transport;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты кодекa MTProto-пакетов для HTTP/2-транспорта (порт upstream
/// <c>_read_packet</c>/<c>_encode_reply</c> из <c>proxy/cf_h2.py</c>).
/// </summary>
public class H2PacketCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();

    // ---------- abridged ----------

    [Fact]
    public async Task ReadPacketAsync_Abridged_ShortHeader_ReturnsBody()
    {
        var payload = Pattern(32);
        var frame = new byte[] { 8 }.Concat(payload).ToArray();

        var (body, quick) = await ReadAsync(frame, Protocol.ProtoTagAbridged);

        Assert.Equal(payload, body);
        Assert.False(quick);
    }

    [Fact]
    public async Task ReadPacketAsync_Abridged_QuickFlag_IsReported()
    {
        var payload = Pattern(32);
        var frame = new byte[] { 0x80 | 8 }.Concat(payload).ToArray();

        var (body, quick) = await ReadAsync(frame, Protocol.ProtoTagAbridged);

        Assert.Equal(payload, body);
        Assert.True(quick);
    }

    [Fact]
    public async Task ReadPacketAsync_Abridged_ExtendedHeader_ReturnsBody()
    {
        // 0x7F + LE24(words): 600 байт payload требуют расширенного заголовка.
        var payload = Pattern(600);
        var frame = new byte[] { 0x7F, 0x96, 0x00, 0x00 }.Concat(payload).ToArray();

        var (body, quick) = await ReadAsync(frame, Protocol.ProtoTagAbridged);

        Assert.Equal(payload, body);
        Assert.False(quick);
    }

    [Fact]
    public async Task ReadPacketAsync_Abridged_TooShortPacket_Throws()
    {
        // words = 1 -> 4 байта, меньше минимальных 24.
        var frame = new byte[] { 1, 0, 0, 0 };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(frame, Protocol.ProtoTagAbridged));
    }

    // ---------- intermediate ----------

    [Fact]
    public async Task ReadPacketAsync_Intermediate_ReturnsBody()
    {
        var payload = Pattern(48);
        var frame = LengthPrefixed(48, payload);

        var (body, quick) = await ReadAsync(frame, Protocol.ProtoTagIntermediate);

        Assert.Equal(payload, body);
        Assert.False(quick);
    }

    [Fact]
    public async Task ReadPacketAsync_Intermediate_QuickFlag_IsReported()
    {
        var payload = Pattern(48);
        var frame = LengthPrefixed(48 | unchecked((int)0x80000000), payload);

        var (body, quick) = await ReadAsync(frame, Protocol.ProtoTagIntermediate);

        Assert.Equal(payload, body);
        Assert.True(quick);
    }

    [Fact]
    public async Task ReadPacketAsync_Intermediate_LengthBelowMinimum_Throws()
    {
        var frame = LengthPrefixed(20, new byte[20]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(frame, Protocol.ProtoTagIntermediate));
    }

    [Fact]
    public async Task ReadPacketAsync_Intermediate_LengthAboveLimit_Throws()
    {
        // Проверка лимита выполняется до чтения тела, поэтому достаточно заголовка.
        var frame = BitConverter.GetBytes(Protocol.H2MaxPacket + 4);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(frame, Protocol.ProtoTagIntermediate));
    }

    [Fact]
    public async Task ReadPacketAsync_EmptyStream_Throws()
    {
        using var stream = new MemoryStream();
        using var decryptor = new IncrementalCipher(Key, Iv);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => H2PacketCodec.ReadPacketAsync(stream, decryptor, Protocol.ProtoTagIntermediate, CancellationToken.None));
    }

    // ---------- padded intermediate (secure) ----------

    [Fact]
    public async Task ReadPacketAsync_Secure_ZeroPrefix_UsesEmbeddedLength()
    {
        // Первые 8 байт нулевые -> длина пакета берётся из LE32 по смещению 16.
        var payload = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), 12);
        var frame = LengthPrefixed(36, payload);

        var (body, _) = await ReadAsync(frame, Protocol.ProtoTagSecure);

        Assert.Equal(32, body.Length);
        Assert.Equal(payload.AsSpan(0, 32).ToArray(), body);
    }

    [Fact]
    public async Task ReadPacketAsync_Secure_NonZeroPrefix_AlignsTo16()
    {
        // Ненулевой префикс -> 24 + ((length - 24) / 16) * 16 = 40 из 48 байт.
        var payload = Pattern(48);
        var frame = LengthPrefixed(48, payload);

        var (body, _) = await ReadAsync(frame, Protocol.ProtoTagSecure);

        Assert.Equal(40, body.Length);
        Assert.Equal(payload.AsSpan(0, 40).ToArray(), body);
    }

    [Fact]
    public async Task ReadPacketAsync_Secure_PaddingAboveLimit_Throws()
    {
        var payload = new byte[324];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), 4);
        var frame = LengthPrefixed(324, payload);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAsync(frame, Protocol.ProtoTagSecure));
    }

    // ---------- EncodeReply ----------

    [Fact]
    public void EncodeReply_Abridged_UsesShortHeader()
    {
        var body = Pattern(32);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagAbridged);

        Assert.Equal(1 + body.Length, framed.Length);
        Assert.Equal(8, framed[0]);
        Assert.Equal(body, framed.AsSpan(1).ToArray());
    }

    [Fact]
    public void EncodeReply_Abridged_UsesExtendedHeader_ForLargeBody()
    {
        var body = Pattern(600);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagAbridged);

        Assert.Equal(4 + body.Length, framed.Length);
        Assert.Equal(0x7F, framed[0]);
        // words = 150 = 0x96
        Assert.Equal(new byte[] { 0x96, 0x00, 0x00 }, framed.AsSpan(1, 3).ToArray());
        Assert.Equal(body, framed.AsSpan(4).ToArray());
    }

    [Fact]
    public void EncodeReply_Intermediate_PrefixesLength()
    {
        var body = Pattern(48);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagIntermediate);

        Assert.Equal(4 + body.Length, framed.Length);
        Assert.Equal(48, BinaryPrimitives.ReadInt32LittleEndian(framed));
        Assert.Equal(body, framed.AsSpan(4).ToArray());
    }

    [Fact]
    public void EncodeReply_Secure_AddsPaddingWithinLimit()
    {
        var body = Pattern(40);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagSecure);

        var declared = BinaryPrimitives.ReadInt32LittleEndian(framed);
        Assert.Equal(framed.Length - 4, declared);
        Assert.InRange(declared - body.Length, 0, 3);
        Assert.Equal(body, framed.AsSpan(4, body.Length).ToArray());
    }

    [Fact]
    public void EncodeReply_EmptyBody_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => H2PacketCodec.EncodeReply([], Protocol.ProtoTagIntermediate));
    }

    [Fact]
    public void EncodeReply_UnalignedBody_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => H2PacketCodec.EncodeReply(Pattern(30), Protocol.ProtoTagIntermediate));
    }

    [Fact]
    public void EncodeReply_OversizedBody_Throws()
    {
        var body = new byte[Protocol.H2MaxPacket + 4];

        Assert.Throws<InvalidDataException>(
            () => H2PacketCodec.EncodeReply(body, Protocol.ProtoTagIntermediate));
    }

    // ---------- roundtrip ----------

    [Fact]
    public async Task RoundTrip_Abridged_ReturnsOriginalBody()
    {
        var body = Pattern(600);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagAbridged);
        var (decoded, _) = await ReadAsync(framed, Protocol.ProtoTagAbridged);

        Assert.Equal(body, decoded);
    }

    [Fact]
    public async Task RoundTrip_Intermediate_ReturnsOriginalBody()
    {
        var body = Pattern(48);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagIntermediate);
        var (decoded, _) = await ReadAsync(framed, Protocol.ProtoTagIntermediate);

        Assert.Equal(body, decoded);
    }

    [Fact]
    public async Task RoundTrip_Secure_ReturnsOriginalBody()
    {
        // 40 = 24 + 16: длина пакета восстанавливается однозначно при любом padding 0..3.
        var body = Pattern(40);

        var framed = H2PacketCodec.EncodeReply(body, Protocol.ProtoTagSecure);
        var (decoded, _) = await ReadAsync(framed, Protocol.ProtoTagSecure);

        Assert.Equal(body, decoded);
    }

    // ---------- helpers ----------

    /// <summary>Шифрует кадр, читает его кодеком и возвращает результат.</summary>
    private static async Task<(byte[] Body, bool Quick)> ReadAsync(byte[] plainFrame, byte[] protoTag)
    {
        using var encryptor = new IncrementalCipher(Key, Iv);
        var cipherFrame = encryptor.Update(plainFrame);

        using var stream = new MemoryStream(cipherFrame);
        using var decryptor = new IncrementalCipher(Key, Iv);
        return await H2PacketCodec.ReadPacketAsync(stream, decryptor, protoTag, CancellationToken.None);
    }

    private static byte[] LengthPrefixed(int length, byte[] payload) =>
        BitConverter.GetBytes(length).Concat(payload).ToArray();

    private static byte[] Pattern(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = (byte)(i * 7 + 3);
        }

        return buffer;
    }
}
