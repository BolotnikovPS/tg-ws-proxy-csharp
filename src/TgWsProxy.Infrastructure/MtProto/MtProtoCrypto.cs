#nullable disable
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Infrastructure.MtProto;

public static class MtProtoCrypto
{
    private static readonly Dictionary<long, (byte[] n, byte[] e)> PublicKeys = [];

    static MtProtoCrypto()
    {
        // Production key (D09D1D85DE64FD85)
        LoadPublicKey(@"-----BEGIN RSA PUBLIC KEY-----
MIIBCgKCAQEA6LszBcC1LGzyr992NzE0ieY+BSaOW622Aa9Bd4ZHLl+TuFQ4lo4g
5nKaMBwK/BIb9xUfg0Q29/2mgIR6Zr9krM7HjuIcCzFvDtr+L0GQjae9H0pRB2OO
62cECs5HKhT5DZ98K33vmWiLowc621dQuwKWSQKjWf50XYFw42h21P2KXUGyp2y/
+aEyZ+uVgLLQbRA1dEjSDZ2iGRy12Mk5gpYc397aYp438fsJoHIgJ2lgMv5h7WY9
t6N/byY9Nw9p21Og3AoXSL2q/2IJ1WRUhebgAdGVMlV1fkuOQoEzR7EdpqtQD9Cs
5+bfo3Nhmcyvk5ftB0WkJ9z6bNZ7yxrP8wIDAQAB
-----END RSA PUBLIC KEY-----");
        // Test key (B25898DF208D2603)
        LoadPublicKey(@"-----BEGIN RSA PUBLIC KEY-----
MIIBCgKCAQEAyMEdY1aR+sCR3ZSJrtztKTKqigvO/vBfqACJLZtS7QMgCGXJ6XIR
yy7mx66W0/sOFa7/1mAZtEoIokDP3ShoqF4fVNb6XeqgQfaUHd8wJpDWHcR2OFwv
plUUI1PLTktZ9uW2WE23b+ixNwJjJGwBDJPQEQFBE+vfmH0JP503wr5INS1poWg/
j25sIWeYPHYeOrFp/eXaqhISP6G+q2IeTaWTXpwZj4LzXq5YOpk4bYEQ6mvRq7D1
aHWfYmlEGepfaYR8Q0YqvvhYtMte3ITnuSJs171+GDqpdKcSwHnd6FudwGO4pcCO
j4WcDuXc2CTHgH8gFTNhp/Y8/SpDOhvn9QIDAQAB
-----END RSA PUBLIC KEY-----");
    }

    /// <summary>
    /// MTProto 2.0 cryptographic primitives: AES-256-IGE, SHA-256 key derivation, RSA, DH.
    /// Implemented from scratch following https://core.telegram.org/mtproto
    /// </summary>


    public static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    /// <summary>AES-256-IGE encrypt/decrypt (in-place on a copy, returns new array).</summary>
    public static byte[] AesIgeEncryptDecrypt(ReadOnlySpan<byte> input, byte[] aesKey, byte[] aesIv, bool encrypt)
    {
        if (input.Length % 16 != 0)
        {
            throw new ArgumentException("AES_IGE input size not divisible by 16");
        }

        // IGE шифрует каждый блок независимо (ECB): дефолтный режим .NET Aes — CBC, поэтому задаём
        // его явно, а экземпляр создаём локально (статический Aes не потокобезопасен).
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var aesCrypto = encrypt
            ? aes.CreateEncryptor(aesKey, null)
            : aes.CreateDecryptor(aesKey, null);

        var output = new byte[input.Length];
        var prevBytes = (byte[])aesIv.Clone();
        var span = MemoryMarshal.Cast<byte, long>(input);
        var sout = MemoryMarshal.Cast<byte, long>(output.AsSpan());
        var prev = MemoryMarshal.Cast<byte, long>(prevBytes.AsSpan());

        if (!encrypt) { (prev[2], prev[0]) = (prev[0], prev[2]); (prev[3], prev[1]) = (prev[1], prev[3]); }

        for (int i = 0, count = input.Length / 8; i < count;)
        {
            sout[i] = span[i] ^ prev[0];
            sout[i + 1] = span[i + 1] ^ prev[1];
            aesCrypto.TransformBlock(output, i * 8, 16, output, i * 8);
            prev[0] = sout[i] ^= prev[2];
            prev[1] = sout[i + 1] ^= prev[3];
            prev[2] = span[i++];
            prev[3] = span[i++];
        }
        return output;
    }

    /// <summary>Derive AES key and IV from msg_key and auth_key (MTProto 2.0)</summary>
    public static (byte[] aesKey, byte[] aesIv) DeriveAesKeyIv(byte[] msgKey, int msgKeyOffset, byte[] authKey, int x, SHA256 sha256)
    {
        byte[] aesKey = new byte[32], aesIv = new byte[32];
        sha256.TransformBlock(msgKey, msgKeyOffset, 16, null, 0);
        sha256.TransformFinalBlock(authKey, x, 36);
        var sha256A = sha256.Hash;
        sha256.Initialize();
        sha256.TransformBlock(authKey, 40 + x, 36, null, 0);
        sha256.TransformFinalBlock(msgKey, msgKeyOffset, 16);
        var sha256B = sha256.Hash;
        sha256.Initialize();

        Array.Copy(sha256A, 0, aesKey, 0, 8);
        Array.Copy(sha256B, 8, aesKey, 8, 16);
        Array.Copy(sha256A, 24, aesKey, 24, 8);
        Array.Copy(sha256B, 0, aesIv, 0, 8);
        Array.Copy(sha256A, 8, aesIv, 8, 16);
        Array.Copy(sha256B, 24, aesIv, 24, 8);
        return (aesKey, aesIv);
    }

    /// <summary>Compute msg_key from plaintext data (SHA-256 of data, take bytes 8..24)</summary>
    public static byte[] ComputeMsgKey(byte[] data, SHA256 sha256)
    {
        var hash = sha256.ComputeHash(data);
        var msgKey = new byte[16];
        Array.Copy(hash, 8, msgKey, 0, 16);
        return msgKey;
    }

    /// <summary>Generate random bytes</summary>
    public static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Rng.GetBytes(bytes);
        return bytes;
    }

    /// <summary>Big-endian to BigInteger</summary>
    public static BigInteger BigEndianToBigInteger(byte[] bytes)
    {
        var copy = bytes.Clone() as byte[];
        Array.Reverse(copy);
        return new BigInteger(copy, isUnsigned: true);
    }

    /// <summary>BigInteger to big-endian bytes (fixed length)</summary>
    public static byte[] BigIntegerToBigEndian(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length)
        {
            return bytes;
        }

        var result = new byte[length];
        if (bytes.Length < length)
        {
            Array.Copy(bytes, 0, result, length - bytes.Length, bytes.Length);
        }
        else
        {
            Array.Copy(bytes, bytes.Length - length, result, 0, length);
        }

        return result;
    }

    /// <summary>Load RSA public key from PEM</summary>
    public static (byte[] n, byte[] e) LoadRsaPublicKey(string pem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var param = rsa.ExportParameters(false);
        var n = param.Modulus!;
        if (n[0] == 0)
        {
            n = n[1..];
        }

        return (n, param.Exponent!);
    }

    /// <summary>RSA encrypt: m^e mod n</summary>
    public static byte[] RsaEncrypt(byte[] data, byte[] n, byte[] e)
    {
        var m = BigEndianToBigInteger(data);
        var modulus = BigEndianToBigInteger(n);
        var exponent = BigEndianToBigInteger(e);
        var result = BigInteger.ModPow(m, exponent, modulus);
        return BigIntegerToBigEndian(result, n.Length);
    }
    public static byte[] Sha1(byte[] data) => SHA1.HashData(data);
    public static byte[] Sha1(ReadOnlySpan<byte> data) => SHA1.HashData(data.ToArray());
    public static (byte[] p, byte[] q) FactorizePQ(byte[] pqBytes) { ulong pq = 0; foreach (var b in pqBytes) { pq = (pq << 8) | b; } var p = PQFactorize(pq); var q = pq / p; return (ToBigEndianBytes(p), ToBigEndianBytes(q)); }
    private static ulong PQFactorize(ulong pq) { if (pq % 2 == 0) { return 2; } for (ulong d = 3; d * d <= pq; d += 2) { if (pq % d == 0) { return d; } } throw new InvalidOperationException("factor fail"); }
    private static byte[] ToBigEndianBytes(ulong v) { var b = new byte[8]; for (var i = 7; i >= 0; i--) { b[i] = (byte)(v & 0xFF); v >>= 8; } var s = 0; while (s < b.Length - 1 && b[s] == 0) { s++; } var r = new byte[b.Length - s]; Array.Copy(b, s, r, 0, r.Length); return r; }
    public static byte[] ModPowBytes(byte[] baseBytes, byte[] expBytes, byte[] modBytes) { var b = BigEndianToBigInteger(baseBytes); var e = BigEndianToBigInteger(expBytes); var m = BigEndianToBigInteger(modBytes); var r = BigInteger.ModPow(b, e, m); return BigIntegerToBigEndian(r, modBytes.Length); }
    public static void CheckGoodPrime(BigInteger p) { if (p.GetBitLength() != 2048)
        {
            throw new InvalidOperationException("bad prime");
        }
    }
    public static void CheckGoodGaAndGb(BigInteger g, BigInteger p) { if (g <= 1 || g >= p - 1)
        {
            throw new InvalidOperationException("bad ga/gb");
        }
    }
    public static (byte[] key, byte[] iv) ConstructTmpAESKeyIV(byte[] serverNonce, byte[] newNonce) { using var sha1 = SHA1.Create(); var k = new byte[32]; var iv = new byte[32]; sha1.TransformBlock(newNonce, 0, 32, null, 0); sha1.TransformFinalBlock(serverNonce, 0, 16); var h1 = sha1.Hash; sha1.Initialize(); sha1.TransformBlock(serverNonce, 0, 16, null, 0); sha1.TransformFinalBlock(newNonce, 0, 32); var h2 = sha1.Hash; sha1.Initialize(); Array.Copy(h1, 0, k, 0, 20); Array.Copy(h2, 0, k, 20, 12); Array.Copy(h2, 12, iv, 0, 8); sha1.TransformBlock(newNonce, 0, 32, null, 0); sha1.TransformFinalBlock(newNonce, 0, 32); var h3 = sha1.Hash; sha1.Initialize(); Array.Copy(h3, 8, iv, 8, 20); Array.Copy(newNonce, 0, iv, 28, 4); return (k, iv); }
    public static byte[] DecryptServerDHParams(byte[] enc, byte[] key, byte[] iv) => AesIgeEncryptDecrypt(enc, key, iv, false);
    public static byte[] EncryptClientDHInnerData(byte[] data, byte[] key, byte[] iv) => AesIgeEncryptDecrypt(data, key, iv, true);
    public static byte[] ComputeAuthKey(BigInteger gA, byte[] bBytes, BigInteger dhPrime) { var b = BigEndianToBigInteger(bBytes); var gab = BigInteger.ModPow(gA, b, dhPrime); var ak = gab.ToByteArray(isUnsigned: true, isBigEndian: true); if (ak.Length < 256) { var p = new byte[256]; Array.Copy(ak, 0, p, 256 - ak.Length, ak.Length); ak = p; } return ak; }
    public static byte[] ComputeNewNonceHash1(byte[] newNonce, int number) { var d = new byte[33]; Array.Copy(newNonce, 0, d, 0, 32); d[32] = (byte)number; var h = SHA1.HashData(d); var r = new byte[16]; Array.Copy(h, 4, r, 0, 16); return r; }
    public static void LoadPublicKey(string pem)
    {
        var lines = pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim()).ToArray();
        var der = Convert.FromBase64String(string.Join("", lines));
        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(der, out _);
        var rsaParam = rsa.ExportParameters(false);
        if (rsaParam.Modulus![0] == 0)
        {
            rsaParam.Modulus = rsaParam.Modulus[1..];
        }

        var publicKey = new RSAPublicKey { n = rsaParam.Modulus, e = rsaParam.Exponent! };
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.WriteTLObject(publicKey);
        var bareData = ms.ToArray();
        var hash = SHA1.HashData(bareData.AsSpan(4, bareData.Length - 4));
        var fingerprint = BinaryPrimitives.ReadInt64LittleEndian(hash.AsSpan(12));
        PublicKeys[fingerprint] = (rsaParam.Modulus, rsaParam.Exponent!);
    }

    public static bool HasPublicKey(long fingerprint) => PublicKeys.ContainsKey(fingerprint);

    public static (byte[] n, byte[] e) GetPublicKey(long fingerprint) => PublicKeys[fingerprint];


}

