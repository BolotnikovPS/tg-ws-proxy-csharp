#nullable disable
using System;
using System.IO;
using System.Text;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Персистентное состояние MTProto-сессии одного бота. Самодостаточная бинарная сериализация
/// (компактный little-endian формат с magic и версией) — без SQLite и лишних NuGet-пакетов.
/// </summary>
public sealed class BotSession
{
    private const uint Magic = 0x53544F42; // "BOTS" в little-endian
    private const int Version = 1;

    /// <summary>auth_key (256 байт). <c>null</c> или неверной длины — сессия невалидна.</summary>
    public byte[] AuthKey { get; set; }

    public long AuthKeyId { get; set; }
    public long Salt { get; set; }
    public long SessionId { get; set; }
    public int DcId { get; set; }
    public bool TestMode { get; set; }
    public long ServerTimeOffset { get; set; }
    public long LastMsgId { get; set; }
    public int SeqNo { get; set; }

    // Состояние обновлений (заполняется после getUpdates/getState; сейчас резерв = 0).
    public long Pts { get; set; }
    public long Qts { get; set; }
    public long Date { get; set; }
    public long Seq { get; set; }

    public bool IsValid => AuthKey is { Length: 256 };

    public void WriteTo(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(AuthKeyId);
        w.Write(Salt);
        w.Write(SessionId);
        w.Write(DcId);
        w.Write(TestMode);
        w.Write(ServerTimeOffset);
        w.Write(LastMsgId);
        w.Write(SeqNo);
        w.Write(Pts);
        w.Write(Qts);
        w.Write(Date);
        w.Write(Seq);
        // Ключ — последним, с префиксом длины.
        var key = AuthKey ?? [];
        w.Write(key.Length);
        w.Write(key);
    }

    public static BotSession ReadFrom(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
        {
            return null;
        }

        if (r.ReadInt32() != Version)
        {
            return null;
        }

        var s = new BotSession
        {
            AuthKeyId = r.ReadInt64(),
            Salt = r.ReadInt64(),
            SessionId = r.ReadInt64(),
            DcId = r.ReadInt32(),
            TestMode = r.ReadBoolean(),
            ServerTimeOffset = r.ReadInt64(),
            LastMsgId = r.ReadInt64(),
            SeqNo = r.ReadInt32(),
            Pts = r.ReadInt64(),
            Qts = r.ReadInt64(),
            Date = r.ReadInt64(),
            Seq = r.ReadInt64(),
        };

        var keyLen = r.ReadInt32();
        if (keyLen is < 0 or > 4096)
        {
            return null;
        }

        s.AuthKey = keyLen == 0 ? null : r.ReadBytes(keyLen);
        return s;
    }
}
