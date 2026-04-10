using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Security.Cryptography;
using TgWsProxy.Application.Logic.Helpers;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application.Logic;

internal sealed class MtProtoInspector(ILogger<MtProtoInspector> logger) : IMtProtoInspector
{
    private static readonly byte[] ReservedStart1 = [0x48, 0x45, 0x41, 0x44]; // HEAD
    private static readonly byte[] ReservedStart2 = [0x50, 0x4F, 0x53, 0x54]; // POST
    private static readonly byte[] ReservedStart3 = [0x47, 0x45, 0x54, 0x20]; // GET
    private static readonly byte[] ReservedStart4 = [0xEE, 0xEE, 0xEE, 0xEE];
    private static readonly byte[] ReservedStart5 = [0xDD, 0xDD, 0xDD, 0xDD];
    private static readonly byte[] ReservedStart6 = [0x16, 0x03, 0x01, 0x02];
    private static readonly byte[] ReservedContinue = [0x00, 0x00, 0x00, 0x00];

    public (int? Dc, bool? IsMedia) DcFromInit(byte[] data, byte[] secret)
    {
        if (data.Length < 64)
        {
            logger.LogDebug("MTProto init too short: {Len} bytes", data.Length);
            return (null, null);
        }

        try
        {
            // dec_prekey_and_iv = handshake[8:56]
            var decPrekeyAndIv = data.AsSpan(8, 48).ToArray();
            var decPrekey = decPrekeyAndIv.AsSpan(0, 32).ToArray();
            var decIv = decPrekeyAndIv.AsSpan(32, 16).ToArray();

            // dec_key = SHA256(dec_prekey + secret)
            var decKey = SHA256.HashData([.. decPrekey, .. secret]);

            // Decrypt to get proto_tag and dc_idx We need to decrypt positions 56-64 (proto_tag +
            // dc_idx + 2 random bytes) Using AES-CTR: decrypt = encrypt (CTR is symmetric) Build
            // counter for block 3: iv + 3 (since 56/16 = 3)
            var counter = BuildCounterAt(decIv, 56L / 16);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = decKey;
            using var enc = aes.CreateEncryptor();

            var keystream = new byte[16];
            enc.TransformBlock(counter, 0, 16, keystream, 0);

            // proto_tag is at positions 56-59 in plaintext plaintext[56:64] = ciphertext[56:64] XOR keystream[8:16]
            var protoTag = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                protoTag[i] = (byte)(data[56 + i] ^ keystream[8 + i]);
            }

            var dcRaw = (short)((data[60] ^ keystream[12]) | ((data[61] ^ keystream[13]) << 8));

            var proto = BinaryPrimitives.ReadUInt32LittleEndian(protoTag);
            logger.LogDebug("MTProto inspect: proto=0x{Proto:X8} dcRaw={DcRaw} secretLen={SecretLen}",
                proto, dcRaw, secret.Length);

            if (proto is 0xEFEFEFEF or 0xEEEEEEEE or 0xDDDDDDDD)
            {
                var dc = Math.Abs(dcRaw);
                if (dc is (>= 1 and <= 5) or 203)
                {
                    return (dc, dcRaw < 0);
                }

                logger.LogDebug("MTProto: DC value {Dc} out of valid range", dc);
            }
            else
            {
                logger.LogDebug("MTProto: unknown proto tag 0x{Proto:X8}", proto);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to inspect MTProto init (len={Len})", data.Length);
        }

        return (null, null);
    }

    private static byte[] BuildCounterAt(byte[] iv, long blockIndex)
    {
        var counter = iv.ToArray();
        var carry = (ulong)blockIndex;
        for (var b = 15; b >= 0 && carry != 0; b--)
        {
            var sum = (ushort)(counter[b] + (carry & 0xFF));
            counter[b] = (byte)sum;
            carry = (carry >> 8) + (uint)(sum >> 8);
        }
        return counter;
    }

    public byte[] AesCtr(byte[] key, byte[] iv, int len)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();
        var ctr = iv.ToArray();
        var output = new byte[len];
        var block = new byte[16];
        for (var pos = 0; pos < len;)
        {
            enc.TransformBlock(ctr, 0, 16, block, 0);
            var n = Math.Min(16, len - pos);
            Buffer.BlockCopy(block, 0, output, pos, n);
            pos += n;
            for (var i = 15; i >= 0; i--)
            {
                if (++ctr[i] != 0)
                {
                    break;
                }
            }
        }
        return output;
    }

    public bool IsHttpTransport(ReadOnlySpan<byte> data)
        => data.StartsWith("POST "u8) ||
               data.StartsWith("GET "u8) ||
               data.StartsWith("HEAD "u8) ||
               data.StartsWith("OPTIONS "u8);

    public byte[] PatchInitDc(byte[] data, short dcRaw, byte[] secret)
    {
        if (data.Length < 64)
        {
            return data;
        }

        try
        {
            var decPrekeyAndIv = data.AsSpan(8, 48).ToArray();
            var decPrekey = decPrekeyAndIv.AsSpan(0, 32).ToArray();
            var decIv = decPrekeyAndIv.AsSpan(32, 16).ToArray();
            var decKey = SHA256.HashData([.. decPrekey, .. secret]);

            var counter = BuildCounterAt(decIv, 56L / 16);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = decKey;
            using var enc = aes.CreateEncryptor();

            var keystream = new byte[16];
            enc.TransformBlock(counter, 0, 16, keystream, 0);

            var patched = data.ToArray();
            var dcBytes = BitConverter.GetBytes(dcRaw);
            patched[60] = (byte)(keystream[12] ^ dcBytes[0]);
            patched[61] = (byte)(keystream[13] ^ dcBytes[1]);
            return patched;
        }
        catch
        {
            return data;
        }
    }

    public byte[] GenerateRelayInit(byte[] protoTag, short dcIdx)
    {
        byte[] rnd;
        var rng = RandomNumberGenerator.Create();
        do
        {
            rnd = new byte[64];
            rng.GetBytes(rnd);
        } while (IsReservedHandshake(rnd));

        var encKey = rnd.AsSpan(8, 32).ToArray();
        var encIv = rnd.AsSpan(40, 16).ToArray();

        // Generate keystream using IncrementalCipher (same mechanism used in bridge for
        // consistency) This ensures the keystream is identical to what Telegram expects.
        using var cipher = new IncrementalCipher(encKey, encIv);
        var ks = cipher.Update(new byte[64]);

        // Python: keystream_tail[i] = encrypted_full[i] ^ rnd_bytes[i] But encrypted_full[i] =
        // rnd[i] ^ ks[i], so: keystream_tail[i] = (rnd[i] ^ ks[i]) ^ rnd[i] = ks[i] encrypted_tail
        // = tail_plain XOR ks result[56+i] = tail_plain[i] ^ ks[56+i]

        var tailPlain = new byte[8];
        Buffer.BlockCopy(protoTag, 0, tailPlain, 0, 4);
        var dcBytes = BitConverter.GetBytes(dcIdx);
        Buffer.BlockCopy(dcBytes, 0, tailPlain, 4, 2);
        rng.GetBytes(tailPlain, 6, 2);

        var result = rnd.ToArray();
        for (var i = 0; i < 8; i++)
        {
            result[56 + i] = (byte)(tailPlain[i] ^ ks[56 + i]);
        }

        return result;
    }

    private static bool IsReservedHandshake(byte[] rnd)
    {
        // Проверка первого байта
        if (rnd[0] == 0xEF)
        {
            return true;
        }

        // Проверка первых 4 байт
        var first4 = rnd.AsSpan(0, 4);
        if (first4.SequenceEqual(ReservedStart1) ||
            first4.SequenceEqual(ReservedStart2) ||
            first4.SequenceEqual(ReservedStart3) ||
            first4.SequenceEqual(ReservedStart4) ||
            first4.SequenceEqual(ReservedStart5) ||
            first4.SequenceEqual(ReservedStart6))
        {
            return true;
        }

        // Проверка байт 4-8
        var continue4 = rnd.AsSpan(4, 4);
        return continue4.SequenceEqual(ReservedContinue);
    }
}
