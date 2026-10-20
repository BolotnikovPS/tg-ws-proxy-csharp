using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Пул соединений через Cloudflare Worker (порт upstream <c>_CfWorkerPool</c>).
/// Воркер принимает путь <c>/apiws?dst=...&amp;dc=...</c> и проксирует трафик в нужный DC.
/// </summary>
internal sealed class CfWorkerPool(
    Config config,
    Dictionary<int, string> dcOpt,
    IRawWebSocketFactory wsFactory,
    IProxyStats stats,
    ILogger<CfWorkerPool> logger) : ICfWorkerPool
{
    /// <summary>Максимальный возраст соединения воркера (WS_POOL_MAX_AGE).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(100);

    /// <summary>Максимум соединений на один DC (PER_DC_LIMIT).</summary>
    private const int PerDcLimit = 1;

    /// <summary>Таймаут подключения к воркеру (секунды).</summary>
    private const int ConnectTimeoutSeconds = 8;

    private readonly ConcurrentDictionary<int, ConcurrentQueue<PooledWorkerWs>> idle = new();
    private readonly ConcurrentDictionary<int, byte> refilling = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> exhaustedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    /// <inheritdoc />
    public (IRawWebSocket Ws, string Domain)? TryGet(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (config.WsPoolSize <= 0 || workerDomains.Count == 0)
        {
            return null;
        }

        var bucket = idle.GetOrAdd(dc, _ => new ConcurrentQueue<PooledWorkerWs>());
        var now = DateTimeOffset.UtcNow;

        while (bucket.TryDequeue(out var entry))
        {
            if (IsStale(entry, now))
            {
                CloseQuietly(entry.Ws);
                continue;
            }

            stats.IncCfPoolHit();
            logger.LogDebug(
                "CF worker pool hit DC{Dc} via {Domain} (age={Age:F1}s, left={Left})",
                dc, entry.Domain, (now - entry.Created).TotalSeconds, bucket.Count);
            ScheduleRefill(dc, fallbackDst, workerDomains);
            return (entry.Ws, entry.Domain);
        }

        stats.IncCfPoolMiss();
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableDomains(IReadOnlyList<string> workerDomains)
    {
        var now = DateTimeOffset.UtcNow;
        var domains = new List<string>(workerDomains.Count);
        foreach (var domain in workerDomains)
        {
            if (domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exhaustedUntil.TryGetValue(domain, out var until))
            {
                if (until > now)
                {
                    continue;
                }

                exhaustedUntil.TryRemove(domain, out _);
            }

            domains.Add(domain);
        }

        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(domains));
        return domains;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Соответствует upstream, где обработка лимита воркера (HTTP 429) закомментирована как TODO.
    /// </remarks>
    public void ReportFailure(string workerDomain, Exception exception)
    {
        logger.LogDebug(exception, "CF worker {Domain} connect failed", workerDomain);
    }

    /// <inheritdoc />
    public void Warmup()
    {
        if (config.WsPoolSize <= 0 || config.CfProxyWorkerDomains.Count == 0)
        {
            return;
        }

        var scheduled = 0;
        foreach (var (dc, fallbackDst) in Protocol.DcDefaultIps)
        {
            if (dcOpt.ContainsKey(dc))
            {
                continue;
            }

            ScheduleRefill(dc, fallbackDst, config.CfProxyWorkerDomains);
            scheduled++;
        }

        if (scheduled > 0)
        {
            logger.LogInformation("CF worker pool warmup started for {Count} DC(s)", scheduled);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        foreach (var bucket in idle.Values)
        {
            while (bucket.TryDequeue(out var entry))
            {
                CloseQuietly(entry.Ws);
            }
        }

        idle.Clear();
        refilling.Clear();
        exhaustedUntil.Clear();
    }

    /// <summary>
    /// Планирует фоновое наполнение пула для DC, если оно ещё не запущено.
    /// </summary>
    private void ScheduleRefill(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (config.WsPoolSize <= 0 || !refilling.TryAdd(dc, 0))
        {
            return;
        }

        var domains = workerDomains.ToArray();
        BackgroundTaskRunner.RunDetachedSafe(
            ct => RefillAsync(dc, fallbackDst, domains, ct),
            logger,
            $"[cf-pool] refill DC{dc}",
            lifetime.Token);
    }

    /// <summary>
    /// Открывает недостающие соединения воркера и складывает их в пул.
    /// </summary>
    private async Task RefillAsync(int dc, string fallbackDst, string[] workerDomains, CancellationToken cancellationToken)
    {
        try
        {
            var bucket = idle.GetOrAdd(dc, _ => new ConcurrentQueue<PooledWorkerWs>());
            var targetSize = Math.Min(config.WsPoolSize, PerDcLimit);
            var needed = targetSize - bucket.Count;
            if (needed <= 0)
            {
                return;
            }

            for (var i = 0; i < needed; i++)
            {
                var connected = await ConnectOneAsync(workerDomains, fallbackDst, dc, cancellationToken).ConfigureAwait(false);
                if (connected is null)
                {
                    break;
                }

                bucket.Enqueue(new PooledWorkerWs(connected.Value.Ws, DateTimeOffset.UtcNow, connected.Value.Domain));
            }

            logger.LogDebug("CF worker pool refilled DC{Dc}: {Count} ready", dc, bucket.Count);
        }
        finally
        {
            refilling.TryRemove(dc, out _);
        }
    }

    /// <summary>
    /// Пытается открыть одно соединение воркера, перебирая доступные домены.
    /// </summary>
    private async Task<(IRawWebSocket Ws, string Domain)?> ConnectOneAsync(
        string[] workerDomains,
        string fallbackDst,
        int dc,
        CancellationToken cancellationToken)
    {
        var path = $"{Protocol.WsPath}?dst={Uri.EscapeDataString(fallbackDst)}&dc={dc}";

        foreach (var workerDomain in AvailableDomains(workerDomains))
        {
            try
            {
                var ws = await wsFactory
                    .Connect(
                        workerDomain,
                        workerDomain,
                        path,
                        $"cf-worker DC{dc}",
                        TimeSpan.FromSeconds(ConnectTimeoutSeconds),
                        secure: !config.DisableSecure)
                    .ConfigureAwait(false);
                return (ws, workerDomain);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                ReportFailure(workerDomain, ex);
            }
        }

        return null;
    }

    /// <summary>
    /// Проверяет протухание соединения: возраст, локальное закрытие и закрытие
    /// со стороны сервера (upstream <c>ws.writer.transport.is_closing()</c>).
    /// </summary>
    private static bool IsStale(PooledWorkerWs entry, DateTimeOffset now)
        => entry.Ws.IsClosed
            || entry.Ws.IsRemoteClosed
            || now - entry.Created >= MaxAge;

    private void CloseQuietly(IRawWebSocket ws)
        => BackgroundTaskRunner.RunDetachedSafe(
            async ct =>
            {
                try
                {
                    await ws.Close(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Соединение уже мертво — закрытие не критично.
                }
            },
            logger,
            "[cf-pool] quiet close",
            CancellationToken.None);

    /// <summary>Соединение воркера вместе со временем создания и доменом.</summary>
    private readonly record struct PooledWorkerWs(IRawWebSocket Ws, DateTimeOffset Created, string Domain);
}
