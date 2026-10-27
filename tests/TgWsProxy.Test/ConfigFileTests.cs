using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты слоя файловой конфигурации: приоритет источников (умолчания → файл),
/// устойчивость к ошибкам ввода и round-trip записи.
/// </summary>
public sealed class ConfigFileTests : IDisposable
{
    private readonly string _directory;

    public ConfigFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tgws-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Каталог временный: неудачная очистка не должна ронять тесты.
        }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void MissingFileWithoutExplicitPathReturnsDefaults()
    {
        var path = Path.Combine(_directory, "does-not-exist.json");

        var config = ConfigFileLoader.Load(path, isExplicit: false);

        Assert.Equal(1443, config.MtProto.Port);
        Assert.Equal(ConfigDefaults.DefaultDcIp, config.MtProto.DcIp);
        Assert.Equal(5, config.Logs.MaxMegabytes);
    }

    [Fact]
    public void MissingFileWithExplicitPathThrows()
    {
        var path = Path.Combine(_directory, "does-not-exist.json");

        var exception = Assert.Throws<ArgumentException>(() => ConfigFileLoader.Load(path, isExplicit: true));
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileOverridesDefaults()
    {
        var path = WriteConfig("""
            {
              "mtproto": {
                "port": 20443,
                "wsPoolSize": 0
              },
              "logs": { "maxMegabytes": 12.5 }
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(20443, config.MtProto.Port);
        Assert.Equal(0, config.MtProto.WsPoolSize);
        Assert.Equal(12.5, config.Logs.MaxMegabytes);
    }

    [Fact]
    public void LegacyFlatLogKeysMigrateIntoLogsBlock()
    {
        // Прежние плоские ключи logPath/logMaxMegabytes/logRetainedFileCount при чтении
        // переносятся во вложенный блок logs (по образцу миграции secret → tokens).
        var path = WriteConfig("""
            {
              "logPath": "/var/log/tgws.log",
              "logMaxMegabytes": 20,
              "logRetainedFileCount": 5
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal("/var/log/tgws.log", config.Logs.Path);
        Assert.Equal(20, config.Logs.MaxMegabytes);
        Assert.Equal(5, config.Logs.RetainedFileCount);
    }

    [Fact]
    public void LogsBlockWinsOverLegacyFlatKeys()
    {
        // Если задан и новый блок logs, и плоский legacy-ключ — побеждает блок logs,
        // плоский ключ игнорируется.
        var path = WriteConfig("""
            {
              "logPath": "/var/log/legacy.log",
              "logs": { "path": "/var/log/current.log", "maxMegabytes": 8 }
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal("/var/log/current.log", config.Logs.Path);
        Assert.Equal(8, config.Logs.MaxMegabytes);
        Assert.Equal(1, config.Logs.RetainedFileCount); // умолчание: плоский ключ не задан
    }

    [Fact]
    public void FileWithoutDcIpKeepsDefaultDcList()
    {
        var path = WriteConfig("""{ "mtproto": { "port": 8443 } }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(ConfigDefaults.DefaultDcIp, config.MtProto.DcIp);
    }

    [Fact]
    public void EmptyDcIpArrayInFileDisablesPool()
    {
        var path = WriteConfig("""{ "mtproto": { "dcIp": [] } }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Empty(config.MtProto.DcIp);
    }

    [Fact]
    public void CollectionsReplaceInsteadOfAppend()
    {
        var path = WriteConfig("""
            {
              "allowIp": ["10.0.0.0/8", "192.168.0.0/16"],
              "mtproto": { "dcIp": ["2:1.2.3.4"] }
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], config.AllowIp);
        Assert.Equal(["2:1.2.3.4"], config.MtProto.DcIp);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        var path = WriteConfig("""
            {
              "mtproto": {
                // Порт прослушивания
                "port": 1444,
                /* Список DC */
                "dcIp": ["2:1.2.3.4"],
              }
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(1444, config.MtProto.Port);
        Assert.Equal(["2:1.2.3.4"], config.MtProto.DcIp);
    }

    [Fact]
    public void FileIsTheOnlyConfigurationSource()
    {
        // F-2.2: слоя CLI больше нет, поэтому значения файла не может перекрыть никто,
        // кроме последующего PUT /api/config.
        var path = WriteConfig("""{ "mtproto": { "port": 1444, "dcIp": ["2:9.9.9.9"] } }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(1444, config.MtProto.Port);
        Assert.Equal(["2:9.9.9.9"], config.MtProto.DcIp);
    }

    [Fact]
    public void InvalidJsonReportsLocation()
    {
        var path = WriteConfig("""{ "mtproto": { "port": } }""");

        var exception = Assert.Throws<ArgumentException>(() => ConfigFileLoader.Load(path, isExplicit: true));
        Assert.Contains("Invalid JSON", exception.Message);
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        var path = WriteConfig("""{ "mtproto": { "port": 1443 }, "prot": 1444 }""");

        var exception = Assert.Throws<ArgumentException>(() => ConfigFileLoader.Load(path, isExplicit: true));
        Assert.Contains("\"prot\"", exception.Message);
    }

    [Fact]
    public void NonObjectRootIsRejected()
    {
        var path = WriteConfig("""[1, 2, 3]""");

        var exception = Assert.Throws<ArgumentException>(() => ConfigFileLoader.Load(path, isExplicit: true));
        Assert.Contains("JSON object", exception.Message);
    }

    [Fact]
    public void RoundTripPreservesValues()
    {
        var original = new Config
        {
            MtProto = new MtProtoConfig
            {
                Port = 2443,
                WsConnectTimeoutSeconds = 33,
                SocketBufferKb = 512,
                WsPoolSize = 2,
                AllowInvalidCertificates = true,
            },
            MaxConnections = 500,
            RateLimitPerIp = 42,
            RateLimitWindowSeconds = 120,
            CfProxy = new CfProxyConfig { Enabled = false, H2Media = false },
            DisableSecure = true,
            FakeTlsDomain = "example.com",
            ProxyProtocol = true,
            ForceTestDc = true,
            Verbose = true,
            Logs = new LogsConfig
            {
                Path = "/var/log/tgws.log",
                MaxMegabytes = 7.5,
                RetainedFileCount = 3,
            },
        };
        original.MtProto.DcIp.Add("2:1.2.3.4");
        original.AllowIp.Add("10.0.0.0/8");
        original.DenyIp.Add("10.9.9.9");
        original.CfProxy.Domains.Add("cf.example.com");
        original.CfProxy.WorkerDomains.Add("worker.example.com");

        var path = Path.Combine(_directory, "roundtrip.json");
        ConfigFileLoader.Save(path, original);
        var restored = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(original.MtProto.Port, restored.MtProto.Port);
        Assert.Equal(original.MtProto.WsConnectTimeoutSeconds, restored.MtProto.WsConnectTimeoutSeconds);
        Assert.Equal(original.MtProto.SocketBufferKb, restored.MtProto.SocketBufferKb);
        Assert.Equal(original.MtProto.WsPoolSize, restored.MtProto.WsPoolSize);
        Assert.Equal(original.MaxConnections, restored.MaxConnections);
        Assert.Equal(original.RateLimitPerIp, restored.RateLimitPerIp);
        Assert.Equal(original.RateLimitWindowSeconds, restored.RateLimitWindowSeconds);
        Assert.Equal(original.Logs.MaxMegabytes, restored.Logs.MaxMegabytes);
        Assert.Equal(original.Logs.RetainedFileCount, restored.Logs.RetainedFileCount);
        Assert.Equal(original.CfProxy.Enabled, restored.CfProxy.Enabled);
        Assert.Equal(original.CfProxy.H2Media, restored.CfProxy.H2Media);
        Assert.Equal(original.DisableSecure, restored.DisableSecure);
        Assert.Equal(original.FakeTlsDomain, restored.FakeTlsDomain);
        Assert.Equal(original.ProxyProtocol, restored.ProxyProtocol);
        Assert.Equal(original.ForceTestDc, restored.ForceTestDc);
        Assert.Equal(original.MtProto.AllowInvalidCertificates, restored.MtProto.AllowInvalidCertificates);
        Assert.Equal(original.Verbose, restored.Verbose);
        Assert.Equal(original.Logs.Path, restored.Logs.Path);
        Assert.Equal(original.MtProto.DcIp, restored.MtProto.DcIp);
        Assert.Equal(original.AllowIp, restored.AllowIp);
        Assert.Equal(original.DenyIp, restored.DenyIp);
        Assert.Equal(original.CfProxy.Domains, restored.CfProxy.Domains);
        Assert.Equal(original.CfProxy.WorkerDomains, restored.CfProxy.WorkerDomains);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "atomic.json");

        ConfigFileLoader.Save(path, new Config());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ConfigFlagIsNotAppliedAsUnknownArgument()
    {
        // --config остаётся единственным флагом и не должен трактоваться как неизвестный аргумент.
        var options = BootstrapOptions.Parse(["--config", "ignored.json"]);

        Assert.Equal("ignored.json", options.ConfigPath);
    }

    [Fact]
    public void InvalidFileValuesAreRejectedByValidator()
    {
        var path = WriteConfig("""{ "mtproto": { "port": 70000 } }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
    }
}