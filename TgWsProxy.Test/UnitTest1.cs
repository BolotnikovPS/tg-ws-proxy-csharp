using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure;

namespace TgWsProxy.Test;

public class UnitTest1
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
    public void CliParser_Parses_AuthLogin_And_AuthPassword()
    {
        var cfg = CliParser.Parse([
            "--auth-login", "user",
            "--auth-password", "pass"
        ]);

        Assert.Single(cfg.Credentials);
        Assert.Equal("user", cfg.Credentials[0].Login);
        Assert.Equal("pass", cfg.Credentials[0].Password);
    }

    [Fact]
    public void CliParser_Throws_On_UnknownArg()
    {
        Assert.Throws<ArgumentException>(() => CliParser.Parse(["--unknown", "x"]));
    }

    [Fact]
    public void ConfigValidator_Rejects_BadPort()
    {
        var cfg = new Config { Port = 70000 };
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(cfg));
    }

    [Fact]
    public void MtProtoInspector_ReturnsNull_OnGarbage()
    {
        var inspector = new MtProtoInspector();
        var data = new byte[64];
        var (Dc, IsMedia) = inspector.DcFromInit(data);
        Assert.Null(Dc);
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

        var ex = await Record.ExceptionAsync(() =>
            service.TcpFallbackAsync(client.GetStream(), "127.0.0.1", 1, new byte[64], "test-scope"));

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
