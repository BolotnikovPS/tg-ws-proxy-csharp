#nullable enable

using System.Security.Cryptography;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Дешифрует фрагменты MTProto (AES-CTR) и при наличии нескольких сообщений разбивает chunk на части.
/// </summary>
internal sealed class MtProtoMsgSplitter(byte[] init)
{
    private readonly byte[] key = init.AsSpan(8, 32).ToArray();
    private readonly byte[] iv = init.AsSpan(40, 16).ToArray();
    private long streamOffset = 64; // skip init packet keystream like Python implementation

    /// <summary>
    /// Расшифровывает chunk и, при наличии нескольких MTProto-сообщений, разбивает его на части.
    /// Возвращает срезы исходного <paramref name="chunk"/> (а не plain), т.к. границы в байтах совпадают.
    /// </summary>
    public IReadOnlyList<byte[]> Split(byte[] chunk)
    {
        var plain = DecryptChunk(chunk);
        var boundaries = new List<int>();
        var pos = 0;

        while (pos < plain.Length)
        {
            int msgLen;
            var first = plain[pos];
            if (first == 0x7f)
            {
                if (pos + 4 > plain.Length)
                {
                    break;
                }

                msgLen = (plain[pos + 1] | (plain[pos + 2] << 8) | (plain[pos + 3] << 16)) * 4;
                pos += 4;
            }
            else
            {
                msgLen = first * 4;
                pos += 1;
            }

            if (msgLen == 0 || pos + msgLen > plain.Length)
            {
                break;
            }

            pos += msgLen;
            boundaries.Add(pos);
        }

        if (boundaries.Count <= 1)
        {
            return [chunk];
        }

        var parts = new List<byte[]>(boundaries.Count + 1);
        var prev = 0;
        foreach (var b in boundaries)
        {
            parts.Add(chunk[prev..b]);
            prev = b;
        }

        if (prev < chunk.Length)
        {
            parts.Add(chunk[prev..]);
        }

        return parts;
    }

    private byte[] DecryptChunk(byte[] chunk)
    {
        var plain = new byte[chunk.Length];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();

        var block = new byte[16];
        var counter = new byte[16];

        var blockIndex = streamOffset / 16;
        var blockOffset = (int)(streamOffset % 16);

        var pos = 0;
        while (pos < chunk.Length)
        {
            FillCounterAt(blockIndex, counter);
            enc.TransformBlock(counter, 0, 16, block, 0);

            var take = Math.Min(16 - blockOffset, chunk.Length - pos);
            for (var i = 0; i < take; i++)
            {
                plain[pos + i] = (byte)(chunk[pos + i] ^ block[blockOffset + i]);
            }

            pos += take;
            blockIndex++;
            blockOffset = 0;
        }

        streamOffset += chunk.Length;
        return plain;
    }

    /// <summary>
    /// Заполняет 16-байтовый CTR-counter для заданного индекса блока без аллокаций на каждый шаг.
    /// </summary>
    private void FillCounterAt(long blockIndex, byte[] counter16)
    {
        // Start from IV.
        Buffer.BlockCopy(iv, 0, counter16, 0, 16);
        var carry = (ulong)blockIndex;

        // Add blockIndex as a big-endian offset.
        for (var b = 15; b >= 0 && carry != 0; b--)
        {
            var sum = counter16[b] + (carry & 0xFF);
            counter16[b] = (byte)sum;
            carry = (carry >> 8) + (sum >> 8);
        }
    }
}
