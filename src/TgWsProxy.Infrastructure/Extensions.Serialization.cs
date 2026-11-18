#nullable disable
using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.MtProto.TL;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure;

internal static partial class Extensions
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
        var type = obj.GetType();
        var tlDef = type.GetCustomAttribute<TLDefAttribute>();
        var ctorNb = tlDef.CtorNb;
        writer.Write(ctorNb);
        var fields = GetTLFields(type, tlDef);
        ulong flags = 0;
        IfFlagAttribute ifFlag;
        foreach (var field in fields)
        {
            if ((ifFlag = field.GetCustomAttribute<IfFlagAttribute>()) != null && (flags & (1UL << ifFlag.Bit)) == 0)
            {
                continue;
            }

            var value = field.GetValue(obj);
            writer.WriteTLValue(value, field.FieldType);
            if (field.FieldType.IsEnum)
            {
                if (field.Name == "flags")
                {
                    flags = (uint)value;
                }
                else if (field.Name == "flags2")
                {
                    flags |= (ulong)(uint)value << 32;
                }
            }
        }
    }

    public static IObject ReadTLObject(this BinaryReader reader, uint ctorNb = 0)
    {
        if (ctorNb == 0)
        {
            ctorNb = reader.ReadUInt32();
        }

        if (ctorNb == AppConstants.Tl.GZipedCtor)
        {
            return (IObject)reader.ReadTLGzipped(typeof(IObject));
        }

        if (ctorNb == AppConstants.Tl.VectorCtor)
        {
            var count = reader.ReadInt32();
            var items = new IObject[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = reader.ReadTLObject();
            }

            return new TLVector { Items = items };
        }
        // boolTrue#997275b5 / boolFalse#bc799737 — результат IMethod<bool> (не входят в Layer.Table).
        if (ctorNb is 0x997275B5 or 0xBC799737)
        {
            return new BoolValue { Value = ctorNb == 0x997275B5 };
        }
        if (Layer.EmptyCtors.Contains(ctorNb))
        {
            return null; // "empty"/"notModified" ctor (e.g. inputPeerEmpty) => null
        }

        if (!Layer.Table.TryGetValue(ctorNb, out var type))
        {
            throw new InvalidOperationException($"Cannot find type for ctor #{ctorNb:x}");
        }

        var tlDef = type.GetCustomAttribute<TLDefAttribute>();
        var obj = Activator.CreateInstance(type, true);
        var fields = GetTLFields(type, tlDef);
        ulong flags = 0;
        IfFlagAttribute ifFlag;
        foreach (var field in fields)
        {
            if ((ifFlag = field.GetCustomAttribute<IfFlagAttribute>()) != null && (flags & (1UL << ifFlag.Bit)) == 0)
            {
                continue;
            }

            var value = reader.ReadTLValue(field.FieldType);
            field.SetValue(obj, value);
            if (field.FieldType.IsEnum)
            {
                if (field.Name == "flags")
                {
                    flags = (uint)value;
                }
                else if (field.Name == "flags2")
                {
                    flags |= (ulong)(uint)value << 32;
                }
            }
        }
        return (IObject)obj;
    }

    private static IEnumerable<FieldInfo> GetTLFields(Type type, TLDefAttribute tlDef)
    {
        if (!(tlDef?.inheritAt >= 0))
        {
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        if (type.IsAbstract || type.BaseType == typeof(IObject))
        {
            return fields;
        }

        var subfields = GetTLFields(type.BaseType, type.BaseType.GetCustomAttribute<TLDefAttribute>());
        return fields.Take(tlDef.inheritAt).Concat(subfields).Concat(fields.Skip(tlDef.inheritAt));
    }

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
                    {
                        writer.WriteTLBytes(bytes);
                    }
                    else
                    {
                        writer.WriteTLVector((Array)value);
                    }
                }
                else if (value is IObject tlObject)
                {
                    WriteTLObject(writer, tlObject);
                }
                else if (value is List<Msg> messages)
                {
                    writer.WriteTLMessages(messages);
                }
                else if (value is MtProto.TL.Int128 int128)
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
                    AppConstants.Tl.RpcErrorCtor => reader.ReadTLObject(AppConstants.Tl.RpcErrorCtor),
                    var value => throw new FormatException($"Invalid boolean value #{value:x}")
                };
            case TypeCode.Object:
                if (type.IsArray)
                {
                    return type == typeof(byte[]) ? reader.ReadTLBytes() : (object)reader.ReadTLVector(type);
                }
                else if (type == typeof(List<Msg>))
                {
                    return reader.ReadTLMessages();
                }
                else
                {
                    return type == typeof(MtProto.TL.Int128)
                        ? new MtProto.TL.Int128(reader)
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

    internal static void WriteTLMessages(this BinaryWriter writer, List<Msg> messages)
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

    internal static List<Msg> ReadTLMessages(this BinaryReader reader)
    {
        // msg_container#73f1f8dc messages:vector<%Message> — bare vector: count then
        // per-message struct msg_id:long seqno:int bytes:int body:Object (no ctor id).
        var count = reader.ReadInt32();
        var messages = new List<Msg>(count);
        for (var i = 0; i < count; i++)
        {
            var msg = new Msg
            {
                msg_id = reader.ReadInt64(),
                seqno = reader.ReadInt32(),
                bytes = reader.ReadInt32(),
                body = reader.ReadTLObject()
            };
            messages.Add(msg);
        }
        return messages;
    }

    internal static void WriteTLVector(this BinaryWriter writer, Array array)
    {
        writer.Write(AppConstants.Tl.VectorCtor);
        if (array == null) { writer.Write(0); return; }
        var count = array.Length;
        writer.Write(count);
        var elementType = array.GetType().GetElementType();
        for (var i = 0; i < count; i++)
        {
            writer.WriteTLValue(array.GetValue(i), elementType);
        }
    }

    internal static T[] ReadTLVector<T>(this BinaryReader reader)
    {
        var elementType = typeof(T);
        if (reader.ReadUInt32() is not AppConstants.Tl.VectorCtor and uint ctorNb)
        {
            throw new FormatException($"Cannot deserialize {elementType.Name}[] with ctor #{ctorNb:x}");
        }

        var count = reader.ReadInt32();
        var array = new T[count];
        for (var i = 0; i < count; i++)
        {
            array[i] = (T)reader.ReadTLValue(elementType);
        }

        return array;
    }

    internal static Array ReadTLVector(this BinaryReader reader, Type type)
    {
        var elementType = type.GetElementType();
        var ctorNb = reader.ReadUInt32();
        if (ctorNb == AppConstants.Tl.VectorCtor)
        {
            var count = reader.ReadInt32();
            var array = (Array)Activator.CreateInstance(type, count);
            if (elementType.IsEnum)
            {
                for (var i = 0; i < count; i++)
                {
                    array.SetValue(Enum.ToObject(elementType, reader.ReadTLValue(elementType)), i);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    array.SetValue(reader.ReadTLValue(elementType), i);
                }
            }

            return array;
        }
        else if (ctorNb < 1024 && !elementType.IsAbstract && elementType.GetCustomAttribute<TLDefAttribute>() is TLDefAttribute attr)
        {
            var count = (int)ctorNb;
            var array = (Array)Activator.CreateInstance(type, count);
            for (var i = 0; i < count; i++)
            {
                array.SetValue(reader.ReadTLObject(attr.CtorNb), i);
            }

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
        if (ctorNb != AppConstants.Tl.VectorCtor)
        {
            throw new FormatException($"Cannot deserialize Vector<{typeof(T).Name}> with ctor #{ctorNb:x}");
        }

        var count = reader.ReadInt32();
        var dict = new Dictionary<long, T>(count);
        for (var i = 0; i < count; i++)
        {
            var obj = reader.ReadTLObject();
            if (obj is T value)
            {
                dict[value.ID] = value;
            }
            else if (obj is UserEmpty ue)
            {
                dict[ue.id] = null;
            }
            else
            {
                throw new InvalidCastException($"ReadTLDictionary got '{obj?.GetType().Name}' instead of '{typeof(T).Name}'");
            }
        }
        return dict;
    }

    internal static void WriteTLStamp(this BinaryWriter writer, DateTime datetime)
        => writer.Write((int)Math.Min(Math.Max(datetime.ToUniversalTime().Ticks / 10000000 - 62135596800L, 0), int.MaxValue));

    internal static DateTime ReadTLStamp(this BinaryReader reader) => reader.ReadInt32() switch
    {
        <= 0 => default,
        int.MaxValue => DateTime.MaxValue,
        int unixstamp => new((unixstamp + 62135596800L) * 10000000, DateTimeKind.Utc)
    };

    internal static void WriteTLString(this BinaryWriter writer, string str)
    {
        if (str == null)
        {
            writer.Write(0);
        }
        else
        {
            writer.WriteTLBytes(Encoding.UTF8.GetBytes(str));
        }
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
        while (++length % 4 != 0)
        {
            writer.Write((byte)0);
        }
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
        while (++length % 4 != 0)
        {
            _ = reader.ReadByte();
        }

        return bytes;
    }

    internal static void WriteTLNull(this BinaryWriter writer, Type type)
    {
        if (type == typeof(string)) { }
        else if (!type.IsArray)
        {
            writer.Write(Layer.Nullables.TryGetValue(type, out var nullCtor) ? nullCtor : AppConstants.Tl.NullCtor);
            return;
        }
        else if (type != typeof(byte[]))
        {
            writer.Write(AppConstants.Tl.VectorCtor); // not raw bytes but a vector => needs a VectorCtor
        }

        writer.Write(0);    // null arrays/strings are serialized as empty
    }

    internal static object ReadTLGzipped(this BinaryReader reader, Type type)
    {
        using var gzipReader = new BinaryReader(new GZipStream(new MemoryStream(reader.ReadTLBytes()), CompressionMode.Decompress));
        return gzipReader.ReadTLValue(type);
    }

#if DEBUG
    private static void ShouldntBeHere() => System.Diagnostics.Debugger.Break();
#else
    private static void ShouldntBeHere() => throw new NotImplementedException("You've reached an unexpected point in code");
#endif
}
