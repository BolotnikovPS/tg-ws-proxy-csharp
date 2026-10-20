using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Hosting;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.Metrics;
using TgWsProxy.Infrastructure.Session;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Test;

/// <summary>
/// Проверяет жизненный цикл <see cref="ProxyHostedService"/>: старт прокси как hosted-сервиса (F-1.1),
/// идемпотентность старта и порядок остановки из F-1.3.
/// </summary>
public class ProxyHostedServiceTests
{
    [Fact]
    public async Task StartAsync_AppliesStartupHooksAndStartsProxy()
    {
        var log = new CallLog();
        var server = new StubProxyServer();
        var (service, _) = CreateService(server, log);
        await using var _service = service;

        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsStarted);
        Assert.False(service.Faulted);
        // Run работает до отмены, поэтому старт не должен блокировать вызывающего.
        Assert.False(service.Completion.IsCompleted);

        var calls = log.Snapshot();
        Assert.Contains("bridge.ResetTcpBackoff", calls);
        Assert.Contains("wsPool.Reset", calls);
        Assert.Contains("cfWorkerPool.Reset", calls);
        Assert.Contains("cfDomainRefresher.Start", calls);
        Assert.Contains("session.Warmup", calls);
        Assert.Contains("cfWorkerPool.Warmup", calls);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var log = new CallLog();
        var (service, _) = CreateService(new StubProxyServer(), log);
        await using var _service = service;

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        // Повторный старт не должен ни перезапускать прокси, ни дублировать прогрев.
        Assert.Single(log.Snapshot(), call => call == "wsPool.Reset");
        Assert.Single(log.Snapshot(), call => call == "session.Warmup");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShutsDownInF13Order()
    {
        var log = new CallLog();
        var server = new StubProxyServer();
        var (service, _) = CreateService(server, log);
        await using var _service = service;

        await service.StartAsync(CancellationToken.None);
        log.Clear();

        await service.StopAsync(CancellationToken.None);

        // F-1.3: cfDomainRefresher.Stop → wsPool.Reset → cfWorkerPool.Reset → cfH2Pool.CloseAsync.
        Assert.Equal(
            new[] { "cfDomainRefresher.Stop", "wsPool.Reset", "cfWorkerPool.Reset", "cfH2Pool.Close" },
            log.Snapshot());

        Assert.True(service.Completion.IsCompleted);
        Assert.False(service.Faulted);
        // Токен цикла приёма соединений отменён, поэтому Run завершился штатно.
        Assert.True(server.CapturedToken?.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_IsNoOp()
    {
        var log = new CallLog();
        var (service, _) = CreateService(new StubProxyServer(), log);
        await using var _service = service;

        await service.StopAsync(CancellationToken.None);

        Assert.False(service.IsStarted);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public async Task Faulted_WhenProxyServerThrows()
    {
        var log = new CallLog();
        var server = new StubProxyServer
        {
            RunBehavior = _ => Task.FromException(new InvalidOperationException("boom")),
        };
        var (service, _) = CreateService(server, log);
        await using var _service = service;

        await service.StartAsync(CancellationToken.None);
        await service.Completion;

        // Авария цикла приёма помечается, чтобы точка входа вернула код 1.
        Assert.True(service.Faulted);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RecordsInitialHistorySample()
    {
        var log = new CallLog();
        var (service, history) = CreateService(new StubProxyServer(), log);
        await using var _service = service;

        await service.StartAsync(CancellationToken.None);

        // Сэмплер истории стартует с немедленной записью, чтобы график не пустовал после рестарта.
        var series = history.Query(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow);
        Assert.NotEmpty(series.Points);

        await service.StopAsync(CancellationToken.None);
    }

    private static (ProxyHostedService Service, MetricsHistory History) CreateService(
        IProxyServer server,
        CallLog log,
        IActiveSessionRegistry? sessions = null,
        Config? config = null)
    {
        var history = new MetricsHistory();
        var liveConfig = config ?? new Config();
        var service = new ProxyHostedService(
            server,
            new StubSessionHandler(log),
            new ProxyStats(),
            new StubWsPool(log),
            new StubCfWorkerPool(log),
            new StubCfDomainRefresher(log),
            new StubCfH2Pool(log),
            new StubBridgeService(log),
            sessions ?? new ActiveSessionRegistry(NullLogger<ActiveSessionRegistry>.Instance),
            history,
            liveConfig,
            ConfigDefaults.ParseDcIpList(liveConfig.DcIp),
            NullLogger<ProxyHostedService>.Instance);
        return (service, history);
    }

    /// <summary>Потокобезопасный журнал вызовов заглушек: фиксирует порядок операций.</summary>
    private sealed class CallLog
    {
        private readonly object sync = new();
        private readonly List<string> calls = [];

        public void Add(string call)
        {
            lock (sync)
            {
                calls.Add(call);
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (sync)
            {
                return calls.ToArray();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                calls.Clear();
            }
        }
    }

    /// <summary><see cref="IProxyServer"/> с настраиваемым поведением <c>Run</c> и <c>RebindAsync</c>.</summary>
    private sealed class StubProxyServer : IProxyServer
    {
        public bool IsListening => true;

        public Func<CancellationToken, Task>? RunBehavior { get; set; }

        public CancellationToken? CapturedToken { get; private set; }

        /// <summary>Адрес, на который запрошена перепривязка (F-7.1).</summary>
        public IPEndPoint? ReboundEndpoint { get; private set; }

        /// <summary>Имитирует занятый порт: сервер уже откатил привязку и сообщает об ошибке (F-7.5).</summary>
        public Exception? RebindFailure { get; set; }

        public Task Run(CancellationToken cancellationToken)
        {
            CapturedToken = cancellationToken;
            return RunBehavior?.Invoke(cancellationToken) ?? WaitForCancellationAsync(cancellationToken);
        }

        public async Task RebindAsync(
            IPEndPoint endpoint,
            Func<CancellationToken, Task>? betweenStopAndBind,
            CancellationToken cancellationToken)
        {
            ReboundEndpoint = endpoint;

            if (RebindFailure is not null)
            {
                throw RebindFailure;
            }

            if (betweenStopAndBind is not null)
            {
                await betweenStopAndBind(cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StubSessionHandler(CallLog log) : IClientSessionHandler
    {
        public Task Handle(
            Socket client,
            ClientContext context,
            ISessionTokenBinding? session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Handle не вызывается в тестах hosted-сервиса.");

        public void Warmup(CancellationToken cancellationToken) => log.Add("session.Warmup");
    }

    private sealed class StubWsPool(CallLog log) : IWsPool
    {
        public IRawWebSocket? TryGet(int dc, bool isMedia, bool isTestDc) => null;

        public void Warmup() => log.Add("wsPool.Warmup");

        public void Reset() => log.Add("wsPool.Reset");

        public string FormatPoolSummary() => string.Empty;
    }

    private sealed class StubCfWorkerPool(CallLog log) : ICfWorkerPool
    {
        public (IRawWebSocket Ws, string Domain)? TryGet(
            int dc,
            string fallbackDst,
            IReadOnlyList<string> workerDomains) => null;

        public IReadOnlyList<string> AvailableDomains(IReadOnlyList<string> workerDomains) => [];

        public void ReportFailure(string workerDomain, Exception exception)
        {
        }

        public void Warmup() => log.Add("cfWorkerPool.Warmup");

        public void Reset() => log.Add("cfWorkerPool.Reset");
    }

    private sealed class StubCfDomainRefresher(CallLog log) : ICfDomainRefresher
    {
        public void Start(CancellationToken cancellationToken) => log.Add("cfDomainRefresher.Start");

        public void Stop() => log.Add("cfDomainRefresher.Stop");
    }

    private sealed class StubCfH2Pool(CallLog log) : ICfH2Pool
    {
        public Task<ICfH2Channel?> OpenAsync(int dc, string label, CancellationToken cancellationToken) =>
            Task.FromResult<ICfH2Channel?>(null);

        public Task CloseAsync()
        {
            log.Add("cfH2Pool.Close");
            return Task.CompletedTask;
        }
    }

    private sealed class StubBridgeService(CallLog log) : ITcpBridgeService
    {
        public Task BridgeWsReencrypt(
            Stream client,
            IRawWebSocket ws,
            string scope,
            CryptoCtx ctx,
            int dc,
            bool isMedia,
            MsgSplitter? splitter,
            IPAddress? peer,
            string? tokenId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Мост не вызывается в тестах hosted-сервиса.");

        public Task<bool> DoFallback(FallbackRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public void ResetTcpBackoff() => log.Add("bridge.ResetTcpBackoff");
    }
}
