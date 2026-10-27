using Microsoft.Extensions.Logging;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Session;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="SessionLogScope"/>: структурированная корреляция прокси-сессий через
/// <see cref="ILogger.BeginScope{TState}(TState)"/>.
/// </summary>
public class SessionLogScopeTests
{
    [Fact]
    public void Begin_Pushes_ConnectionId_And_Peer()
    {
        var logger = new RecordingLogger();
        var context = new ClientContext("peer|1", "peer", "000001");

        SessionLogScope.Begin(logger, context);

        var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(logger.Scopes.Single());
        Assert.Equal("000001", state["ConnectionId"]);
        Assert.Equal("peer", state["Peer"]);
        Assert.False(state.ContainsKey("TokenId"));
    }

    [Fact]
    public void Begin_Pushes_TokenId_When_Present()
    {
        var logger = new RecordingLogger();
        var context = new ClientContext("peer|1", "peer", "000001", TokenId: "tok-1");

        SessionLogScope.Begin(logger, context);

        var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(logger.Scopes.Single());
        Assert.Equal("tok-1", state["TokenId"]);
    }

    [Fact]
    public void BeginRoute_Pushes_Token_Dc_Media()
    {
        var logger = new RecordingLogger();

        SessionLogScope.BeginRoute(logger, "tok-2", 5, media: true);

        var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(logger.Scopes.Single());
        Assert.Equal("tok-2", state["TokenId"]);
        Assert.Equal("5", state["Dc"]);
        Assert.Equal(true, state["Media"]);
    }

    [Fact]
    public void BeginRoute_NullDc_RendersDash()
    {
        var logger = new RecordingLogger();

        SessionLogScope.BeginRoute(logger, "tok-2", null, media: false);

        var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(logger.Scopes.Single());
        Assert.Equal("-", state["Dc"]);
        Assert.Equal(false, state["Media"]);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<object?> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}