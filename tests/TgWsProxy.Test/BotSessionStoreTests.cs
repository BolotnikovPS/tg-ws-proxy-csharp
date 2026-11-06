using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public sealed class BotSessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public BotSessionStoreTests() => _tempDir = Path.Combine(Path.GetTempPath(), "tgsession-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static BotSession NewSession()
        => new()
        {
            AuthKey = [.. Enumerable.Range(0, 256).Select(i => (byte)i)],
            AuthKeyId = 0x0102030405060708,
            Salt = 0x1112131415161718,
            SessionId = 0x2122232425262728,
            DcId = 2,
            TestMode = false,
            ServerTimeOffset = -3600,
            LastMsgId = 0x3132333435363738,
            SeqNo = 42,
            ApiId = 12345,
            Pts = 111,
            Qts = 222,
            Date = 333,
            Seq = 444,
        };

    [Fact]
    public void BotSession_RoundTrip_PreservesAllFields()
    {
        var s = NewSession();

        using var ms = new MemoryStream();
        s.WriteTo(ms);
        ms.Position = 0;
        var restored = BotSession.ReadFrom(ms);

        Assert.NotNull(restored);
        Assert.True(restored.IsValid);
        Assert.Equal(s.AuthKey, restored.AuthKey);
        Assert.Equal(s.AuthKeyId, restored.AuthKeyId);
        Assert.Equal(s.Salt, restored.Salt);
        Assert.Equal(s.SessionId, restored.SessionId);
        Assert.Equal(s.DcId, restored.DcId);
        Assert.Equal(s.TestMode, restored.TestMode);
        Assert.Equal(s.ServerTimeOffset, restored.ServerTimeOffset);
        Assert.Equal(s.LastMsgId, restored.LastMsgId);
        Assert.Equal(s.SeqNo, restored.SeqNo);
        Assert.Equal(s.ApiId, restored.ApiId);
        Assert.Equal(s.Pts, restored.Pts);
        Assert.Equal(s.Qts, restored.Qts);
        Assert.Equal(s.Date, restored.Date);
        Assert.Equal(s.Seq, restored.Seq);
    }

    [Fact]
    public void BotSession_ReadFrom_ReturnsNull_OnBadMagic()
    {
        using var ms = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Null(BotSession.ReadFrom(ms));
    }

    [Fact]
    public void BotSession_IsInvalid_WithoutAuthKey()
    {
        var s = NewSession();
        s.AuthKey = null;
        Assert.False(s.IsValid);
    }

    [Fact]
    public void Store_SaveLoadDelete_RoundTrip()
    {
        var store = new BotSessionStore(_tempDir);
        const long botId = 123456789;

        Assert.Null(store.Load(botId)); // отсутствует

        var s = NewSession();
        store.Save(botId, s);

        var loaded = store.Load(botId);
        Assert.NotNull(loaded);
        Assert.True(loaded.IsValid);
        Assert.Equal(s.AuthKey, loaded.AuthKey);
        Assert.Equal(s.AuthKeyId, loaded.AuthKeyId);
        Assert.Equal(s.SessionId, loaded.SessionId);
        Assert.Equal(s.SeqNo, loaded.SeqNo);

        Assert.True(store.Delete(botId));
        Assert.Null(store.Load(botId));
        Assert.False(store.Delete(botId)); // повторное удаление
    }

    [Fact]
    public void MtProtoClient_ImportExportSession_RoundTrip()
    {
        var s = NewSession();
        using var client = new MtProtoClient("127.0.0.1", 1);

        client.ImportSession(s);
        var exported = client.ExportSession();

        Assert.NotNull(exported);
        Assert.Equal(s.AuthKey, exported.AuthKey);
        Assert.Equal(s.AuthKeyId, exported.AuthKeyId);
        Assert.Equal(s.Salt, exported.Salt);
        Assert.Equal(s.DcId, exported.DcId);
        Assert.Equal(s.TestMode, exported.TestMode);
        Assert.Equal(s.ServerTimeOffset, exported.ServerTimeOffset);
        Assert.Equal(s.ApiId, exported.ApiId);

        // Обновление ленты переживает перезапуск.
        Assert.Equal(s.Pts, exported.Pts);
        Assert.Equal(s.Qts, exported.Qts);
        Assert.Equal(s.Seq, exported.Seq);
        Assert.Equal(s.Date, exported.Date);

        // Сессия открывается заново: seq_no/msg_id прошлого процесса сервер уже видел,
        // продолжить их нельзя (bad_msg_notification error_code=32), поэтому счётчики
        // обнуляются, а session_id генерируется новый.
        Assert.Equal(0, exported.LastMsgId);
        Assert.Equal(0, exported.SeqNo);
        Assert.NotEqual(s.SessionId, exported.SessionId);
    }

    [Fact]
    public void BotSession_ReadFrom_AcceptsLegacyVersionWithoutApiId()
    {
        var s = NewSession();
        using var ms = new MemoryStream();
        s.WriteTo(ms);

        // Собираем файл прежней версии: version = 1 и нет поля ApiId (4 байта перед длиной ключа).
        const int apiIdOffset =
            sizeof(uint) + sizeof(int)   // magic + version
            + sizeof(long)               // auth_key_id
            + sizeof(long)               // salt
            + sizeof(long)               // session_id
            + sizeof(int)                // dc_id
            + sizeof(bool)               // test_mode
            + sizeof(long)               // server_time_offset
            + sizeof(long)               // last_msg_id
            + sizeof(int)                // seq_no
            + sizeof(long)               // pts
            + sizeof(long)               // qts
            + sizeof(long)               // date
            + sizeof(long);              // seq

        var bytes = ms.ToArray();
        BitConverter.GetBytes(1).CopyTo(bytes, sizeof(uint));
        var legacy = bytes[..apiIdOffset].Concat(bytes[(apiIdOffset + sizeof(int))..]).ToArray();

        using var read = new MemoryStream(legacy);
        var restored = BotSession.ReadFrom(read);

        Assert.NotNull(restored);
        Assert.True(restored.IsValid);
        Assert.Equal(0, restored.ApiId);
        Assert.Equal(s.SeqNo, restored.SeqNo);
    }
}
