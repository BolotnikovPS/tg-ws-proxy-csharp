#nullable enable

using System.Security.Cryptography;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Дешифрует фрагменты MTProto (AES-CTR) и при наличии нескольких сообщений разбивает chunk на части.
/// </summary>
internal sealed class MtProtoMsgSplitter(byte[] init)
{
    private readonly byte[] _key = init.AsSpan(8, 32).ToArray();
    private readonly byte[] _iv = init.AsSpan(40, 16).ToArray();
    private long _streamOffset = 64;
    private readonly List<byte> _cipherBuf = [];
    private readonly List<byte> _plainBuf = [];

    /// <summary>
    /// Расшифровывает chunk и, при наличии нескольких MTProto-сообщений, разбивает его на части.
    /// Возвращает срезы исходного <paramref name="chunk"/> (а не plain), т.к. границы в байтах совпадают.
    /// </summary>
    public IReadOnlyList<byte[]> Split(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            return [];
        }

        _cipherBuf.AddRange(chunk);
        var plain = DecryptChunk(chunk);
        _plainBuf.AddRange(plain);

        var parts = new List<byte[]>();

        while (_plainBuf.Count > 0)
        {
            var packetLen = NextPacketLenFromPlain();
            if (packetLen is null)
            {
                break;
            }

            if (packetLen <= 0)
            {
                // Invalid packet, return everything as single part
                parts.Add([.. _cipherBuf]);
                _cipherBuf.Clear();
                _plainBuf.Clear();
                return parts;
            }

            if (_cipherBuf.Count < packetLen.Value)
            {
                break;
            }

            parts.Add([.. _cipherBuf.GetRange(0, packetLen.Value)]);
            _cipherBuf.RemoveRange(0, packetLen.Value);
            _plainBuf.RemoveRange(0, packetLen.Value);
        }

        return parts.Count > 0 ? parts : [chunk];
    }

    /// <summary>
    /// Возвращает оставшиеся в буфере данные (вызывается при закрытии соединения).
    /// </summary>
    public IReadOnlyList<byte[]> Flush()
    {
        if (_cipherBuf.Count == 0)
        {
            return [];
        }

        var tail = _cipherBuf.ToArray();
        _cipherBuf.Clear();
        _plainBuf.Clear();
        return [tail];
    }

    private byte[] DecryptChunk(byte[] chunk)
    {
        var plain = new byte[chunk.Length];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = _key;
        using var enc = aes.CreateEncryptor();

        var block = new byte[16];
        var counter = new byte[16];

        var blockIndex = _streamOffset / 16;
        var blockOffset = (int)(_streamOffset % 16);

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

        _streamOffset += chunk.Length;
        return plain;
    }

    /// <summary>
    /// Определяет длину следующего MTProto пакета из расшифрованных данных.
    /// </summary>
    /// <returns>Длина пакета, 0 если ошибка, или null если недостаточно данных.</returns>
    private int? NextPacketLenFromPlain()
    {
        if (_plainBuf.Count < 1)
        {
            return null;
        }

        var first = _plainBuf[0];
        int headerLen;
        int payloadLen;

        if (first is 0x7f or 0xFF)
        {
            // Extended format: 4-byte header
            if (_plainBuf.Count < 4)
            {
                return null;
            }

            payloadLen = (_plainBuf[1] | (_plainBuf[2] << 8) | (_plainBuf[3] << 16)) * 4;
            headerLen = 4;
        }
        else
        {
            // Short format: 1-byte header
            payloadLen = (first & 0x7F) * 4;
            headerLen = 1;
        }

        if (payloadLen <= 0)
        {
            return 0;
        }

        var packetLen = headerLen + payloadLen;
        if (_plainBuf.Count < packetLen)
        {
            return null;
        }

        return packetLen;
    }

    /// <summary>
    /// Заполняет 16-байтовый CTR-counter для заданного индекса блока без аллокаций на каждый шаг.
    /// </summary>
    private void FillCounterAt(long blockIndex, byte[] counter16)
    {
        // Start from IV.
        Buffer.BlockCopy(_iv, 0, counter16, 0, 16);
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
