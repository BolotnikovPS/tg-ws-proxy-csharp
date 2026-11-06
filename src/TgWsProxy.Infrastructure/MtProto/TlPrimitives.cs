#nullable disable
using System.Security.Cryptography;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Нетипизированный результат TL vector#1cb5c415 — обёртка над уже прочитанными IObject[].
/// Возвращается <see cref="Serialization.ReadTLObject"/> на VectorCtor (например, для users.getUsers = Vector&lt;User&gt;);
/// элементы десериализуются в конкретные типы (которые зарегистрированы в Layer.Table).
/// </summary>
public sealed class TLVector : IObject
{
    public IObject[] Items;
}

public struct Int128
{
    public byte[] raw;

    public Int128(BinaryReader reader) => raw = reader.ReadBytes(16);
    public Int128(byte[] data) => raw = data;
    public Int128(RandomNumberGenerator rng) => rng.GetBytes(raw = new byte[16]);
    public static bool operator ==(Int128 left, Int128 right) { for (var i = 0; i < 16; i++) if (left.raw[i] != right.raw[i]) return false; return true; }
    public static bool operator !=(Int128 left, Int128 right) { for (var i = 0; i < 16; i++) if (left.raw[i] != right.raw[i]) return true; return false; }
    public override readonly bool Equals(object obj) => obj is Int128 other && this == other;
    public override readonly int GetHashCode() => BitConverter.ToInt32(raw, 0);
    public override readonly string ToString() => Convert.ToHexString(raw);
    public static implicit operator byte[](Int128 int128) => int128.raw;
}

public struct Int256
{
    public byte[] raw;

    public Int256(BinaryReader reader) => raw = reader.ReadBytes(32);
    public Int256(byte[] data) => raw = data;
    public Int256(RandomNumberGenerator rng) => rng.GetBytes(raw = new byte[32]);
    public static bool operator ==(Int256 left, Int256 right) { for (var i = 0; i < 32; i++) if (left.raw[i] != right.raw[i]) return false; return true; }
    public static bool operator !=(Int256 left, Int256 right) { for (var i = 0; i < 32; i++) if (left.raw[i] != right.raw[i]) return true; return false; }
    public override readonly bool Equals(object obj) => obj is Int256 other && this == other;
    public override readonly int GetHashCode() => BitConverter.ToInt32(raw, 0);
    public override readonly string ToString() => Convert.ToHexString(raw);
    public static implicit operator byte[](Int256 int256) => int256.raw;
}

/// <summary>Упаковка результата boolTrue#997275b5 / boolFalse#bc799737 для IMethod&lt;bool&gt;.</summary>
public sealed class BoolValue : IObject
{
    public bool Value;
}