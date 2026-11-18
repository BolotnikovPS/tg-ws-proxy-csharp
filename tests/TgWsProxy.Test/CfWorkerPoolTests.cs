using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты самовосстановления пула CF Worker: промах и ротация запускают наполнение,
/// а серия неудачных подключений включает backoff (upstream 1636903c).
/// </summary>
public sealed class CfWorkerPoolTests
{
    private static readonly string[] Domains = ["worker.example"];
    private const string FallbackDst = "149.154.167.220";

    [Fact]
    public async Task TryGet_OnMiss_SchedulesRefillAndPoolBecomesUsable()
    {
        var factory = new RecordingWsFactory();
        using var pool = CreatePool(factory);

        // Первый запрос промахивается: пул пуст. До v1.11.4 на этом всё и заканчивалось —
        // наполнение запускалось только при попадании, то есть уже никогда.
        Assert.Null(pool.TryGet(2, FallbackDst, Domains));

        Assert.True(
            await WaitUntilAsync(() => factory.CallCount >= 1),
            "промах не запустил наполнение пула");

        Assert.True(
            await WaitUntilAsync(() => pool.TryGet(2, FallbackDst, Domains) is not null),
            "пул не наполнился после промаха");
    }

    [Fact]
    public async Task TryGet_WhenRefillKeepsFailing_BacksOffInsteadOfHammering()
    {
        var factory = new RecordingWsFactory(fail: true);
        using var pool = CreatePool(factory);

        Assert.Null(pool.TryGet(2, FallbackDst, Domains));
        Assert.True(
            await WaitUntilAsync(() => factory.CallCount == 1),
            "наполнение не было предпринято");

        // После неудачи включается backoff: промахи не должны сразу же бить по воркерам.
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(pool.TryGet(2, FallbackDst, Domains));
        }

        await Task.Delay(100);
        Assert.Equal(1, factory.CallCount);
    }

    [Fact]
    public async Task Reset_ClosesIdleWorkerConnections()
    {
        var factory = new RecordingWsFactory();
        var pool = CreatePool(factory);

        Assert.Null(pool.TryGet(2, FallbackDst, Domains));
        Assert.True(
            await WaitUntilAsync(() => factory.CreatedCount == 1),
            "соединение воркера не было создано");

        pool.Reset();

        Assert.True(
            await WaitUntilAsync(() => factory.Created[0].CloseCalled),
            "Reset не закрыл простаивающее соединение");
        pool.Dispose();
    }

    private static CfWorkerPool CreatePool(RecordingWsFactory factory)
        => new(
            TestConfig.Create(config => config.MtProto.WsPoolSize = 1),
            [],
            factory,
            new NullStats(),
            NullLogger<CfWorkerPool>.Instance);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Фабрика соединений, считающая попытки подключения к воркеру.</summary>
    private sealed class RecordingWsFactory(bool fail = false) : IRawWebSocketFactory
    {
        private readonly List<FakeRawWebSocket> created = [];
        private int calls;

        public int CallCount => Volatile.Read(ref calls);

        public IReadOnlyList<FakeRawWebSocket> Created
        {
            get
            {
                lock (created)
                {
                    return [.. created];
                }
            }
        }

        public int CreatedCount => Created.Count;

        public Task<IRawWebSocket> Connect(
            string ip,
            string domain,
            string path,
            string scope,
            TimeSpan? timeout = null,
            bool secure = true,
            string? sni = null)
        {
            Interlocked.Increment(ref calls);
            if (fail)
            {
                throw new IOException("connect failed");
            }

            var ws = new FakeRawWebSocket();
            lock (created)
            {
                created.Add(ws);
            }

            return Task.FromResult<IRawWebSocket>(ws);
        }
    }

    /// <summary>Соединение воркера без сети.</summary>
    private sealed class FakeRawWebSocket : IRawWebSocket
    {
        public bool IsClosed { get; set; }

        public bool IsRemoteClosed { get; set; }

        public bool CloseCalled { get; private set; }

        public Task Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendBatch(IReadOnlyList<ReadOnlyMemory<byte>> frames, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]?> Recv(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task Close(CancellationToken cancellationToken)
        {
            CloseCalled = true;
            IsClosed = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Статистика-заглушка: пулу важны только счётчики попаданий.</summary>
    private sealed class NullStats : IProxyStats
    {
        public void IncConnectionsTotal()
        {
        }

        public void IncConnectionsActive()
        {
        }

        public void DecConnectionsActive()
        {
        }

        public void IncConnectionsWs()
        {
        }

        public void IncConnectionsH2()
        {
        }

        public void IncH2TcpConnections()
        {
        }

        public void IncH2Requests()
        {
        }

        public void IncH2Errors()
        {
        }

        public void IncConnectionsTcpFallback()
        {
        }

        public void IncConnectionsCfProxy()
        {
        }

        public void IncConnectionsFronting()
        {
        }

        public void IncConnectionsBad()
        {
        }

        public void IncConnectionsBlocked()
        {
        }

        public void IncConnectionsMasked()
        {
        }

        public void IncConnectionsLimited()
        {
        }

        public void IncConnectionsRateLimited()
        {
        }

        public void IncWsErrors()
        {
        }

        public void IncPoolHit()
        {
        }

        public void IncPoolMiss()
        {
        }

        public void IncCfPoolHit()
        {
        }

        public void IncCfPoolMiss()
        {
        }

        public void AddBytesUp(long bytes)
        {
        }

        public void AddBytesDown(long bytes)
        {
        }

        public string Summary() => string.Empty;

        public StatsSnapshot Snapshot() => new();
    }
}
