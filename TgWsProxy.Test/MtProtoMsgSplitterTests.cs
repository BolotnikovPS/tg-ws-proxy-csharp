#nullable enable

using System.Security.Cryptography;
using System.Linq;
using TgWsProxy.Infrastructure;

namespace TgWsProxy.Test;

public class MtProtoMsgSplitterTests
{
    [Fact]
    public void Split_TwoMessages_SplitsIntoTwoParts()
    {
        var init = BuildInitForTest();
        var msg1 = BuildSimpleMessage(firstLenUnit: 1, payloadByte: 0xAA); // total 1(header)+4(payload)=5
        var msg2 = BuildSimpleMessage(firstLenUnit: 1, payloadByte: 0xBB); // total 5
        var plain = msg1.Concat(msg2).ToArray();

        var chunk = EncryptCtr(init, plain, offset: 64);

        var splitter = new MtProtoMsgSplitter(init);
        var parts = splitter.Split(chunk);

        Assert.Equal(2, parts.Count);
        Assert.Equal(5, parts[0].Length);
        Assert.Equal(5, parts[1].Length);
        Assert.Equal(chunk[..5], parts[0]);
        Assert.Equal(chunk[5..], parts[1]);
    }

    [Fact]
    public void Split_SingleMessage_ReturnsOnePart()
    {
        var init = BuildInitForTest();
        var msg = BuildSimpleMessage(firstLenUnit: 2, payloadByte: 0xCC); // total 1+8=9
        var plain = msg;

        var chunk = EncryptCtr(init, plain, offset: 64);
        var splitter = new MtProtoMsgSplitter(init);

        var parts = splitter.Split(chunk);
        Assert.Single(parts);
        Assert.Equal(plain.Length, parts[0].Length);
        Assert.Equal(chunk, parts[0]);
    }

    [Fact]
    public void Split_MixedSimpleAndExtendedLength_SplitsCorrectly()
    {
        var init = BuildInitForTest();
        var msg1 = BuildSimpleMessage(firstLenUnit: 1, payloadByte: 0x11); // total 5
        var msg2 = BuildExtendedMessage(wordsLen: 1, payloadByte: 0x22); // total 4(header)+4(payload)=8
        var plain = msg1.Concat(msg2).ToArray();

        var chunk = EncryptCtr(init, plain, offset: 64);

        var splitter = new MtProtoMsgSplitter(init);
        var parts = splitter.Split(chunk);

        Assert.Equal(2, parts.Count);
        Assert.Equal(5, parts[0].Length);
        Assert.Equal(8, parts[1].Length);
        Assert.Equal(chunk[..5], parts[0]);
        Assert.Equal(chunk[5..], parts[1]);
    }

    private static byte[] BuildInitForTest()
    {
        // init layout used by splitter:
        // key = init[8..39] (32 bytes)
        // iv  = init[40..55] (16 bytes)
        var init = new byte[64];

        for (var i = 0; i < 32; i++)
        {
            init[8 + i] = (byte)(0x10 + i);
        }

        for (var i = 0; i < 16; i++)
        {
            init[40 + i] = (byte)(0x80 + i);
        }

        return init;
    }

    private static byte[] BuildSimpleMessage(int firstLenUnit, byte payloadByte)
    {
        // plain format:
        // [lenUnit (1 byte)] + [payload (lenUnit*4 bytes)]
        var msgLen = firstLenUnit * 4;
        var data = new byte[1 + msgLen];
        data[0] = (byte)firstLenUnit;
        for (var i = 0; i < msgLen; i++)
        {
            data[1 + i] = payloadByte;
        }
        return data;
    }

    private static byte[] BuildExtendedMessage(int wordsLen, byte payloadByte)
    {
        // format:
        // [0x7f] + [lenLow  (1 byte)] + [lenMid (1 byte)] + [lenHigh (1 byte)] + payload
        var msgLen = wordsLen * 4;
        var data = new byte[4 + msgLen];
        data[0] = 0x7f;
        data[1] = (byte)(wordsLen & 0xFF);
        data[2] = (byte)((wordsLen >> 8) & 0xFF);
        data[3] = (byte)((wordsLen >> 16) & 0xFF);
        for (var i = 0; i < msgLen; i++)
        {
            data[4 + i] = payloadByte;
        }
        return data;
    }

    /// <summary>
    /// Encrypts plaintext using AES-CTR where counter blocks are AES-ECB(key) of (iv + blockIndex offset).
    /// This matches the splitter's FillCounterAt + TransformBlock usage.
    /// </summary>
    private static byte[] EncryptCtr(byte[] init, byte[] plain, long offset)
    {
        var key = init.AsSpan(8, 32).ToArray();
        var iv = init.AsSpan(40, 16).ToArray();

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var enc = aes.CreateEncryptor();

        var counter = new byte[16];
        var block = new byte[16];

        var cipher = new byte[plain.Length];

        var blockIndex = offset / 16;
        var blockOffset = (int)(offset % 16);

        var pos = 0;
        while (pos < plain.Length)
        {
            FillCounterAt(iv, blockIndex, counter);
            enc.TransformBlock(counter, 0, 16, block, 0);

            var take = Math.Min(16 - blockOffset, plain.Length - pos);
            for (var i = 0; i < take; i++)
            {
                cipher[pos + i] = (byte)(plain[pos + i] ^ block[blockOffset + i]);
            }

            pos += take;
            blockIndex++;
            blockOffset = 0;
        }

        return cipher;
    }

    private static void FillCounterAt(byte[] iv, long blockIndex, byte[] counter16)
    {
        Buffer.BlockCopy(iv, 0, counter16, 0, 16);
        var carry = (ulong)blockIndex;

        // big-endian offset addition (matches splitter)
        for (var b = 15; b >= 0 && carry != 0; b--)
        {
            var sum = counter16[b] + (carry & 0xFF);
            counter16[b] = (byte)sum;
            carry = (carry >> 8) + (sum >> 8);
        }
    }
}

