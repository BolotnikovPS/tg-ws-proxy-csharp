using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Transport;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Разбор MTProto handshake и генерация relay-пакетов (порт upstream <c>_try_handshake</c> /
/// <c>_generate_relay_init</c>).
/// </summary>
internal sealed class MtProtoInspector(ILogger<MtProtoInspector> logger) : IMtProtoInspector
{
    /// <inheritdoc />
    public MtProtoHandshake? TryHandshake(ReadOnlySpan<byte> init, byte[] secret)
    {
        if (init.Length < Protocol.HandshakeLen)
        {
            logger.LogDebug("MTProto init too short: {Len} bytes", init.Length);
            return null;
        }

        // dec_prekey_and_iv = handshake[8:56]; dec_key = SHA256(dec_prekey + secret)
        var decPrekey = init.Slice(Protocol.SkipLen, Protocol.PreKeyLen);
        var decIv = init.Slice(Protocol.SkipLen + Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        var decKey = SHA256.HashData([.. decPrekey, .. secret]);

        // AES-CTR симметричен: тот же поток используется как keystream.
        Span<byte> plain = stackalloc byte[Protocol.HandshakeLen];
        using (var cipher = new IncrementalCipher(decKey, decIv))
        {
            Span<byte> keystream = stackalloc byte[Protocol.HandshakeLen];
            cipher.UpdateInto(Protocol.Zero64, keystream);
            for (var i = 0; i < Protocol.HandshakeLen; i++)
            {
                plain[i] = (byte)(init[i] ^ keystream[i]);
            }
        }

        var protoTag = plain.Slice(Protocol.ProtoTagPos, 4);
        int protoInt;
        if (protoTag.SequenceEqual(Protocol.ProtoTagAbridged))
        {
            protoInt = Protocol.ProtoAbridgedInt;
        }
        else if (protoTag.SequenceEqual(Protocol.ProtoTagIntermediate))
        {
            protoInt = Protocol.ProtoIntermediateInt;
        }
        else if (protoTag.SequenceEqual(Protocol.ProtoTagSecure))
        {
            protoInt = Protocol.ProtoPaddedIntermediateInt;
        }
        else
        {
            logger.LogDebug("MTProto: unknown proto tag {Tag}", Convert.ToHexString(protoTag));
            return null;
        }

        var dcIdx = BinaryPrimitives.ReadInt16LittleEndian(plain.Slice(Protocol.DcIdxPos, 2));
        var dc = Math.Abs((int)dcIdx);
        var isMedia = dcIdx < 0;

        return new MtProtoHandshake(dc, isMedia, protoTag.ToArray(), protoInt);
    }

    /// <inheritdoc />
    public bool IsHttpTransport(ReadOnlySpan<byte> data)
        => data.StartsWith("POST "u8) ||
           data.StartsWith("GET "u8) ||
           data.StartsWith("HEAD "u8) ||
           data.StartsWith("OPTIONS "u8);

    /// <inheritdoc />
    public byte[] GenerateRelayInit(byte[] protoTag, short dcIdx)
    {
        var rnd = new byte[Protocol.HandshakeLen];
        var rng = RandomNumberGenerator.Create();
        do
        {
            rng.GetBytes(rnd);
        }
        while (IsReservedHandshake(rnd));

        var encKey = rnd.AsSpan(Protocol.SkipLen, Protocol.PreKeyLen).ToArray();
        var encIv = rnd.AsSpan(Protocol.SkipLen + Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        Span<byte> keystream = stackalloc byte[Protocol.HandshakeLen];
        using (var cipher = new IncrementalCipher(encKey, encIv))
        {
            cipher.UpdateInto(Protocol.Zero64, keystream);
        }

        // tail_plain = proto_tag + dc_idx(LE) + 2 случайных байта
        Span<byte> tailPlain = stackalloc byte[8];
        protoTag.AsSpan(0, 4).CopyTo(tailPlain);
        BinaryPrimitives.WriteInt16LittleEndian(tailPlain[4..], dcIdx);
        Span<byte> randomTail = stackalloc byte[2];
        rng.GetBytes(randomTail);
        randomTail.CopyTo(tailPlain[6..]);

        var result = (byte[])rnd.Clone();
        for (var i = 0; i < 8; i++)
        {
            result[Protocol.ProtoTagPos + i] = (byte)(tailPlain[i] ^ keystream[Protocol.ProtoTagPos + i]);
        }

        return result;
    }

    /// <summary>
    /// Проверяет, что сгенерированный handshake не начинается с зарезервированных последовательностей.
    /// </summary>
    private static bool IsReservedHandshake(ReadOnlySpan<byte> rnd)
    {
        if (Protocol.ReservedFirstBytes.Contains(rnd[0]))
        {
            return true;
        }

        var first4 = rnd[..4];
        foreach (var reserved in Protocol.ReservedStarts)
        {
            if (first4.SequenceEqual(reserved))
            {
                return true;
            }
        }

        return rnd.Slice(4, 4).SequenceEqual(Protocol.ReservedContinue);
    }
}
