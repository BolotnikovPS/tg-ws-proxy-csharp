using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Security.Cryptography;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application.Logic;

internal sealed class MtProtoInspector(ILogger<MtProtoInspector> logger) : IMtProtoInspector
{
    public (int? Dc, bool? IsMedia) DcFromInit(byte[] data)
    {
        if (data.Length < 64)
        {
            logger.LogDebug("MTProto init too short: {Len} bytes", data.Length);
            return (null, null);
        }

        try
        {
            var key = data.AsSpan(8, 32).ToArray();
            var iv = data.AsSpan(40, 16).ToArray();
            var ks = AesCtr(key, iv, 64);
            Span<byte> plain = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
            {
                plain[i] = (byte)(data[56 + i] ^ ks[56 + i]);
            }

            var proto = BinaryPrimitives.ReadUInt32LittleEndian(plain[..4]);
            var dcRaw = BinaryPrimitives.ReadInt16LittleEndian(plain[4..6]);
            if (proto is 0xEFEFEFEF or 0xEEEEEEEE or 0xDDDDDDDD)
            {
                var dc = Math.Abs(dcRaw);
                if (dc is (>= 1 and <= 5) or 203)
                {
                    return (dc, dcRaw < 0);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to inspect MTProto init (len={Len})", data.Length);
        }

        return (null, null);
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

    public byte[] PatchInitDc(byte[] data, short dcRaw)
    {
        if (data.Length < 64)
        {
            return data;
        }

        try
        {
            var key = data.AsSpan(8, 32).ToArray();
            var iv = data.AsSpan(40, 16).ToArray();
            var ks = AesCtr(key, iv, 64);
            var patched = data.ToArray();
            var dcBytes = BitConverter.GetBytes(dcRaw);
            patched[60] = (byte)(ks[60] ^ dcBytes[0]);
            patched[61] = (byte)(ks[61] ^ dcBytes[1]);
            return patched;
        }
        catch
        {
            return data;
        }
    }
}
