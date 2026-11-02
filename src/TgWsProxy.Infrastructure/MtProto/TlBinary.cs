#nullable disable
using System.Text;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// TL (Type Language) binary serialization primitives.
/// https://core.telegram.org/mtproto/TL
/// </summary>
public static class TlBinary
{
    public const uint VectorCtor = 0x1CB5C415;
    public const uint NullCtor = 0x56730BCC; // null
    public const uint GzipPackedCtor = 0x3072CFA1;
    public const uint RpcResultCtor = 0xF35C6D01;
    public const uint RpcErrorCtor = 0x2144CA19;
    public const uint MsgContainerCtor = 0x73F1F8DC;
    public const uint MessageCtor = 0x5BB8E511;
    public const uint BoolTrue = 0x997275B5;
    public const uint BoolFalse = 0xBC799737;

    // ---- Writer helpers ----

    public static void WriteInt32(BinaryWriter w, int value) => w.Write(value);
    public static void WriteUInt32(BinaryWriter w, uint value) => w.Write(value);
    public static void WriteInt64(BinaryWriter w, long value) => w.Write(value);
    public static void WriteUInt64(BinaryWriter w, ulong value) => w.Write(value);
    public static void WriteDouble(BinaryWriter w, double value) => w.Write(value);

    public static void WriteBytes(BinaryWriter w, byte[] bytes)
    {
        if (bytes == null) { w.Write(0); return; }
        var length = bytes.Length;
        if (length < 254)
        {
            w.Write((byte)length);
        }
        else
        {
            w.Write((byte)254);
            w.Write((byte)(length & 0xFF));
            w.Write((byte)((length >> 8) & 0xFF));
            w.Write((byte)((length >> 16) & 0xFF));
            length += 3;
        }
        w.Write(bytes);
        while (++length % 4 != 0)
        {
            w.Write((byte)0);
        }
    }

    public static void WriteString(BinaryWriter w, string str)
    {
        if (str == null) { WriteBytes(w, null); return; }
        WriteBytes(w, Encoding.UTF8.GetBytes(str));
    }

    public static void WriteVector<T>(BinaryWriter w, T[] items, Action<BinaryWriter, T> writeItem)
    {
        w.Write(VectorCtor);
        w.Write(items?.Length ?? 0);
        if (items != null)
        {
            foreach (var item in items)
            {
                writeItem(w, item);
            }
        }
    }

    public static void WriteVector(BinaryWriter w, int count, Action<BinaryWriter, int> writeItem)
    {
        w.Write(VectorCtor);
        w.Write(count);
        for (var i = 0; i < count; i++)
        {
            writeItem(w, i);
        }
    }

    public static void WriteBool(BinaryWriter w, bool value) => w.Write(value ? BoolTrue : BoolFalse);

    public static void WriteConstructor(BinaryWriter w, uint ctorNb) => w.Write(ctorNb);

    // ---- Reader helpers ----

    public static int ReadInt32(BinaryReader r) => r.ReadInt32();
    public static uint ReadUInt32(BinaryReader r) => r.ReadUInt32();
    public static long ReadInt64(BinaryReader r) => r.ReadInt64();
    public static ulong ReadUInt64(BinaryReader r) => r.ReadUInt64();
    public static double ReadDouble(BinaryReader r) => r.ReadDouble();

    public static byte[] ReadBytes(BinaryReader r)
    {
        int length = r.ReadByte();
        if (length < 254)
        {
            var bytes = r.ReadBytes(length);
            while (++length % 4 != 0)
            {
                _ = r.ReadByte();
            }

            return bytes;
        }
        else
        {
            length = r.ReadByte() + (r.ReadByte() << 8) + (r.ReadByte() << 16);
            var bytes = r.ReadBytes(length);
            length += 3;
            while (++length % 4 != 0)
            {
                _ = r.ReadByte();
            }

            return bytes;
        }
    }

    public static string ReadString(BinaryReader r) => Encoding.UTF8.GetString(ReadBytes(r));

    public static bool ReadBool(BinaryReader r)
    {
        var val = r.ReadUInt32();
        return val switch
        {
            BoolTrue => true,
            BoolFalse => false,
            _ => throw new InvalidOperationException($"Invalid bool value 0x{val:X8}")
        };
    }

    public static uint ReadConstructor(BinaryReader r) => r.ReadUInt32();

    public static int ReadVectorCount(BinaryReader r)
    {
        var ctor = r.ReadUInt32();
        return ctor != VectorCtor ? throw new InvalidOperationException($"Expected Vector constructor, got 0x{ctor:X8}") : r.ReadInt32();
    }

    public static DateTime ReadTimestamp(BinaryReader r)
    {
        var unix = r.ReadInt32();
        return unix <= 0
            ? default
            : unix == int.MaxValue ? DateTime.MaxValue : new DateTime((unix + 62135596800L) * 10000000, DateTimeKind.Utc);
    }

    public static void WriteTimestamp(BinaryWriter w, DateTime dt)
    {
        var unix = dt.ToUniversalTime().Ticks / 10000000 - 62135596800L;
        w.Write((int)Math.Clamp(unix, 0, int.MaxValue));
    }
}

