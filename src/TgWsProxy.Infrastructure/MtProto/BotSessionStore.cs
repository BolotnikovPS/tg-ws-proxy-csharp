#nullable disable
using System;
using System.IO;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Файловое хранилище сессий ботов: по одному файлу <c>{botId}.bin</c> на бота.
/// Атомарная запись (temp + rename). При отсутствии или повреждении файла <see cref="Load"/>
/// возвращает <c>null</c>.
/// </summary>
public sealed class BotSessionStore(string dataDir)
{
    private readonly string _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "./bot-sessions" : dataDir;

    public string GetPath(long botId) => Path.Combine(_dataDir, botId + ".bin");

    public BotSession Load(long botId)
    {
        var path = GetPath(botId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return BotSession.ReadFrom(fs);
        }
        catch (Exception)
        {
            return null; // повреждён или недоступен — трактуем как отсутствие
        }
    }

    public void Save(long botId, BotSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(_dataDir);
        var path = GetPath(botId);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            session.WriteTo(fs);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public bool Delete(long botId)
    {
        var path = GetPath(botId);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
