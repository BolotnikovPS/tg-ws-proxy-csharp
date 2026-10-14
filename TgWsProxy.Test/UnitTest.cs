using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.Logic.Helpers;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты CLI/конфигурации, MTProto handshake, статистики и разборщика пакетов.
/// Ожидаемые значения соответствуют upstream Flowseal/tg-ws-proxy v1.11.2.
/// </summary>
public class UnitTest
{
    [Fact]
    public void CliParser_Uses_Upstream_Defaults()
    {
        var cfg = CliParser.Parse([]);

        Assert.Equal(1443, cfg.Port);
        Assert.Equal("127.0.0.1", cfg.Host);
        Assert.Equal(4, cfg.WsPoolSize);
        Assert.Equal(5, cfg.LogMaxMegabytes);
        Assert.Equal(1, cfg.LogRetainedFileCount);
        Assert.Equal(256, cfg.SocketBufferKb);
        Assert.True(cfg.CfProxyEnabled);
        Assert.True(cfg.CfProxyH2Media);
        Assert.Equal(CliParser.DefaultDcIp, cfg.DcIp);
    }

    [Fact]
    public void CliParser_Parses_Host_Port_And_Flags()
    {
        var cfg = CliParser.Parse([
            "--host", "0.0.0.0",
            "--port", "1081",
            "--dc-ip", "2:149.154.167.220",
            "--verbose"
        ]);

        Assert.Equal("0.0.0.0", cfg.Host);
        Assert.Equal(1081, cfg.Port);
        Assert.True(cfg.Verbose);
        Assert.Equal(["2:149.154.167.220"], cfg.DcIp);
    }

    [Fact]
    public void CliParser_Bare_DcIp_Clears_List()
    {
        var cfg = CliParser.Parse(["--dc-ip"]);

        Assert.Empty(cfg.DcIp);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void CliParser_Help_Flags_Are_Recognized(string flag)
    {
        Assert.True(CliParser.TryHandleInfo([flag], "2.3", out var output));
        Assert.Contains("usage: TgWsProxy", output);
        Assert.Contains("--proxy-protocol", output);
    }

    [Fact]
    public void CliParser_Version_Flag_Prints_Version()
    {
        Assert.True(CliParser.TryHandleInfo(["--version"], "2.3", out var output));
        Assert.Equal("tg-ws-proxy 2.3", output);
    }

    [Fact]
    public void CliParser_Info_Flags_Not_Present_For_Regular_Args()
    {
        Assert.False(CliParser.TryHandleInfo(["--port", "1443"], "2.3", out var output));
        Assert.Empty(output);
    }

    [Fact]
    public void CliParser_Help_Text_Documents_All_Supported_Flags()
    {
        // Справка должна перечислять каждый параметр, который реально понимает парсер.
        string[] expected =
        [
            "--port", "--host", "--secret", "--dc-ip", "--cfproxy-domain",
            "--cfproxy-worker-domain", "--no-cfproxy", "--no-h2", "--no-secure",
            "--fake-tls-domain", "--force-test-dc", "--proxy-protocol", "--verbose",
            "--log-file", "--log-max-mb", "--log-backups", "--buf-kb", "--pool-size",
            "--ws-timeout", "--ws-max-frame-bytes", "--allow-invalid-certs"
        ];

        foreach (var flag in expected)
        {
            Assert.Contains(flag, CliParser.HelpText);
        }
    }

    [Fact]
    public void CliParser_Repeated_DcIp_Is_Accumulated()
    {
        var cfg = CliParser.Parse([
            "--dc-ip", "2:149.154.167.220",
            "--dc-ip", "4:149.154.167.220"
        ]);

        Assert.Equal(2, cfg.DcIp.Count);
    }

    [Fact]
    public void CliParser_Rejects_Removed_Upstream_Flags()
    {
        // --auth и --cfproxy-priority отсутствуют в upstream и удалены из порта.
        Assert.Throws<ArgumentException>(() => CliParser.Parse(["--auth", "u:p"]));
        Assert.Throws<ArgumentException>(() => CliParser.Parse(["--cfproxy-priority", "cf"]));
    }

    [Fact]
    public void CliParser_Throws_OnUnknownArg() => Assert.Throws<ArgumentException>(() => CliParser.Parse(["--unknown", "x"]));

    [Fact]
    public void CliParser_Parses_CfProxyDomains_With_Splitting_And_Dedupe()
    {
        var cfg = CliParser.Parse([
            "--cfproxy-domain", "a.example.com,b.example.com",
            "--cfproxy-domain", "A.EXAMPLE.COM;c.example.com"
        ]);

        Assert.Equal(["a.example.com", "b.example.com", "c.example.com"], cfg.CfProxyDomains);
    }

    [Fact]
    public void CliParser_Parses_Worker_Domains_And_Secure_Flags()
    {
        var cfg = CliParser.Parse([
            "--cfproxy-worker-domain", "w.example.com",
            "--no-secure",
            "--no-h2",
            "--proxy-protocol",
            "--force-test-dc",
            "--fake-tls-domain", "www.google.com"
        ]);

        Assert.Equal(["w.example.com"], cfg.CfProxyWorkerDomains);
        Assert.True(cfg.DisableSecure);
        Assert.False(cfg.CfProxyH2Media);
        Assert.True(cfg.ProxyProtocol);
        Assert.True(cfg.ForceTestDc);
        Assert.Equal("www.google.com", cfg.FakeTlsDomain);
    }

    [Fact]
    public void Config_H2Enabled_Requires_Secure_CfProxy_And_No_TestDc()
    {
        Assert.True(new Config().H2Enabled);
        Assert.False(new Config { DisableSecure = true }.H2Enabled);
        Assert.False(new Config { CfProxyEnabled = false }.H2Enabled);
        Assert.False(new Config { ForceTestDc = true }.H2Enabled);
        Assert.False(new Config { CfProxyH2Media = false }.H2Enabled);
    }

    [Fact]
    public void CliParser_Keeps_Secret_As_Is()
    {
        var secret = new string('a', 32);
        var cfg = CliParser.Parse(["--secret", secret]);

        Assert.Equal(secret, cfg.Secret);
    }

    [Fact]
    public void ConfigValidator_Rejects_BadPort()
    {
        var cfg = new Config { Port = 70000 };
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(cfg));
    }

    [Fact]
    public void CliParser_Parses_WsTimeout()
    {
        var cfg = CliParser.Parse(["--ws-timeout", "60"]);
        Assert.Equal(60, cfg.WsConnectTimeoutSeconds);
    }

    [Fact]
    public void ConfigValidator_Rejects_WsTimeout_OutOfRange()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(new Config { WsConnectTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(new Config { WsConnectTimeoutSeconds = 400 }));
    }

    [Fact]
    public void CliParser_Parses_BufKb_PoolSize_LogRotation()
    {
        var cfg = CliParser.Parse([
            "--buf-kb", "512",
            "--pool-size", "2",
            "--log-max-mb", "5",
            "--log-backups", "3"
        ]);
        Assert.Equal(512, cfg.SocketBufferKb);
        Assert.Equal(2, cfg.WsPoolSize);
        Assert.Equal(5, cfg.LogMaxMegabytes);
        Assert.Equal(3, cfg.LogRetainedFileCount);
    }

    [Fact]
    public void Config_Default_WsTimeout_Is_Ten_Seconds()
        => Assert.Equal(10, new Config().WsConnectTimeoutSeconds);

    [Fact]
    public void Config_Default_WsMaxFrame_Is_Sixteen_MiB()
        => Assert.Equal(16 * 1024 * 1024, new Config().WsMaxFrameBytes);

    [Fact]
    public void ConfigValidator_Rejects_NonHex_Secret()
    {
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(new Config { Secret = "not-a-hex-secret" }));
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(new Config { Secret = new string('a', 31) }));
    }

    [Fact]
    public void ConfigValidator_Accepts_Empty_Secret_For_Autogeneration()
        => ConfigValidator.Validate(new Config { Secret = "" });

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
        stats.IncConnectionsMasked();
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
        Assert.Contains("masked=1", summary);
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
