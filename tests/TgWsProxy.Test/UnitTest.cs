using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты bootstrap-аргументов, умолчаний конфигурации, MTProto handshake, статистики
/// и разборщика пакетов. Ожидаемые значения соответствуют upstream Flowseal/tg-ws-proxy v1.11.2.
/// </summary>
public class UnitTest
{
    [Fact]
    public void Config_Defaults_Match_Upstream_Values()
    {
        var cfg = new Config();
        ConfigDefaults.Apply(cfg);

        Assert.Equal(1443, cfg.MtProto.Port);
        Assert.Equal(4, cfg.MtProto.WsPoolSize);
        Assert.Equal(5, cfg.Logs.MaxMegabytes);
        Assert.Equal(1, cfg.Logs.RetainedFileCount);
        Assert.Equal(256, cfg.MtProto.SocketBufferKb);
        Assert.True(cfg.CfProxy.Enabled);
        Assert.True(cfg.CfProxy.H2Media);
        Assert.Equal(ConfigDefaults.DefaultDcIp, cfg.MtProto.DcIp);
    }

    [Fact]
    public void ConfigDefaults_Apply_Replaces_Existing_DcList()
    {
        // Повторное применение не должно накапливать записи: список перезаписывается целиком.
        var cfg = new Config();
        ConfigDefaults.Apply(cfg);
        ConfigDefaults.Apply(cfg);

        Assert.Equal(ConfigDefaults.DefaultDcIp, cfg.MtProto.DcIp);
    }

    [Fact]
    public void ConfigDefaults_ParseDcIpList_Builds_Map()
    {
        var map = ConfigDefaults.ParseDcIpList(["2:149.154.167.220", "4:149.154.167.220"]);

        Assert.Equal("149.154.167.220", map[2]);
        Assert.Equal("149.154.167.220", map[4]);
    }

    [Fact]
    public void ConfigDefaults_ParseDcIpList_Rejects_Malformed_Entry()
    {
        Assert.Throws<ArgumentException>(() => ConfigDefaults.ParseDcIpList(["149.154.167.220"]));
        Assert.Throws<ArgumentException>(() => ConfigDefaults.ParseDcIpList(["2:not-an-ip"]));
        Assert.Throws<ArgumentException>(() => ConfigDefaults.ParseDcIpList(["x:1.2.3.4"]));
    }

    [Fact]
    public void BootstrapOptions_Defaults_To_No_ConfigPath()
    {
        var options = BootstrapOptions.Parse([]);

        Assert.Null(options.ConfigPath);
    }

    [Theory]
    [InlineData(new[] { "--config", "a.json" }, "a.json")]
    [InlineData(new[] { "--config=a.json" }, "a.json")]
    public void BootstrapOptions_Parses_Config_Both_Forms(string[] args, string expected)
    {
        Assert.Equal(expected, BootstrapOptions.Parse(args).ConfigPath);
    }

    [Fact]
    public void BootstrapOptions_Last_Config_Wins()
    {
        var options = BootstrapOptions.Parse(["--config", "a.json", "--config=b.json"]);

        Assert.Equal("b.json", options.ConfigPath);
    }

    [Fact]
    public void BootstrapOptions_Rejects_Missing_Config_Path()
    {
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--config"]));
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--config="]));
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--config", "   "]));
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void BootstrapOptions_Rejects_Info_Flags(string flag)
    {
        // Справка и версия удалены: проект — веб-сервис, единственная опция — --config.
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([flag]));
    }

    [Theory]
    [InlineData("--port", "mtproto.port")]
    [InlineData("--secret", "tokens")]
    [InlineData("--dc-ip", "mtproto.dcIp")]
    [InlineData("--verbose", "verbose")]
    public void BootstrapOptions_Legacy_Flag_Error_Points_To_Json_Key(string flag, string jsonKey)
    {
        // Подсказка «флаг → ключ config.json» избавляет от догадок при миграции с 2.x.
        var exception = Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([flag, "1"]));

        Assert.Contains(jsonKey, exception.Message);
        Assert.Contains("config.json", exception.Message);
    }

    [Fact]
    public void BootstrapOptions_Legacy_Flag_With_Value_Form_Is_Hinted()
    {
        var exception = Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--port=1443"]));

        Assert.Contains("port", exception.Message);
    }

    [Fact]
    public void BootstrapOptions_Rejects_Unknown_Argument()
    {
        var exception = Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--unknown", "x"]));

        Assert.Contains("--unknown", exception.Message);
    }

    [Fact]
    public void BootstrapOptions_Rejects_Removed_Upstream_Flags()
    {
        // --auth и --cfproxy-priority отсутствуют в upstream и удалены из порта.
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--auth", "u:p"]));
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(["--cfproxy-priority", "cf"]));
    }

    [Fact]
    public void ConfigValidator_Rejects_InvalidIpAcl()
    {
        var allow = TestConfig.Create();
        allow.AllowIp.Add("not-an-ip");
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(allow));

        var deny = TestConfig.Create();
        deny.DenyIp.Add("10.0.0.0/33");
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(deny));
    }

    [Fact]
    public void Config_H2Enabled_Requires_Secure_CfProxy_And_No_TestDc()
    {
        Assert.True(new Config().H2Enabled);
        Assert.False(new Config { DisableSecure = true }.H2Enabled);
        Assert.False(new Config { CfProxy = new CfProxyConfig { Enabled = false } }.H2Enabled);
        Assert.False(new Config { ForceTestDc = true }.H2Enabled);
        Assert.False(new Config { CfProxy = new CfProxyConfig { H2Media = false } }.H2Enabled);
    }

    [Fact]
    public void ConfigValidator_Rejects_BadPort()
    {
        var cfg = TestConfig.Create(c => c.MtProto.Port = 70000);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(cfg));
    }

    [Fact]
    public void ConfigValidator_Rejects_WsTimeout_OutOfRange()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.MtProto.WsConnectTimeoutSeconds = 0)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.MtProto.WsConnectTimeoutSeconds = 400)));
    }

    [Fact]
    public void ConfigValidator_Rejects_Timeseries_Step_Above_Window()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
        {
            c.TimeseriesWindowSeconds = 60;
            c.TimeseriesStepSeconds = 120;
        })));
    }

    [Fact]
    public void Config_Default_WsTimeout_Is_Ten_Seconds()
        => Assert.Equal(10, new Config().MtProto.WsConnectTimeoutSeconds);

    [Fact]
    public void Config_Default_WsMaxFrame_Is_Sixteen_MiB()
        => Assert.Equal(16 * 1024 * 1024, new Config().MtProto.WsMaxFrameBytes);

    [Fact]
    public void Config_Default_Has_No_Tokens()
        // Токены создаёт TokenBootstrap при старте (F-8.3): в самом домене их нет.
        => Assert.Empty(new Config().Tokens);

    [Fact]
    public void ConfigValidator_Rejects_NonHex_TokenSecret()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
            c.Tokens[0].Secret = "not-a-hex-secret")));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
            c.Tokens[0].Secret = new string('a', 31))));
    }

    [Fact]
    public void ConfigValidator_Requires_At_Least_One_Token()
        => Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(new Config()));

    [Fact]
    public void ConfigValidator_Requires_At_Least_One_Enabled_Token()
        => Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
            c.Tokens[0].Enabled = false)));

    [Fact]
    public void ConfigValidator_Rejects_Duplicate_TokenNames_And_Secrets()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
            c.Tokens.Add(new ProxyToken
            {
                Id = "second",
                Name = "DEFAULT",
                Secret = "ffeeddccbbaa99887766554433221100",
                Enabled = true,
            }))));

        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
            c.Tokens.Add(new ProxyToken
            {
                Id = "second",
                Name = "second",
                Secret = TestConfig.TokenSecret.ToUpperInvariant(),
                Enabled = true,
            }))));
    }

    [Fact]
    public void ConfigValidator_Rejects_More_Than_Max_Tokens()
        => Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c =>
        {
            for (var i = 0; i <= ConfigValidator.MaxTokens; i++)
            {
                c.Tokens.Add(new ProxyToken
                {
                    Id = "token-" + i,
                    Name = "token-" + i,
                    Secret = i.ToString("x8").PadLeft(32, '0'),
                    Enabled = true,
                });
            }
        })));

    [Fact]
    public void Config_Default_Limits_Are_Disabled()
    {
        var cfg = new Config();

        Assert.Equal(0, cfg.MaxConnections);
        Assert.Equal(0, cfg.RateLimitPerIp);
        Assert.Equal(60, cfg.RateLimitWindowSeconds);
    }

    [Fact]
    public void ConfigValidator_Rejects_Limit_OutOfRange()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.MaxConnections = -1)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.MaxConnections = 1_000_001)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.RateLimitPerIp = -1)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.RateLimitPerIp = 1_000_001)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.RateLimitWindowSeconds = 0)));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(TestConfig.Create(c => c.RateLimitWindowSeconds = 86_401)));
    }

    [Fact]
    public void ConfigValidator_Accepts_Boundary_Limits()
        => ConfigValidator.Validate(TestConfig.Create(c =>
        {
            c.MaxConnections = 1_000_000;
            c.RateLimitPerIp = 1;
            c.RateLimitWindowSeconds = 86_400;
        }));

    [Fact]
    public void MtProtoInspector_ReturnsNull_OnGarbage()
    {
        var inspector = new MtProtoInspector(NullLogger<MtProtoInspector>.Instance);
        var data = new byte[64];

        Assert.Null(inspector.TryHandshake(data, new byte[16]));
    }

    [Fact]
    public void MtProtoInspector_ReturnsNull_OnShortInit()
    {
        var inspector = new MtProtoInspector(NullLogger<MtProtoInspector>.Instance);

        Assert.Null(inspector.TryHandshake(new byte[63], new byte[16]));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n", true)]
    [InlineData("POST /api HTTP/1.1\r\n", true)]
    [InlineData("OPTIONS / HTTP/1.1\r\n", true)]
    [InlineData("HEAD / HTTP/1.1\r\n", true)]
    [InlineData("CONNECT host:443 HTTP/1.1\r\n", false)]
    [InlineData("XXXX / HTTP/1.1\r\n", false)]
    public void MtProtoInspector_IsHttpTransport_DetectsKnownMethods(string line, bool expected)
    {
        var inspector = new MtProtoInspector(NullLogger<MtProtoInspector>.Instance);
        var data = System.Text.Encoding.ASCII.GetBytes(line);

        Assert.Equal(expected, inspector.IsHttpTransport(data));
    }

    [Theory]
    [InlineData(Protocol.ProtoAbridgedInt)]
    [InlineData(Protocol.ProtoIntermediateInt)]
    [InlineData(Protocol.ProtoPaddedIntermediateInt)]
    public void MtProtoInspector_TryHandshake_RoundTrips_ProtoTag(int protoInt)
    {
        var secret = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var protoTag = protoInt switch
        {
            Protocol.ProtoAbridgedInt => new byte[] { 0xEF, 0xEF, 0xEF, 0xEF },
            Protocol.ProtoIntermediateInt => new byte[] { 0xEE, 0xEE, 0xEE, 0xEE },
            _ => new byte[] { 0xDD, 0xDD, 0xDD, 0xDD },
        };
        var init = BuildClientInit(secret, protoTag, dcIdx: -2);

        var handshake = new MtProtoInspector(NullLogger<MtProtoInspector>.Instance)
            .TryHandshake(init, secret);

        Assert.NotNull(handshake);
        Assert.Equal(2, handshake!.Value.Dc);
        Assert.True(handshake.Value.IsMedia);
        Assert.Equal(protoInt, handshake.Value.ProtoInt);
        Assert.Equal(protoTag, handshake.Value.ProtoTag);
    }

    [Fact]
    public void MtProtoInspector_GenerateRelayInit_Produces_NonReserved_Handshake()
    {
        var inspector = new MtProtoInspector(NullLogger<MtProtoInspector>.Instance);
        var relayInit = inspector.GenerateRelayInit([0xEF, 0xEF, 0xEF, 0xEF], 2);

        Assert.Equal(Protocol.HandshakeLen, relayInit.Length);
        Assert.DoesNotContain(relayInit[0], (IReadOnlySet<byte>)Protocol.ReservedFirstBytes);
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, relayInit[4..8]);
    }

    [Fact]
    public void ProxyStats_Summary_ReflectsCounters()
    {
        var stats = new ProxyStats();
        stats.IncConnectionsTotal();
        stats.IncConnectionsActive();
        stats.IncConnectionsWs();
        stats.IncConnectionsTcpFallback();
        stats.IncConnectionsCfProxy();
        stats.IncConnectionsFronting();
        stats.IncConnectionsBad();
        stats.IncConnectionsBlocked();
        stats.IncConnectionsMasked();
        stats.IncConnectionsLimited();
        stats.IncConnectionsRateLimited();
        stats.IncWsErrors();
        stats.IncPoolHit();
        stats.IncPoolMiss();
        stats.IncCfPoolHit();
        stats.IncCfPoolMiss();
        stats.AddBytesUp(42);
        stats.AddBytesDown(84);

        var summary = stats.Summary();

        Assert.Contains("total=1", summary);
        Assert.Contains("active=1", summary);
        Assert.Contains("ws=1", summary);
        Assert.Contains("tcp_fb=1", summary);
        Assert.Contains("cf=1", summary);
        Assert.Contains("front=1", summary);
        Assert.Contains("bad=1", summary);
        Assert.Contains("blocked=1", summary);
        Assert.Contains("masked=1", summary);
        Assert.Contains("limited=1", summary);
        Assert.Contains("rate_limited=1", summary);
        Assert.Contains("err=1", summary);
        Assert.Contains("pool=1/2", summary);
        Assert.Contains("cf_pool=1/2", summary);
        Assert.Contains("up=42.0B", summary);
        Assert.Contains("down=84.0B", summary);
    }

    [Fact]
    public void ProxyStats_Summary_Reports_NotAvailable_Without_Pool_Activity()
    {
        var summary = new ProxyStats().Summary();

        Assert.Contains("pool=n/a", summary);
        Assert.Contains("cf_pool=n/a", summary);
    }

    /// <summary>
    /// Формирует валидный MTProto init: 8 случайных байт, prekey/iv, зашифрованный хвост.
    /// </summary>
    private static byte[] BuildClientInit(byte[] secret, byte[] protoTag, short dcIdx)
    {
        var init = new byte[Protocol.HandshakeLen];
        for (var i = 0; i < 56; i++)
        {
            init[i] = (byte)(i + 1);
        }

        var decKey = System.Security.Cryptography.SHA256.HashData([.. init.AsSpan(8, 32), .. secret]);
        var iv = init.AsSpan(40, 16).ToArray();

        var tailPlain = new byte[8];
        protoTag.CopyTo(tailPlain, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(tailPlain.AsSpan(4), dcIdx);

        using var cipher = new IncrementalCipher(decKey, iv);
        Span<byte> keystream = stackalloc byte[Protocol.HandshakeLen];
        cipher.UpdateInto(Protocol.Zero64, keystream);
        for (var i = 0; i < 8; i++)
        {
            init[56 + i] = (byte)(tailPlain[i] ^ keystream[56 + i]);
        }

        return init;
    }
}
