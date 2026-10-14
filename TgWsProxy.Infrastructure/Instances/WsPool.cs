#nullable enable

using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Пул заранее открытых WebSocket-соединений (порт upstream <c>_WsPool</c>).
/// Клиентские сессии только забирают готовое соединение; наполнение идёт в фоне
/// с экспоненциальным backoff при неудачах.
/// </summary>
internal sealed class WsPool(
    Config config,
    Dictionary<int, string> dcOpt,
    IRawWebSocketFactory wsFactory,
    IProxyStats stats,
    ILogger<WsPool> logger) : IWsPool
{
    /// <summary>Максимальный возраст соединения в пуле (WS_POOL_MAX_AGE).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);

    /// <summary>Период проверки пула (WS_POOL_CHECK_INTERVAL).</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Начальная задержка backoff наполнения (REFILL_BACKOFF_INITIAL).</summary>
    private static readonly TimeSpan RefillBackoffInitial = TimeSpan.FromSeconds(1);

    /// <summary>Максимальная задержка backoff наполнения (REFILL_BACKOFF_MAX).</summary>
    private static readonly TimeSpan RefillBackoffMax = TimeSpan.FromSeconds(3600);

    /// <summary>Путь WebSocket-эндпоинта (WS_PATH).</summary>
    private const string WsPath = "/apiws";

    /// <summary>Путь тестового WebSocket-эндпоинта (WS_PATH_TEST).</summary>
    private const string WsPathTest = "/apiws_test";

    /// <summary>SNI для domain fronting (upstream использует этот домен вместо реального).</summary>
    private const string FrontingSni = "sprinthost.ru";

    /// <summary>Таймаут подключения в режиме fronting (секунды).</summary>
    private const int FrontedTimeoutSeconds = 7;

    /// <summary>Таймаут обычного подключения (секунды).</summary>
    private const int PlainTimeoutSeconds = 8;

    private readonly Dictionary<PoolKey, List<PooledWs>> idle = [];
    private readonly HashSet<PoolKey> known = [];
    private readonly HashSet<PoolKey> refilling = [];
    private readonly Dictionary<PoolKey, int> refillFailures = [];
    private readonly Dictionary<PoolKey, DateTimeOffset> refillAfter = [];
    private readonly object sync = new();
    private CancellationTokenSource? lifetime;
    private bool sweeperStarted;

    /// <summary>
    /// Признак того, что последняя успешная попытка была в режиме fronting; следующая попытка
    /// начнётся с него же (upstream <c>try_fronting_first</c>).
    /// </summary>
    private bool tryFrontingFirst;

    /// <inheritdoc />
    public IRawWebSocket? TryGet(int dc, bool isMedia, bool isTestDc)
    {
        if (config.WsPoolSize <= 0 || !TryResolveTarget(dc, out var targetIp))
        {
            return null;
        }

        var key = new PoolKey(dc, isMedia, isTestDc);
        var now = DateTimeOffset.UtcNow;
        IRawWebSocket? result = null;
        var hitAge = 0.0;
        var hitLeft = 0;

        lock (sync)
        {
            known.Add(key);
            if (idle.TryGetValue(key, out var bucket))
            {
                while (bucket.Count > 0)
                {
                    var entry = bucket[0];
                    bucket.RemoveAt(0);
                    if (IsStale(entry, now))
                    {
                        CloseQuietly(entry.Ws);
                        continue;
                    }

                    result = entry.Ws;
                    hitAge = (now - entry.Created).TotalSeconds;
                    hitLeft = bucket.Count;
                    break;
                }
            }
        }

        if (result is null)
        {
            stats.IncPoolMiss();
            logger.LogDebug("WS pool miss DC{Dc}{Media}{Test}, scheduling refill", dc, isMedia ? "m" : "", isTestDc ? "t" : "");
        }
        else
        {
            stats.IncPoolHit();
            logger.LogDebug(
                "WS pool hit DC{Dc}{Media}{Test} (age={Age:F1}s, left={Left})",
                dc, isMedia ? "m" : "", isTestDc ? "t" : "", hitAge, hitLeft);
        }

        ScheduleRefill(key, targetIp);
        return result;
    }

    /// <inheritdoc />
    public void Warmup()
    {
        if (config.WsPoolSize <= 0)
        {
            return;
        }

        foreach (var (dc, targetIp) in dcOpt)
        {
            if (string.IsNullOrEmpty(targetIp))
            {
                continue;
            }

            foreach (var isMedia in new[] { false, true })
            {
                var key = new PoolKey(dc, isMedia, config.ForceTestDc);
                lock (sync)
                {
                    known.Add(key);
                }

                ScheduleRefill(key, targetIp);
            }
        }

        logger.LogInformation("WS pool warmup started for {Count} DC(s)", dcOpt.Count);
    }

    /// <inheritdoc />
    public void Reset()
    {
        List<IRawWebSocket> toClose;
        lock (sync)
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            sweeperStarted = false;

            toClose = [.. idle.Values.SelectMany(bucket => bucket.Select(entry => entry.Ws))];
            idle.Clear();
            known.Clear();
            refilling.Clear();
            refillFailures.Clear();
            refillAfter.Clear();
            tryFrontingFirst = false;
        }

        foreach (var ws in toClose)
        {
            CloseQuietly(ws);
        }
    }

    /// <inheritdoc />
    public string FormatPoolSummary()
    {
        lock (sync)
        {
            var entries = idle
                .Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => kv.Key.Dc)
                .ThenBy(kv => kv.Key.IsMedia)
                .ThenBy(kv => kv.Key.IsTestDc)
                .Select(kv => $"DC{kv.Key.Dc}{(kv.Key.IsTestDc ? "t" : "")}{(kv.Key.IsMedia ? "m" : "")}={kv.Value.Count}")
                .ToList();

            return entries.Count == 0 ? "none" : string.Join(", ", entries);
        }
    }

    /// <summary>
    /// Планирует фоновое наполнение пула для указанного ключа с учётом backoff.
    /// </summary>
    private void ScheduleRefill(PoolKey key, string targetIp)
    {
        if (config.WsPoolSize <= 0)
        {
            return;
        }

        EnsureSweeper();

        lock (sync)
        {
            if (refilling.Contains(key))
            {
                return;
            }

            if (refillAfter.TryGetValue(key, out var after) && DateTimeOffset.UtcNow < after)
            {
                return;
            }

            refilling.Add(key);
        }

        var token = lifetime?.Token ?? CancellationToken.None;
        BackgroundTaskRunner.RunDetachedSafe(
            ct => RefillAsync(key, targetIp, ct),
            logger,
            $"[pool] refill DC{key.Dc}{(key.IsMedia ? "m" : "")}",
            token);
    }

    /// <summary>
    /// Открывает недостающие соединения и складывает их в пул.
    /// </summary>
    private async Task RefillAsync(PoolKey key, string targetIp, CancellationToken cancellationToken)
    {
        try
        {
            var needed = config.WsPoolSize - CountIdle(key);
            if (needed <= 0)
            {
                return;
            }

            var domains = WsDomains(key.Dc, key.IsMedia);
            var path = key.IsTestDc ? WsPathTest : WsPath;
            var scope = $"pool DC{key.Dc}{(key.IsTestDc ? "t" : "")}{(key.IsMedia ? "m" : "")}";

            var tasks = new Task<IRawWebSocket?>[needed];
            for (var i = 0; i < needed; i++)
            {
                tasks[i] = ConnectOneAsync(targetIp, domains, path, scope, cancellationToken);
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            var connected = 0;
            var now = DateTimeOffset.UtcNow;
            lock (sync)
            {
                var bucket = GetOrCreateBucket(key);
                foreach (var ws in results)
                {
                    if (ws is null)
                    {
                        continue;
                    }

                    if (ws.IsClosed)
                    {
                        CloseQuietly(ws);
                        continue;
                    }

                    bucket.Add(new PooledWs(ws, now));
                    connected++;
                }

                if (connected > 0)
                {
                    refillFailures.Remove(key);
                    refillAfter.Remove(key);
                }
                else
                {
                    var failures = refillFailures.GetValueOrDefault(key) + 1;
                    refillFailures[key] = failures;
                    var delay = Backoff(RefillBackoffInitial, failures);
                    refillAfter[key] = DateTimeOffset.UtcNow + delay;
                    logger.LogInformation(
                        "WS pool refill failed for DC{Dc}{Media}{Test}, retry in {Delay:F0}s",
                        key.Dc, key.IsMedia ? "m" : "", key.IsTestDc ? "t" : "", delay.TotalSeconds);
                }

                logger.LogDebug(
                    "WS pool refilled DC{Dc}{Media}{Test}: {Count} ready",
                    key.Dc, key.IsMedia ? "m" : "", key.IsTestDc ? "t" : "", bucket.Count);
            }
        }
        finally
        {
            lock (sync)
            {
                refilling.Remove(key);
            }
        }
    }

    /// <summary>
    /// Пытается установить одно WS-соединение, перебирая домены.
    /// </summary>
    private async Task<IRawWebSocket?> ConnectOneAsync(
        string targetIp,
        IReadOnlyList<string> domains,
        string path,
        string scope,
        CancellationToken cancellationToken)
    {
        // upstream: режимы перебираются в порядке try_fronting_first, затем обычный.
        var frontingFirst = Volatile.Read(ref tryFrontingFirst);

        foreach (var domain in domains)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var fronted = (attempt == 0) == frontingFirst;
                var timeout = TimeSpan.FromSeconds(fronted ? FrontedTimeoutSeconds : PlainTimeoutSeconds);
                var sni = fronted ? FrontingSni : null;

                try
                {
                    var ws = await wsFactory
                        .Connect(targetIp, domain, path, scope, timeout, secure: !config.DisableSecure, sni: sni)
                        .ConfigureAwait(false);

                    if (fronted != frontingFirst)
                    {
                        Volatile.Write(ref tryFrontingFirst, fronted);
                    }

                    if (fronted)
                    {
                        stats.IncConnectionsFronting();
                    }

                    return ws;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    stats.IncWsErrors();
                    logger.LogDebug(
                        ex,
                        "[{Scope}] WS pool connect {Domain}{Path} via {Ip} (fronted={Fronted}) failed",
                        scope, domain, path, targetIp, fronted);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Запускает фоновый обход пула: удаляет протухшие соединения и добирает недостающие.
    /// </summary>
    private void EnsureSweeper()
    {
        lock (sync)
        {
            if (sweeperStarted)
            {
                return;
            }

            sweeperStarted = true;
            lifetime ??= new CancellationTokenSource();
        }

        var token = lifetime!.Token;
        BackgroundTaskRunner.RunDetachedSafe(SweepLoopAsync, logger, "[pool] sweeper", token);
    }

    /// <summary>
    /// Периодически проверяет все известные ключи пула (аналог upstream <c>_rotate</c>).
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            List<(PoolKey Key, string Ip)> pending;
            var now = DateTimeOffset.UtcNow;
            lock (sync)
            {
                foreach (var key in known)
                {
                    if (idle.TryGetValue(key, out var bucket))
                    {
                        for (var i = bucket.Count - 1; i >= 0; i--)
                        {
                            if (IsStale(bucket[i], now))
                            {
                                CloseQuietly(bucket[i].Ws);
                                bucket.RemoveAt(i);
                            }
                        }
                    }
                }

                pending = [];
                foreach (var key in known)
                {
                    if (CountIdleLocked(key) >= config.WsPoolSize)
                    {
                        continue;
                    }

                    var ip = dcOpt.GetValueOrDefault(key.Dc);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        pending.Add((key, ip));
                    }
                }
            }

            foreach (var (key, ip) in pending)
            {
                ScheduleRefill(key, ip);
            }
        }
    }

    /// <summary>
    /// Возвращает домены WebSocket для DC (порт upstream <c>ws_domains</c>).
    /// </summary>
    private static List<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203)
        {
            dc = 2;
        }

        return isMedia
            ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"]
            : [$"kws{dc}.web.telegram.org"];
    }

    /// <summary>
    /// Проверяет, что соединение протухло и не может быть отдано клиенту.
    /// Помимо возраста и локального признака закрытия учитывается закрытие со стороны
    /// сервера (upstream <c>reader.at_eof()</c>): Telegram рвёт простаивающие WS,
    /// и такое соединение нельзя выдавать клиенту. <see cref="IRawWebSocket.IsRemoteClosed"/>
    /// ловит и FIN/RST, и close_notify/close-кадр, уже лежащие в буфере сокета.
    /// </summary>
    private static bool IsStale(PooledWs entry, DateTimeOffset now)
        => entry.Ws.IsClosed
            || entry.Ws.IsRemoteClosed
            || now - entry.Created >= MaxAge;

    /// <summary>
    /// Возвращает целевой IP для DC, если он настроен.
    /// </summary>
    private bool TryResolveTarget(int dc, out string targetIp)
    {
        targetIp = dcOpt.GetValueOrDefault(dc) ?? string.Empty;
        return !string.IsNullOrEmpty(targetIp);
    }

    private int CountIdle(PoolKey key)
    {
        lock (sync)
        {
            return CountIdleLocked(key);
        }
    }

    private int CountIdleLocked(PoolKey key)
        => idle.TryGetValue(key, out var bucket) ? bucket.Count : 0;

    private List<PooledWs> GetOrCreateBucket(PoolKey key)
    {
        if (!idle.TryGetValue(key, out var bucket))
        {
            bucket = [];
            idle[key] = bucket;
        }

        known.Add(key);
        return bucket;
    }

    /// <summary>
    /// Считает задержку экспоненциального backoff (min(initial * 2^(failures-1), max)).
    /// </summary>
    private static TimeSpan Backoff(TimeSpan initial, int failures)
    {
        var exponent = Math.Min(failures - 1, 12);
        var seconds = Math.Min(initial.TotalSeconds * Math.Pow(2, exponent), RefillBackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Закрывает соединение, подавляя любые ошибки закрытия.
    /// </summary>
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
            "[pool] quiet close",
            CancellationToken.None);

    /// <summary>Ключ пула: DC + media + test-признак.</summary>
    private readonly record struct PoolKey(int Dc, bool IsMedia, bool IsTestDc);

    /// <summary>Соединение в пуле вместе со временем создания.</summary>
    private readonly record struct PooledWs(IRawWebSocket Ws, DateTimeOffset Created);
}
