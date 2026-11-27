using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты стратегии подключения к CF (порт upstream <c>proxy/cf_websocket.py</c> v1.12.0):
/// охлаждение пары домен+TLS после таймаута, чередование режимов и приоритет TLS
/// по последней удачной попытке.
/// </summary>
public sealed class CfConnectionStrategyTests
{
    private const string Path = "/apiws";
    private const string Domain = "cf.example";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ConnectAsync_WhenTlsTimesOut_SwitchesDomainToPlainAfterCooldown()
    {
        var factory = new SwitchingFactory(failTls: true);
        var strategy = CreateStrategy(factory);

        var connected = await ConnectAsync(strategy, secure: true);

        Assert.NotNull(connected);
        Assert.Equal(Domain, connected.Value.Name);

        // Первая попытка — TLS (приоритет по умолчанию), вторая — уже без TLS.
        Assert.Equal(new[] { true, false }, factory.SecureModes);

        // Пара (домен, TLS) в охлаждении: H2-пул не должен ходить на этот домен по TLS.
        Assert.True(strategy.TlsInCooldown(Domain));
    }

    [Fact]
    public async Task ConnectAsync_WhenPlainSucceeded_PrefersPlainForNextCall()
    {
        var factory = new SwitchingFactory(failTls: true);
        var strategy = CreateStrategy(factory);

        await ConnectAsync(strategy, secure: true);

        Assert.False(strategy.PrefersTls());

        await ConnectAsync(strategy, secure: true);

        // Второй вызов начинает с plain: TLS-пара ещё остывает, а plain работает.
        Assert.Equal(new[] { true, false, false }, factory.SecureModes);
    }

    [Fact]
    public async Task ConnectAsync_WhenBothModesTimedOut_ReturnsNullAndSkipsCooledPairs()
    {
        var factory = new SwitchingFactory(failAll: true);
        var strategy = CreateStrategy(factory);

        Assert.Null(await ConnectAsync(strategy, secure: true));

        // Лестница таймаутов исчерпана: обе пары (TLS и plain) остывают, и повторный
        // вызов на том же домене сразу возвращает null, не тратя ещё один таймаут.
        var attempts = factory.Attempts;

        Assert.Null(await ConnectAsync(strategy, secure: true));
        Assert.Equal(attempts, factory.Attempts);
    }

    [Fact]
    public async Task Reset_ClearsCooldownsAndTlsPreference()
    {
        var factory = new SwitchingFactory(failTls: true);
        var strategy = CreateStrategy(factory);

        await ConnectAsync(strategy, secure: true);
        strategy.Reset();

        Assert.False(strategy.TlsInCooldown(Domain));
        Assert.True(strategy.PrefersTls());
    }

    [Fact]
    public async Task ConnectAsync_NoSecureMode_NeverUsesTls()
    {
        var factory = new SwitchingFactory();
        var strategy = CreateStrategy(factory);

        var connected = await ConnectAsync(strategy, secure: false);

        Assert.NotNull(connected);
        Assert.Equal(new[] { false }, factory.SecureModes);
    }

    private static Task<(IRawWebSocket Ws, string Name)?> ConnectAsync(CfConnectionStrategy strategy, bool secure)
        => strategy.ConnectAsync([Domain], name => name, "DC2", secure, Path, Timeout, CancellationToken.None);

    private static CfConnectionStrategy CreateStrategy(SwitchingFactory factory)
        => new(factory, NullLogger<CfConnectionStrategy>.Instance);

    /// <summary>
    /// Фабрика, которой «не везёт» с TLS: без TLS соединение удаётся всегда,
    /// по TLS — таймаут (или любой сбой при <paramref name="failAll"/>).
    /// </summary>
    private sealed class SwitchingFactory(bool failTls = false, bool failAll = false) : IRawWebSocketFactory
    {
        private readonly List<bool> secureModes = [];

        public IReadOnlyList<bool> SecureModes
        {
            get
            {
                lock (secureModes)
                {
                    return [.. secureModes];
                }
            }
        }

        public int Attempts
        {
            get
            {
                lock (secureModes)
                {
                    return secureModes.Count;
                }
            }
        }

        public Task<IRawWebSocket> Connect(
            string ip,
            string domain,
            string path,
            string scope,
            TimeSpan? timeout = null,
            bool secure = true,
            string? sni = null)
        {
            lock (secureModes)
            {
                secureModes.Add(secure);
            }

            if (failAll || (secure && failTls))
            {
                throw new TimeoutException(secure ? "tls handshake timed out" : "connect timed out");
            }

            return Task.FromResult<IRawWebSocket>(new StubRawWebSocket());
        }
    }

    /// <summary>Соединение без сети.</summary>
    private sealed class StubRawWebSocket : IRawWebSocket
    {
        public bool IsClosed => false;

        public bool IsRemoteClosed => false;

        public Task Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendInit(ReadOnlyMemory<byte> data) => Task.CompletedTask;

        public Task SendBatch(IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]?> Recv(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task Close(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
