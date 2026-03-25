using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure;

namespace TgWsProxy.Test;

public class UnitTest
{
    [Fact]
    public void CliParser_Parses_MultipleAuth_AndFlags()
    {
        var cfg = CliParser.Parse([
            "--host", "127.0.0.1",
            "--port", "1081",
            "--auth", "u1:p1",
            "--dc-ip", "2:149.154.167.220",
            "--auth", "u2:p2",
            "--verbose"
        ]);

        Assert.Equal("127.0.0.1", cfg.Host);
        Assert.Equal(1081, cfg.Port);
        Assert.True(cfg.Verbose);
        Assert.Equal(2, cfg.Credentials.Count);
        Assert.Equal("u1", cfg.Credentials[0].Login);
        Assert.Equal("p2", cfg.Credentials[1].Password);
    }

    [Fact]
    public void CliParser_Throws_On_UnknownArg() => Assert.Throws<ArgumentException>(() => CliParser.Parse(["--unknown", "x"]));

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
    public void MtProtoInspector_ReturnsNull_OnGarbage()
    {
        var inspector = new MtProtoInspector(new NullLogger<MtProtoInspector>());
        var data = new byte[64];
        var (Dc, _) = inspector.DcFromInit(data);
        Assert.Null(Dc);
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
        var inspector = new MtProtoInspector(new NullLogger<MtProtoInspector>());
        var data = System.Text.Encoding.ASCII.GetBytes(line);

        var actual = inspector.IsHttpTransport(data);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MtProtoInspector_AesCtr_ReturnsRequestedLength()
    {
        var inspector = new MtProtoInspector(new NullLogger<MtProtoInspector>());
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var iv = Enumerable.Range(0, 16).Select(i => (byte)(255 - i)).ToArray();

        var stream = inspector.AesCtr(key, iv, 64);

        Assert.Equal(64, stream.Length);
        Assert.NotEqual(new byte[64], stream);
    }

    [Fact]
    public void MtProtoInspector_PatchInitDc_LeavesShortPacketUnchanged()
    {
        var inspector = new MtProtoInspector(new NullLogger<MtProtoInspector>());
        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var patched = inspector.PatchInitDc(data, -2);

        Assert.Equal(data, patched);
    }

    [Fact]
    public void ProxyStats_Summary_ReflectsCounters()
    {
        var stats = new ProxyStats();
        stats.IncConnectionsTotal();
        stats.IncConnectionsWs();
        stats.IncConnectionsTcpFallback();
        stats.IncConnectionsHttpRejected();
        stats.IncConnectionsPassthrough();
        stats.IncWsErrors();
        stats.IncPoolHit();
        stats.IncPoolMiss();
        stats.AddBytesUp(42);
        stats.AddBytesDown(84);

        var summary = stats.Summary();

        Assert.Contains("total=1", summary);
        Assert.Contains("ws=1", summary);
        Assert.Contains("tcp_fb=1", summary);
        Assert.Contains("http_skip=1", summary);
        Assert.Contains("pass=1", summary);
        Assert.Contains("err=1", summary);
        Assert.Contains("pool=1/2", summary);
        Assert.Contains("up=42B", summary);
        Assert.Contains("down=84B", summary);
    }

    [Fact]
    public async Task TcpBridgeService_Fallback_Unreachable_DoesNotThrow()
    {
        var service = new TcpBridgeService(new NullLogger<TcpBridgeService>(), new ProxyStatsStub());
        using var client = new TcpClient();
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var acceptedTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await acceptedTask;
        await using var stream = client.GetStream();
        var ex = await Record.ExceptionAsync(() =>
            service.TcpFallbackAsync(stream, "127.0.0.1", 1, new byte[64], "test-scope", CancellationToken.None));

        Assert.Null(ex);
        listener.Stop();
    }

    private sealed class ProxyStatsStub : IProxyStats
    {
        public void AddBytesDown(long bytes) { }
        public void AddBytesUp(long bytes) { }
        public void IncConnectionsHttpRejected() { }
        public void IncConnectionsPassthrough() { }
        public void IncConnectionsTcpFallback() { }
        public void IncConnectionsTotal() { }
        public void IncConnectionsWs() { }
        public void IncPoolHit() { }
        public void IncPoolMiss() { }
        public void IncWsErrors() { }
        public string Summary() => string.Empty;
    }
}
