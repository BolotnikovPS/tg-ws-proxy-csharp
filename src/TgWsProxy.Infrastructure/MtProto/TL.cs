#nullable disable
using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TgWsProxy.Infrastructure.MtProto.TL;

#pragma warning disable IDE1006 // Naming Styles

namespace TgWsProxy.Infrastructure.MtProto;

#if MTPG
	public interface IObject { void WriteTL(BinaryWriter writer); }
#else
public interface IObject { }
#endif
public interface IMethod<out TResult> : IObject { }
public interface IPeerResolver { IPeerInfo UserOrChat(Peer peer); }

[AttributeUsage(AttributeTargets.Class)]
public sealed class TLDefAttribute(uint ctorNb) : Attribute
{
    public readonly uint CtorNb = ctorNb;
    public int inheritAt = -1;
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class IfFlagAttribute(int bit) : Attribute
{
    public readonly int Bit = bit;
}

public sealed class RpcException(int code, string message, int x = -1) : Exception(message)
{
    public readonly int Code = code;
    /// <summary>The value of X in the message, -1 if no variable X was found</summary>
    public readonly int X = x;
    public override string ToString() { var str = base.ToString(); return str.Insert(str.IndexOf(':') + 1, " " + Code); }
}

public sealed partial class ReactorError : IObject
{
    public Exception Exception;
    public void WriteTL(BinaryWriter writer) => throw new NotSupportedException();
}

/// <summary>
/// Нетипизированный результат TL vector#1cb5c415 — обёртка над уже прочитанными IObject[].
/// Возвращается <see cref="ReadTLObject"/> на VectorCtor (например, для users.getUsers = Vector&lt;User&gt;);
/// элементы десериализуются в конкретные типы (которые зарегистрированы в Layer.Table).
/// </summary>
public sealed class TLVector : IObject
{
    public IObject[] Items;
}

public static class Serialization
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static byte[] ToBytes<T>(this T obj) where T : IObject
    {
        using var ms = new MemoryStream(384);
        using var writer = new BinaryWriter(ms);
        writer.WriteTLObject(obj);
        return ms.ToArray();
    }

    public static void WriteTLObject<T>(this BinaryWriter writer, T obj) where T : IObject
    {
        if (obj == null) { writer.WriteTLNull(typeof(T)); return; }
#if MTPG
			obj.WriteTL(writer);
#else
        var type = obj.GetType();
        var tlDef = type.GetCustomAttribute<TLDefAttribute>();
        var ctorNb = tlDef.CtorNb;
        writer.Write(ctorNb);
        var fields = GetTLFields(type, tlDef);
        ulong flags = 0;
        IfFlagAttribute ifFlag;
        foreach (var field in fields)
        {
            if (((ifFlag = field.GetCustomAttribute<IfFlagAttribute>()) != null) && (flags & (1UL << ifFlag.Bit)) == 0) continue;
            var value = field.GetValue(obj);
            writer.WriteTLValue(value, field.FieldType);
            if (field.FieldType.IsEnum)
            {
                if (field.Name == "flags") flags = (uint)value;
                else if (field.Name == "flags2") flags |= (ulong)(uint)value << 32;
            }
        }
#endif
    }

    public static IObject ReadTLObject(this BinaryReader reader, uint ctorNb = 0)
    {
        if (ctorNb == 0) ctorNb = reader.ReadUInt32();
#if MTPG
			if (!Layer.Table.TryGetValue(ctorNb, out var ctor))
				throw new InvalidOperationException($"Cannot find type for ctor #{ctorNb:x}");
			return ctor?.Invoke(reader);
#else
        if (ctorNb == Layer.GZipedCtor) return (IObject)reader.ReadTLGzipped(typeof(IObject));
        if (ctorNb == Layer.VectorCtor)
        {
            var count = reader.ReadInt32();
            var items = new IObject[count];
            for (var i = 0; i < count; i++)
                items[i] = reader.ReadTLObject();
            return new TLVector { Items = items };
        }
        // boolTrue#997275b5 / boolFalse#bc799737 — результат IMethod<bool> (не входят в Layer.Table).
        if (ctorNb is 0x997275B5 or 0xBC799737)
        {
            return new BoolValue { Value = ctorNb == 0x997275B5 };
        }
        if (!Layer.Table.TryGetValue(ctorNb, out var type))
            throw new InvalidOperationException($"Cannot find type for ctor #{ctorNb:x}");
        if (type == null) return null; // nullable ctor (class meaning is associated with null)
        var tlDef = type.GetCustomAttribute<TLDefAttribute>();
        var obj = Activator.CreateInstance(type, true);
        var fields = GetTLFields(type, tlDef);
        ulong flags = 0;
        IfFlagAttribute ifFlag;
        foreach (var field in fields)
        {
            if (((ifFlag = field.GetCustomAttribute<IfFlagAttribute>()) != null) && (flags & (1UL << ifFlag.Bit)) == 0) continue;
            var value = reader.ReadTLValue(field.FieldType);
            field.SetValue(obj, value);
            if (field.FieldType.IsEnum)
            {
                if (field.Name == "flags") flags = (uint)value;
                else if (field.Name == "flags2") flags |= (ulong)(uint)value << 32;
            }
        }
        return (IObject)obj;
#endif
    }

#if !MTPG
    private static IEnumerable<FieldInfo> GetTLFields(Type type, TLDefAttribute tlDef)
    {
        if (!(tlDef?.inheritAt >= 0)) return type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        if (type.IsAbstract || type.BaseType == typeof(IObject)) return fields;
        var subfields = GetTLFields(type.BaseType, type.BaseType.GetCustomAttribute<TLDefAttribute>());
        return fields.Take(tlDef.inheritAt).Concat(subfields).Concat(fields.Skip(tlDef.inheritAt));
    }
#else
		public static IMethod<X> ReadTLMethod<X>(this BinaryReader reader)
		{
			uint ctorNb = reader.ReadUInt32();
			if (!Layer.Methods.TryGetValue(ctorNb, out var ctor))
				throw new Exception($"Cannot find method for ctor #{ctorNb:x}");
			var method = ctor?.Invoke(reader);
			if (method is IMethod<bool> && typeof(X) == typeof(object))
				method = new BoolMethod { query = method };
			return (IMethod<X>)method;
		}
#endif

    internal static void WriteTLValue(this BinaryWriter writer, object value, Type valueType)
    {
        if (value == null)
        {
            writer.WriteTLNull(valueType);
            return;
        }
        var type = value.GetType();
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Int32: writer.Write((int)value); break;
            case TypeCode.Int64: writer.Write((long)value); break;
            case TypeCode.UInt32: writer.Write((uint)value); break;
            case TypeCode.UInt64: writer.Write((ulong)value); break;
            case TypeCode.Double: writer.Write((double)value); break;
            case TypeCode.String: writer.WriteTLString((string)value); break;
            case TypeCode.Boolean: writer.Write((bool)value ? 0x997275B5 : 0xBC799737); break;
            case TypeCode.DateTime: writer.WriteTLStamp((DateTime)value); break;
            case TypeCode.Object:
                if (type.IsArray)
                {
                    if (value is byte[] bytes)
                        writer.WriteTLBytes(bytes);
                    else
                        writer.WriteTLVector((Array)value);
                }
                else if (value is IObject tlObject)
                {
                    WriteTLObject(writer, tlObject);
                }
                else if (value is List<_Message> messages)
                {
                    writer.WriteTLMessages(messages);
                }
                else if (value is Int128 int128)
                {
                    writer.Write(int128);
                }
                else if (value is Int256 int256)
                {
                    writer.Write(int256);
                }
                else if (type.IsEnum) // needed for Mono (enums in generic types are seen as TypeCode.Object)
                {
                    writer.Write((uint)value);
                }
                else
                {
                    ShouldntBeHere();
                }

                break;
            default:
                ShouldntBeHere();
                break;
        }
    }

    internal static object ReadTLValue(this BinaryReader reader, Type type)
    {
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Int32: return reader.ReadInt32();
            case TypeCode.Int64: return reader.ReadInt64();
            case TypeCode.UInt32: return reader.ReadUInt32();
            case TypeCode.UInt64: return reader.ReadUInt64();
            case TypeCode.Double: return reader.ReadDouble();
            case TypeCode.String: return reader.ReadTLString();
            case TypeCode.DateTime: return reader.ReadTLStamp();
            case TypeCode.Boolean:
                return reader.ReadUInt32() switch
                {
                    0x997275b5 => true,
                    0xbc799737 => false,
                    Layer.RpcErrorCtor => reader.ReadTLObject(Layer.RpcErrorCtor),
                    var value => throw new FormatException($"Invalid boolean value #{value:x}")
                };
            case TypeCode.Object:
                if (type.IsArray)
                {
                    return type == typeof(byte[]) ? reader.ReadTLBytes() : (object)reader.ReadTLVector(type);
                }
                else if (type == typeof(List<_Message>))
                {
                    return reader.ReadTLMessages();
                }
                else
                {
                    return type == typeof(Int128)
                        ? new Int128(reader)
                        : type == typeof(Int256)
                                            ? new Int256(reader)
                                            : type == typeof(Dictionary<long, User>)
                                                                ? reader.ReadTLDictionary<User>()
                                                                : type == typeof(Dictionary<long, ChatBase>) ? reader.ReadTLDictionary<ChatBase>() : reader.ReadTLObject();
                }

            default:
                ShouldntBeHere();
                return null;
        }
    }

    internal static void WriteTLMessages(this BinaryWriter writer, List<_Message> messages)
    {
        writer.Write(messages.Count);
        foreach (var msg in messages)
        {
            writer.Write(msg.msg_id);
            writer.Write(msg.seqno);
            var patchPos = writer.BaseStream.Position;
            writer.Write(0);                                                // patched below
                                                                            // logging removed
            writer.WriteTLObject(msg.body);
            writer.BaseStream.Position = patchPos;
            writer.Write((int)(writer.BaseStream.Length - patchPos - 4));   // patch bytes field
            _ = writer.Seek(0, SeekOrigin.End);
        }
    }

    internal static List<_Message> ReadTLMessages(this BinaryReader reader)
    {
        // msg_container#73f1f8dc messages:vector<%Message> — bare vector: count then
        // per-message struct msg_id:long seqno:int bytes:int body:Object (no ctor id).
        var count = reader.ReadInt32();
        var messages = new List<_Message>(count);
        for (var i = 0; i < count; i++)
        {
            var msg = new _Message();
            msg.msg_id = reader.ReadInt64();
            msg.seqno = reader.ReadInt32();
            msg.bytes = reader.ReadInt32();
            msg.body = reader.ReadTLObject();
            messages.Add(msg);
        }
        return messages;
    }

    internal static void WriteTLVector(this BinaryWriter writer, Array array)
    {
        writer.Write(Layer.VectorCtor);
        if (array == null) { writer.Write(0); return; }
        var count = array.Length;
        writer.Write(count);
        var elementType = array.GetType().GetElementType();
        for (var i = 0; i < count; i++)
            writer.WriteTLValue(array.GetValue(i), elementType);
    }

    internal static void WriteTLRawVector(this BinaryWriter writer, Array array, int elementSize)
    {
        var startPos = writer.BaseStream.Position;
        var count = array.Length;
        var elementType = array.GetType().GetElementType();
        for (var i = count - 1; i >= 0; i--)
        {
            writer.BaseStream.Position = startPos + (i * elementSize);
            writer.WriteTLValue(array.GetValue(i), elementType);
        }
        writer.BaseStream.Position = startPos;
        writer.Write(count);
        writer.BaseStream.Position = startPos + (count * elementSize) + 4;
    }

    internal static List<T> ReadTLRawVector<T>(this BinaryReader reader, uint ctorNb)
    {
        var count = reader.ReadInt32();
        var list = new List<T>(count);
        for (var i = 0; i < count; i++)
            list.Add((T)reader.ReadTLObject(ctorNb));
        return list;
    }

    internal static T[] ReadTLVector<T>(this BinaryReader reader)
    {
        var elementType = typeof(T);
        if (reader.ReadUInt32() is not Layer.VectorCtor and uint ctorNb)
            throw new FormatException($"Cannot deserialize {elementType.Name}[] with ctor #{ctorNb:x}");
        var count = reader.ReadInt32();
        var array = new T[count];
        for (var i = 0; i < count; i++)
            array[i] = (T)reader.ReadTLValue(elementType);
        return array;
    }

    internal static Array ReadTLVector(this BinaryReader reader, Type type)
    {
        var elementType = type.GetElementType();
        var ctorNb = reader.ReadUInt32();
        if (ctorNb == Layer.VectorCtor)
        {
            var count = reader.ReadInt32();
            var array = (Array)Activator.CreateInstance(type, count);
            if (elementType.IsEnum)
            {
                for (var i = 0; i < count; i++)
                    array.SetValue(Enum.ToObject(elementType, reader.ReadTLValue(elementType)), i);
            }
            else
            {
                for (var i = 0; i < count; i++)
                    array.SetValue(reader.ReadTLValue(elementType), i);
            }

            return array;
        }
        else if (ctorNb < 1024 && !elementType.IsAbstract && elementType.GetCustomAttribute<TLDefAttribute>() is TLDefAttribute attr)
        {
            var count = (int)ctorNb;
            var array = (Array)Activator.CreateInstance(type, count);
            for (var i = 0; i < count; i++)
                array.SetValue(reader.ReadTLObject(attr.CtorNb), i);
            return array;
        }
        else
        {
            throw new FormatException($"Cannot deserialize {type.Name} with ctor #{ctorNb:x}");
        }
    }

    internal static Dictionary<long, T> ReadTLDictionary<T>(this BinaryReader reader) where T : class, IPeerInfo
    {
        var ctorNb = reader.ReadUInt32();
        if (ctorNb != Layer.VectorCtor)
            throw new FormatException($"Cannot deserialize Vector<{typeof(T).Name}> with ctor #{ctorNb:x}");
        var count = reader.ReadInt32();
        var dict = new Dictionary<long, T>(count);
        for (var i = 0; i < count; i++)
        {
            var obj = reader.ReadTLObject();
            if (obj is T value) dict[value.ID] = value;
            else if (obj is UserEmpty ue) dict[ue.id] = null;
            else throw new InvalidCastException($"ReadTLDictionary got '{obj?.GetType().Name}' instead of '{typeof(T).Name}'");
        }
        return dict;
    }

    internal static void WriteTLStamp(this BinaryWriter writer, DateTime datetime)
        => writer.Write((int)Math.Min(Math.Max((datetime.ToUniversalTime().Ticks / 10000000) - 62135596800L, 0), int.MaxValue));

    internal static DateTime ReadTLStamp(this BinaryReader reader) => reader.ReadInt32() switch
    {
        <= 0 => default,
        int.MaxValue => DateTime.MaxValue,
        int unixstamp => new((unixstamp + 62135596800L) * 10000000, DateTimeKind.Utc)
    };

    internal static void WriteTLString(this BinaryWriter writer, string str)
    {
        if (str == null)
            writer.Write(0);
        else
            writer.WriteTLBytes(Encoding.UTF8.GetBytes(str));
    }

    internal static string ReadTLString(this BinaryReader reader)
        => Encoding.UTF8.GetString(reader.ReadTLBytes());

    internal static void WriteTLBytes(this BinaryWriter writer, byte[] bytes)
    {
        if (bytes == null) { writer.Write(0); return; }
        var length = bytes.Length;
        if (length < 254)
        {
            writer.Write((byte)length);
        }
        else
        {
            writer.Write((length << 8) | 254);
            length += 3;
        }
        writer.Write(bytes);
        while (++length % 4 != 0) writer.Write((byte)0);
    }

    internal static byte[] ReadTLBytes(this BinaryReader reader)
    {
        byte[] bytes;
        int length = reader.ReadByte();
        if (length < 254)
        {
            bytes = reader.ReadBytes(length);
        }
        else
        {
            length = reader.ReadUInt16() + (reader.ReadByte() << 16);
            bytes = reader.ReadBytes(length);
            length += 3;
        }
        while (++length % 4 != 0) _ = reader.ReadByte();
        return bytes;
    }

    internal static void WriteTLNull(this BinaryWriter writer, Type type)
    {
        if (type == typeof(string)) { }
        else if (!type.IsArray)
        {
            writer.Write(Layer.Nullables.TryGetValue(type, out var nullCtor) ? nullCtor : Layer.NullCtor);
            return;
        }
        else if (type != typeof(byte[]))
        {
            writer.Write(Layer.VectorCtor); // not raw bytes but a vector => needs a VectorCtor
        }

        writer.Write(0);    // null arrays/strings are serialized as empty
    }

    internal static object ReadTLGzipped(this BinaryReader reader, Type type)
    {
        using var gzipReader = new BinaryReader(new GZipStream(new MemoryStream(reader.ReadTLBytes()), CompressionMode.Decompress));
        return gzipReader.ReadTLValue(type);
    }

    internal static bool ReadTLBool(this BinaryReader reader) => reader.ReadUInt32() switch
    {
        0x997275b5 => true,
        0xbc799737 => false,
        var value => throw new FormatException($"Invalid boolean value #{value:x}")
    };

#if DEBUG
    private static void ShouldntBeHere() => System.Diagnostics.Debugger.Break();
#else
		private static void ShouldntBeHere() => throw new NotImplementedException("You've reached an unexpected point in code");
#endif
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

public sealed partial class UpdateAffectedMessages : Update // auto-generated for OnOwnUpdates in case of such API call result
{
    public long mbox_id;
    public int pts;
    public int pts_count;
    public override (long, int, int) GetMBox() => (mbox_id, pts, pts_count);
#if MTPG
		public override void WriteTL(BinaryWriter writer) => throw new NotSupportedException();
#endif
}

// Below TL types are commented "parsed manually" from https://github.com/telegramdesktop/tdesktop/blob/dev/Telegram/Resources/tl/mtproto.tl

[TLDef(0x7A19CB76)] //RSA_public_key#7a19cb76 n:bytes e:bytes = RSAPublicKey
public sealed partial class RSAPublicKey : IObject
{
    public byte[] n;
    public byte[] e;
}

[TLDef(0xF35C6D01)] //rpc_result#f35c6d01 req_msg_id:long result:Object = RpcResult
public sealed partial class RpcResult : IObject
{
    public long req_msg_id;
    public object result;
}

[TLDef(0x5BB8E511)] //message#5bb8e511 msg_id:long seqno:int bytes:int body:Object = Message
public sealed partial class _Message(long msgId, int seqno, IObject obj) : IObject
{
    // parameterless ctor: required by reflection-based ReadTLObject (Activator.CreateInstance)
    public _Message() : this(0, 0, null) { }

    public long msg_id = msgId;
    public int seqno = seqno;
    public int bytes;
    public IObject body = obj;
}

[TLDef(0x73F1F8DC)] //msg_container#73f1f8dc messages:vector<%Message> = MessageContainer
public sealed partial class MsgContainer : IObject { public List<_Message> messages; }
[TLDef(0xE06046B2)] //msg_copy#e06046b2 orig_message:Message = MessageCopy
public sealed partial class MsgCopy : IObject { public _Message orig_message; }

[TLDef(0x3072CFA1)] //gzip_packed#3072cfa1 packed_data:bytes = Object
public sealed partial class GzipPacked : IObject { public byte[] packed_data; }

/// <summary>Упаковка результата boolTrue#997275b5 / boolFalse#bc799737 для IMethod&lt;bool&gt;.</summary>
public sealed class BoolValue : IObject
{
    public bool Value;
}

public sealed class Null<TX> : IObject
{
    public static readonly Null<TX> Instance = new();
    public void WriteTL(BinaryWriter writer) => writer.WriteTLNull(typeof(TX));
}

public sealed class BoolMethod : IMethod<object>
{
    public IObject query;
#if MTPG
		public void WriteTL(BinaryWriter writer) => query.WriteTL(writer);
#else
    public void WriteTL(BinaryWriter writer) => writer.WriteTLObject(query);
#endif
}
