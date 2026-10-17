#nullable enable

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

        Assert.Equal(1443, config.Port);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(ConfigDefaults.DefaultDcIp, config.DcIp);
        Assert.Equal(8081, config.MetricsPort);
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
              "port": 20443,
              "host": "0.0.0.0",
              "wsPoolSize": 0,
              "metricsPort": 9090,
              "logMaxMegabytes": 12.5
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(20443, config.Port);
        Assert.Equal("0.0.0.0", config.Host);
        Assert.Equal(0, config.WsPoolSize);
        Assert.Equal(9090, config.MetricsPort);
        Assert.Equal(12.5, config.LogMaxMegabytes);
    }

    [Fact]
    public void FileWithoutDcIpKeepsDefaultDcList()
    {
        var path = WriteConfig("""{ "port": 8443 }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(ConfigDefaults.DefaultDcIp, config.DcIp);
    }

    [Fact]
    public void EmptyDcIpArrayInFileDisablesPool()
    {
        var path = WriteConfig("""{ "dcIp": [] }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Empty(config.DcIp);
    }

    [Fact]
    public void CollectionsReplaceInsteadOfAppend()
    {
        var path = WriteConfig("""
            {
              "allowIp": ["10.0.0.0/8", "192.168.0.0/16"],
              "dcIp": ["2:1.2.3.4"]
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], config.AllowIp);
        Assert.Equal(["2:1.2.3.4"], config.DcIp);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        var path = WriteConfig("""
            {
              // Порт прослушивания
              "port": 1444,
              /* Хост */
              "host": "127.0.0.2",
            }
            """);

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(1444, config.Port);
        Assert.Equal("127.0.0.2", config.Host);
    }

    [Fact]
    public void FileIsTheOnlyConfigurationSource()
    {
        // F-2.2: слоя CLI больше нет, поэтому значения файла не может перекрыть никто,
        // кроме последующего PUT /api/config.
        var path = WriteConfig("""{ "port": 1444, "host": "10.1.1.1", "dcIp": ["2:9.9.9.9"] }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(1444, config.Port);
        Assert.Equal("10.1.1.1", config.Host);
        Assert.Equal(["2:9.9.9.9"], config.DcIp);
    }

    [Fact]
    public void InvalidJsonReportsLocation()
    {
        var path = WriteConfig("""{ "port": }""");

        var exception = Assert.Throws<ArgumentException>(() => ConfigFileLoader.Load(path, isExplicit: true));
        Assert.Contains("Invalid JSON", exception.Message);
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        var path = WriteConfig("""{ "port": 1443, "prot": 1444 }""");

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
            Port = 2443,
            Host = "0.0.0.0",
            MetricsPort = 9999,
            MetricsHost = "0.0.0.0",
            WsConnectTimeoutSeconds = 33,
            SocketBufferKb = 512,
            WsPoolSize = 2,
            MaxConnections = 500,
            RateLimitPerIp = 42,
            RateLimitWindowSeconds = 120,
            LogMaxMegabytes = 7.5,
            LogRetainedFileCount = 3,
            CfProxyEnabled = false,
            CfProxyH2Media = false,
            DisableSecure = true,
            FakeTlsDomain = "example.com",
            ProxyProtocol = true,
            ForceTestDc = true,
            AllowInvalidCertificates = true,
            Verbose = true,
            LogPath = "/var/log/tgws.log",
        };
        original.DcIp.Add("2:1.2.3.4");
        original.AllowIp.Add("10.0.0.0/8");
        original.DenyIp.Add("10.9.9.9");
        original.CfProxyDomains.Add("cf.example.com");
        original.CfProxyWorkerDomains.Add("worker.example.com");

        var path = Path.Combine(_directory, "roundtrip.json");
        ConfigFileLoader.Save(path, original);
        var restored = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Equal(original.Port, restored.Port);
        Assert.Equal(original.Host, restored.Host);
        Assert.Equal(original.MetricsPort, restored.MetricsPort);
        Assert.Equal(original.MetricsHost, restored.MetricsHost);
        Assert.Equal(original.WsConnectTimeoutSeconds, restored.WsConnectTimeoutSeconds);
        Assert.Equal(original.SocketBufferKb, restored.SocketBufferKb);
        Assert.Equal(original.WsPoolSize, restored.WsPoolSize);
        Assert.Equal(original.MaxConnections, restored.MaxConnections);
        Assert.Equal(original.RateLimitPerIp, restored.RateLimitPerIp);
        Assert.Equal(original.RateLimitWindowSeconds, restored.RateLimitWindowSeconds);
        Assert.Equal(original.LogMaxMegabytes, restored.LogMaxMegabytes);
        Assert.Equal(original.LogRetainedFileCount, restored.LogRetainedFileCount);
        Assert.Equal(original.CfProxyEnabled, restored.CfProxyEnabled);
        Assert.Equal(original.CfProxyH2Media, restored.CfProxyH2Media);
        Assert.Equal(original.DisableSecure, restored.DisableSecure);
        Assert.Equal(original.FakeTlsDomain, restored.FakeTlsDomain);
        Assert.Equal(original.ProxyProtocol, restored.ProxyProtocol);
        Assert.Equal(original.ForceTestDc, restored.ForceTestDc);
        Assert.Equal(original.AllowInvalidCertificates, restored.AllowInvalidCertificates);
        Assert.Equal(original.Verbose, restored.Verbose);
        Assert.Equal(original.LogPath, restored.LogPath);
        Assert.Equal(original.DcIp, restored.DcIp);
        Assert.Equal(original.AllowIp, restored.AllowIp);
        Assert.Equal(original.DenyIp, restored.DenyIp);
        Assert.Equal(original.CfProxyDomains, restored.CfProxyDomains);
        Assert.Equal(original.CfProxyWorkerDomains, restored.CfProxyWorkerDomains);
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
        var path = WriteConfig("""{ "port": 70000 }""");

        var config = ConfigFileLoader.Load(path, isExplicit: true);

        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
    }
}
