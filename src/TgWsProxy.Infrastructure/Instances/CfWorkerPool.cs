using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.Transport;
using static TgWsProxy.Domain.AppConstants.CfWorkerPool;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Пул соединений через Cloudflare Worker (порт upstream <c>_CfWorkerPool</c>).
/// Воркер принимает путь <c>/apiws?dst=...&amp;dc=...</c> и проксирует трафик в нужный DC.
/// </summary>
/// <remarks>
/// С v1.11.4 пул воркеров обслуживает себя сам, как и пул прямых WS-соединений:
/// промах <see cref="TryGet"/> и периодическая ротация запускают наполнение, а
/// серия неудачных подключений включает экспоненциальный backoff. До этого
/// наполнение запускалось только при попадании в пул и в <see cref="Warmup"/>,
/// поэтому единственная неудача подключения оставляла DC без соединений до
/// перезапуска процесса (upstream 1636903c).
/// </remarks>
internal sealed class CfWorkerPool(
    Config config,
    Dictionary<int, string> dcOpt,
    IRawWebSocketFactory wsFactory,
    IProxyStats stats,
    ILogger<CfWorkerPool> logger) : ICfWorkerPool, IDisposable
{
    /// <summary>Максимальный возраст соединения воркера (WS_POOL_MAX_AGE).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(MaxAgeSeconds);

    /// <summary>Период обслуживания пула (WS_POOL_CHECK_INTERVAL).</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(CheckIntervalSeconds);

    private readonly ConcurrentDictionary<int, ConcurrentQueue<PooledWorkerWs>> idle = new();
    private readonly ConcurrentDictionary<int, byte> refilling = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> exhaustedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>
    /// Параметры наполнения по DC (fallback-DC и список доменов воркера). Нужны
    /// фоновой ротации: она обходит все известные DC, а не только запрошенные.
    /// </summary>
    private readonly ConcurrentDictionary<int, RefillTarget> targets = new();

    /// <summary>Число подряд идущих неудачных наполнений по DC (REFILL_BACKOFF).</summary>
    private readonly ConcurrentDictionary<int, int> refillFailures = new();

    /// <summary>Момент, раньше которого наполнение DC не повторяется (backoff).</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> refillAfter = new();

    /// <summary>Флаг однократного запуска фоновой ротации (0 — не запущена, 1 — запущена).</summary>
    private int rotationStarted;

    /// <inheritdoc />
    public (IRawWebSocket Ws, string Domain)? TryGet(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (config.MtProto.WsPoolSize <= 0 || workerDomains.Count == 0)
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

        // Промах — тоже повод наполнить пул. Без этого пустой бакет оставался
        // пустым навсегда: попаданий больше не будет, а значит и наполнения тоже.
        stats.IncCfPoolMiss();
        ScheduleRefill(dc, fallbackDst, workerDomains);
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
    public void ReportFailure(string workerDomain, Exception exception) => logger.LogDebug(exception, "CF worker {Domain} connect failed", workerDomain);

    /// <inheritdoc />
    public void Warmup()
    {
        if (config.MtProto.WsPoolSize <= 0 || config.CfProxy.WorkerDomains.Count == 0)
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

            ScheduleRefill(dc, fallbackDst, config.CfProxy.WorkerDomains);
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
        targets.Clear();
        refillFailures.Clear();
        refillAfter.Clear();
    }

    /// <summary>Останавливает фоновое наполнение пула и освобождает ресурсы.</summary>
    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <summary>Целевой размер бакета одного DC.</summary>
    private int TargetSize => Math.Min(config.MtProto.WsPoolSize, PerDcLimit);

    /// <summary>
    /// Планирует фоновое наполнение пула для DC: запоминает параметры подключения,
    /// запускает ротацию (однократно) и, если не мешает backoff, — наполнение.
    /// </summary>
    private void ScheduleRefill(int dc, string fallbackDst, IReadOnlyList<string> workerDomains)
    {
        if (config.MtProto.WsPoolSize <= 0 || workerDomains.Count == 0)
        {
            return;
        }

        targets[dc] = new RefillTarget(fallbackDst, workerDomains.ToArray());
        EnsureRotationStarted();

        if (refillAfter.TryGetValue(dc, out var after) && after > DateTimeOffset.UtcNow)
        {
            return;
        }

        if (!refilling.TryAdd(dc, 0))
        {
            return;
        }

        var domains = targets[dc].Domains;
        BackgroundTaskRunner.RunDetachedSafe(
            ct => RefillAsync(dc, fallbackDst, domains, ct),
            logger,
            $"[cf-pool] refill DC{dc}",
            lifetime.Token);
    }

    /// <summary>Запускает фоновую ротацию один раз на весь жизненный цикл пула.</summary>
    private void EnsureRotationStarted()
    {
        if (Interlocked.CompareExchange(ref rotationStarted, 1, 0) != 0)
        {
            return;
        }

        BackgroundTaskRunner.RunDetachedSafe(RotateAsync, logger, "[cf-pool] rotation", lifetime.Token);
    }

    /// <summary>
    /// Фоновая ротация: срезает протухшие соединения и поддерживает целевой размер
    /// бакета для каждого известного DC (upstream <c>_rotate</c>).
    /// </summary>
    private async Task RotateAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var (dc, target) in targets)
            {
                try
                {
                    RotateDc(dc, target);
                }
                catch (Exception ex)
                {
                    // Один сбойный DC не должен останавливать ротацию остальных.
                    logger.LogDebug(ex, "CF worker pool rotation for DC{Dc} failed", dc);
                }
            }
        }
    }

    /// <summary>Срезает протухшие соединения DC и запускает наполнение при недоборе.</summary>
    private void RotateDc(int dc, RefillTarget target)
    {
        var bucket = idle.GetOrAdd(dc, _ => new ConcurrentQueue<PooledWorkerWs>());
        var now = DateTimeOffset.UtcNow;

        var alive = new List<PooledWorkerWs>(bucket.Count);
        while (bucket.TryDequeue(out var entry))
        {
            if (IsStale(entry, now))
            {
                CloseQuietly(entry.Ws);
            }
            else
            {
                alive.Add(entry);
            }
        }

        foreach (var entry in alive)
        {
            bucket.Enqueue(entry);
        }

        if (alive.Count < TargetSize)
        {
            ScheduleRefill(dc, target.FallbackDst, target.Domains);
        }
    }

    /// <summary>
    /// Открывает недостающие соединения воркера и складывает их в пул. Серия
    /// неудачных наполнений включает экспоненциальный backoff (REFILL_BACKOFF).
    /// </summary>
    private async Task RefillAsync(int dc, string fallbackDst, string[] workerDomains, CancellationToken cancellationToken)
    {
        try
        {
            var bucket = idle.GetOrAdd(dc, _ => new ConcurrentQueue<PooledWorkerWs>());
            var needed = TargetSize - bucket.Count;
            if (needed <= 0)
            {
                return;
            }

            var fresh = 0;
            for (var i = 0; i < needed; i++)
            {
                var connected = await ConnectOneAsync(workerDomains, fallbackDst, dc, cancellationToken).ConfigureAwait(false);
                if (connected is null)
                {
                    break;
                }

                bucket.Enqueue(new PooledWorkerWs(connected.Value.Ws, DateTimeOffset.UtcNow, connected.Value.Domain));
                fresh++;
            }

            if (fresh > 0)
            {
                refillFailures.TryRemove(dc, out _);
                refillAfter.TryRemove(dc, out _);
                logger.LogDebug("CF worker pool refilled DC{Dc}: {Count} ready", dc, bucket.Count);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var failures = refillFailures.AddOrUpdate(dc, 1, (_, value) => value + 1);
            var delay = Math.Min(
                RefillBackoffInitialSeconds * Math.Pow(2, Math.Min(failures - 1, 12)),
                RefillBackoffMaxSeconds);
            refillAfter[dc] = DateTimeOffset.UtcNow.AddSeconds(delay);
            logger.LogInformation("CF worker pool refill failed for DC{Dc}, retry in {Delay:F0}s", dc, delay);
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
        var path = $"{AppConstants.Protocol.WsPath}?dst={Uri.EscapeDataString(fallbackDst)}&dc={dc}";

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
                logger.LogDebug(
                    "CF worker pool connect DC{Dc} via {Domain} for {Dst} failed: {Error}",
                    dc, workerDomain, fallbackDst, ex.Message);
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

    /// <summary>Параметры наполнения бакета одного DC.</summary>
    private readonly record struct RefillTarget(string FallbackDst, string[] Domains);
}
