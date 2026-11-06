using System;
using System.Linq;
using System.Security.Cryptography;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Регрессия деривации <c>tmp_aes_key</c>/<c>tmp_aes_iv</c> из MTProto-рукопожатия.
/// Исходный код копировал SHA1(new_nonce + new_nonce) в iv[8..28) со стартового смещения 8
/// (8 + 20 &gt; 20 байт → ArgumentException "Source array was not long enough"), что проявлялось
/// только когда реальный DC Telegram стал доступен по сети.
/// </summary>
public class MtProtoCryptoTests
{
    [Fact]
    public void ConstructTmpAESKeyIV_MatchesTelegramSpec()
    {
        var serverNonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var newNonce = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

        var (key, iv) = MtProtoCrypto.ConstructTmpAESKeyIV(serverNonce, newNonce);

        var hNewServer = SHA1.HashData(Concat(newNonce, serverNonce)); // SHA1(new_nonce + server_nonce)
        var hServerNew = SHA1.HashData(Concat(serverNonce, newNonce)); // SHA1(server_nonce + new_nonce)
        var hNewNew = SHA1.HashData(Concat(newNonce, newNonce));       // SHA1(new_nonce + new_nonce)

        // tmp_aes_key = SHA1(new_nonce + server_nonce) + substr(SHA1(server_nonce + new_nonce), 0, 12)
        var expectedKey = Concat(hNewServer, hServerNew.Take(12).ToArray());
        // tmp_aes_iv = substr(SHA1(server_nonce + new_nonce), 12, 8) + SHA1(new_nonce + new_nonce) + substr(new_nonce, 0, 4)
        var expectedIv = Concat(
            Concat(hServerNew.Skip(12).Take(8).ToArray(), hNewNew),
            newNonce.Take(4).ToArray());

        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedIv, iv);
    }

    /// <summary>
    /// Регрессия деривации aes_key/aes_iv в шифрованных сообщениях MTProto 2.0.
    /// Исходный код расшифровывал входящие сообщения с x=0 (как для client→server), хотя
    /// для server→client специфика требует x=8; в результате дешифровка давала мусор и
    /// десериализатор падал с «Cannot find type for ctor #…».
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void DeriveAesKeyIv_MatchesTelegramSpec(int x)
    {
        var authKey = Enumerable.Range(1, 256).Select(i => (byte)(i & 0xFF)).ToArray();
        var msgKey = Enumerable.Range(201, 16).Select(i => (byte)i).ToArray();

        using var sha256 = SHA256.Create();
        var (aesKey, aesIv) = MtProtoCrypto.DeriveAesKeyIv(msgKey, 0, authKey, x, sha256);

        // sha256_a = SHA256(msg_key + substr(auth_key, x, 36))
        var sha256A = SHA256.HashData(Concat(msgKey, Slice(authKey, x, 36)));
        // sha256_b = SHA256(substr(auth_key, 40+x, 36) + msg_key)
        var sha256B = SHA256.HashData(Concat(Slice(authKey, 40 + x, 36), msgKey));

        var expectedKey = Concat(
            Concat(sha256A.Take(8).ToArray(), sha256B.Skip(8).Take(16).ToArray()),
            sha256A.Skip(24).Take(8).ToArray());
        var expectedIv = Concat(
            Concat(sha256B.Take(8).ToArray(), sha256A.Skip(8).Take(16).ToArray()),
            sha256B.Skip(24).Take(8).ToArray());

        Assert.Equal(expectedKey, aesKey);
        Assert.Equal(expectedIv, aesIv);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] Slice(byte[] src, int offset, int length)
    {
        var r = new byte[length];
        Buffer.BlockCopy(src, offset, r, 0, length);
        return r;
    }
}