using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="WebProxySessionManager"/>: выпуск/погашение bootstrap, валидация HELLO,
/// лимит сессий, создание/закрытие и учёт числа живых сессий.
/// </summary>
public class WebProxySessionManagerTests
{
    [Fact]
    public void CreateSession_ReturnsWelcomeAndToken()
    {
        var manager = CreateManager();
        var bootstrap = manager.IssueBootstrap(MakeToken());

        var result = manager.CreateSession(bootstrap, HelloFrame(), null, "test");

        Assert.Equal(WebProxyCreateStatus.Ok, result.Status);
        Assert.NotNull(result.SessionToken);
        Assert.Equal(43, result.SessionToken!.Length);
        Assert.NotNull(result.Welcome);
        Assert.True(RelayFrameCodec.TryParse(result.Welcome!, out var welcome, out _));
        Assert.Equal(RelayFrameType.Welcome, welcome.Type);
        Assert.Equal(0, welcome.StreamId);
        Assert.Equal(1, manager.SessionCount);
    }

    [Fact]
    public void CreateSession_BadHello_ReturnsNotFound()
    {
        var manager = CreateManager();
        var bootstrap = manager.IssueBootstrap(MakeToken());

        var result = manager.CreateSession(bootstrap, new byte[] { 1, 2, 3 }, null, "test");

        Assert.Equal(WebProxyCreateStatus.NotFound, result.Status);
        Assert.Equal(0, manager.SessionCount);
    }

    [Fact]
    public void CreateSession_ReusedBootstrap_ReturnsNotFound()
    {
        var manager = CreateManager();
        var bootstrap = manager.IssueBootstrap(MakeToken());
        var hello = HelloFrame();

        var first = manager.CreateSession(bootstrap, hello, null, "test");
        var second = manager.CreateSession(bootstrap, hello, null, "test");

        Assert.Equal(WebProxyCreateStatus.Ok, first.Status);
        Assert.Equal(WebProxyCreateStatus.NotFound, second.Status);
        Assert.Equal(1, manager.SessionCount);
    }

    [Fact]
    public void CreateSession_ExpiredBootstrap_ReturnsNotFound()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var manager = CreateManager(time);
        var bootstrap = manager.IssueBootstrap(MakeToken());

        time.Advance(TimeSpan.FromMinutes(3)); // TTL bootstrap = 2 минуты
        var result = manager.CreateSession(bootstrap, HelloFrame(), null, "test");

        Assert.Equal(WebProxyCreateStatus.NotFound, result.Status);
        Assert.Equal(0, manager.SessionCount);
    }

    [Fact]
    public void CreateSession_UnknownBootstrap_ReturnsNotFound()
    {
        var manager = CreateManager();

        var result = manager.CreateSession("no-such-bootstrap", HelloFrame(), null, "test");

        Assert.Equal(WebProxyCreateStatus.NotFound, result.Status);
    }

    [Fact]
    public void CloseSession_RemovesAndDecrements()
    {
        var manager = CreateManager();
        var bootstrap = manager.IssueBootstrap(MakeToken());
        var result = manager.CreateSession(bootstrap, HelloFrame(), null, "test");

        var closed = manager.CloseSession(result.SessionToken!);

        Assert.True(closed);
        Assert.Null(manager.GetSession(result.SessionToken!));
        Assert.Equal(0, manager.SessionCount);
    }

    [Fact]
    public void CloseSession_UnknownToken_ReturnsFalse()
    {
        var manager = CreateManager();

        Assert.False(manager.CloseSession("does-not-exist"));
    }

    private static WebProxySessionManager CreateManager(TimeProvider? time = null)
        => new(new FakeRelaySession(), NullLogger<WebProxySessionManager>.Instance, time);

    private static ProxyTokenEntry MakeToken()
    {
        var secret = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        return new ProxyTokenEntry(
            "id-1",
            "token-1",
            Convert.ToHexString(secret).ToLowerInvariant(),
            secret,
            true,
            DateTimeOffset.UtcNow,
            null);
    }

    private static byte[] HelloFrame()
    {
        var buffer = new byte[RelayFrameCodec.FrameSize(1)];
        RelayFrameCodec.Write(buffer, RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });
        return buffer;
    }

    private sealed class FakeRelaySession : IWebProxyRelaySession
    {
        public ProxyTokenEntry? ResolveToken(string host, string? capability) => null;

        public WebProxyRelayLease OpenSession(ProxyTokenEntry token, IPAddress? peer, string scope)
            => new((_, _) => Task.CompletedTask, new CancellationTokenSource().Token, () => { });
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
