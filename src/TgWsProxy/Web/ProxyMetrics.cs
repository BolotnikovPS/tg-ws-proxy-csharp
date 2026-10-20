using System.Diagnostics;
using Prometheus;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Web;

/// <summary>
/// Единственный источник метрик Prometheus (F-3.1). Владеет собственным
/// <see cref="CollectorRegistry"/> экземпляра приложения: значения снимаются со
/// счётчиков прокси непосредственно перед сбором, поэтому отдельная синхронизация
/// и рукописный форматтер не нужны.
/// </summary>
/// <remarks>
/// Реестр создаётся на экземпляр приложения (а не через <c>Metrics.DefaultRegistry</c>),
/// чтобы несколько хостов в одном процессе (тестовые приложения, повторный запуск
/// в одном процессе) не конфликтовали именами метрик и не делили состояние.
/// Счётчики публикуются через <c>IncTo</c>: значения в снимке кумулятивные, поэтому
/// повторный сбор всегда даёт актуальное значение без ведения дельт.
/// </remarks>
public sealed class ProxyMetrics
{
    /// <summary>Content-Type экспозиции Prometheus (text format 0.0.4).</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Префикс глобальных метрик прокси.</summary>
    public const string Prefix = "tgwsproxy";

    /// <summary>Префикс метрик по парам (IP, токен).</summary>
    public const string PeerPrefix = "tgws_peer";

    private static readonly (string Name, string Help)[] GlobalCounters =
    [
        ("connections_total", "Total accepted proxy connections, including rejected ones."),
        ("connections_ws_total", "Connections served over a direct WebSocket."),
        ("connections_h2_total", "Media connections served over HTTP/2 multiplexing."),
        ("h2_tcp_connections_total", "Established HTTP/2 transport TCP connections."),
        ("h2_requests_total", "HTTP/2 requests performed."),
        ("h2_errors_total", "HTTP/2 errors."),
        ("connections_tcp_fallback_total", "Connections moved to the TCP fallback path."),
        ("connections_cf_proxy_total", "Connections served through the Cloudflare proxy fallback."),
        ("connections_fronting_total", "Connections served through domain fronting."),
        ("connections_bad_total", "Connections with an invalid MTProto handshake."),
        ("connections_blocked_total", "Connections rejected by the IP access list."),
        ("connections_masked_total", "Connections masked as another domain."),
        ("connections_limited_total", "Connections rejected by the connection limit."),
        ("connections_rate_limited_total", "Connections rejected by the per-IP rate limiter."),
        ("ws_errors_total", "WebSocket errors."),
        ("bytes_up_total", "Bytes received from clients (client to upstream)."),
        ("bytes_down_total", "Bytes sent to clients (upstream to client)."),
        ("pool_hits_total", "WebSocket pool hits."),
        ("pool_misses_total", "WebSocket pool misses."),
        ("cf_pool_hits_total", "Cloudflare worker pool hits."),
        ("cf_pool_misses_total", "Cloudflare worker pool misses."),
        ("peer_tracking_overflow_total", "Per-peer tracking entries rejected by the registry limit."),
    ];

    private readonly CollectorRegistry registry = Metrics.NewCustomRegistry();
    private readonly MetricFactory factory;
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private readonly IProxyStats stats;
    private readonly IPeerStatsRegistry peerStats;
    private readonly PeerStatsSnapshotCache snapshotCache;
    private readonly Config config;
    private readonly Stopwatch uptime = Stopwatch.StartNew();

    private readonly Gauge uptimeSeconds;
    private readonly Gauge connectionsActive;
    private readonly Gauge peerTracked;
    private readonly Counter peerBytesUp;
    private readonly Counter peerBytesDown;
    private readonly Counter peerConnectionsTotal;
    private readonly Counter peerWsErrors;
    private readonly Gauge peerConnectionsActive;

    // Пары (ip, token), по которым метрики уже экспортированы: нужны, чтобы
    // снимать серии, выпавшие из top-N или исчезнувшие из реестра (N-12).
    private readonly HashSet<(string Ip, string Token)> exportedPeers = [];

    /// <summary>
    /// Создаёт реестр метрик приложения и подписывается на сбор.
    /// </summary>
    /// <param name="stats">Глобальные счётчики прокси.</param>
    /// <param name="peerStats">Реестр статистики по парам (IP, токен).</param>
    /// <param name="snapshotCache">Кэш снимка реестра «IP, токен» (P0-2).</param>
    /// <param name="config">Действующая конфигурация (влияет на состав серий).</param>
    public ProxyMetrics(IProxyStats stats, IPeerStatsRegistry peerStats, PeerStatsSnapshotCache snapshotCache, Config config)
    {
        this.stats = stats;
        this.peerStats = peerStats;
        this.snapshotCache = snapshotCache;
        this.config = config;
        factory = Metrics.WithCustomRegistry(registry);

        foreach (var (name, help) in GlobalCounters)
        {
            counters[name] = CreateCounter($"{Prefix}_{name}", help);
        }

        uptimeSeconds = CreateGauge(
            $"{Prefix}_uptime_seconds", "Seconds since the proxy listener was started.");
        connectionsActive = CreateGauge(
            $"{Prefix}_connections_active", "Active client sessions.");
        peerTracked = CreateGauge(
            $"{Prefix}_peer_tracked", "Number of (IP, token) pairs currently tracked.");

        var labelNames = new[] { "ip", "token" };
        peerBytesUp = CreateCounter(
            $"{PeerPrefix}_bytes_up_total", "Bytes received from clients per (IP, token).", labelNames);
        peerBytesDown = CreateCounter(
            $"{PeerPrefix}_bytes_down_total", "Bytes sent to clients per (IP, token).", labelNames);
        peerConnectionsTotal = CreateCounter(
            $"{PeerPrefix}_connections_total", "Accepted connections per (IP, token).", labelNames);
        peerWsErrors = CreateCounter(
            $"{PeerPrefix}_ws_errors_total", "WebSocket errors per (IP, token).", labelNames);
        peerConnectionsActive = CreateGauge(
            $"{PeerPrefix}_connections_active", "Active client sessions per (IP, token).", labelNames);

        HealthStatusGauge = CreateGauge(
            "healthcheck_status",
            "Status of each ASP.NET Core health check (0=unhealthy, 1=degraded, 2=healthy).",
            ["name"]);

        HealthDurationGauge = CreateGauge(
            "healthcheck_duration_seconds",
            "Duration of each ASP.NET Core health check in seconds.",
            ["name"]);

        registry.AddBeforeCollectCallback(Update);
    }

    /// <summary>
    /// Gauge статусов health check (F-3.10). Передаётся в
    /// <c>PrometheusHealthCheckPublisherOptions</c>, чтобы публикатор писал
    /// значения именно в реестр этого приложения.
    /// </summary>
    public Gauge HealthStatusGauge { get; }

    /// <summary>
    /// Gauge длительности health check (F-3.10). Заполняется
    /// <see cref="HealthDurationPublisher"/> из отчёта службы проверок.
    /// </summary>
    public Gauge HealthDurationGauge { get; }

    /// <summary>Реестр метрик приложения (используется публикатором длительности проверок).</summary>
    public CollectorRegistry Registry => registry;

    /// <summary>Секунды с момента старта процесса прокси.</summary>
    public double UptimeSeconds => uptime.Elapsed.TotalSeconds;

    /// <summary>
    /// Записывает текущую экспозицию Prometheus в поток ответа.
    /// </summary>
    /// <param name="destination">Поток ответа.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    public Task CollectAsync(Stream destination, CancellationToken cancellationToken) =>
        registry.CollectAndExportAsTextAsync(destination, cancellationToken);

    /// <summary>
    /// Снимает значения со счётчиков прокси перед сбором метрик.
    /// </summary>
    private void Update()
    {
        var snapshot = stats.Snapshot();

        SetCounter("connections_total", snapshot.ConnectionsTotal);
        SetCounter("connections_ws_total", snapshot.ConnectionsWs);
        SetCounter("connections_h2_total", snapshot.ConnectionsH2);
        SetCounter("h2_tcp_connections_total", snapshot.H2TcpConnections);
        SetCounter("h2_requests_total", snapshot.H2Requests);
        SetCounter("h2_errors_total", snapshot.H2Errors);
        SetCounter("connections_tcp_fallback_total", snapshot.ConnectionsTcpFallback);
        SetCounter("connections_cf_proxy_total", snapshot.ConnectionsCfProxy);
        SetCounter("connections_fronting_total", snapshot.ConnectionsFronting);
        SetCounter("connections_bad_total", snapshot.ConnectionsBad);
        SetCounter("connections_blocked_total", snapshot.ConnectionsBlocked);
        SetCounter("connections_masked_total", snapshot.ConnectionsMasked);
        SetCounter("connections_limited_total", snapshot.ConnectionsLimited);
        SetCounter("connections_rate_limited_total", snapshot.ConnectionsRateLimited);
        SetCounter("ws_errors_total", snapshot.WsErrors);
        SetCounter("bytes_up_total", snapshot.BytesUp);
        SetCounter("bytes_down_total", snapshot.BytesDown);
        SetCounter("pool_hits_total", snapshot.PoolHits);
        SetCounter("pool_misses_total", snapshot.PoolMisses);
        SetCounter("cf_pool_hits_total", snapshot.CfPoolHits);
        SetCounter("cf_pool_misses_total", snapshot.CfPoolMisses);
        SetCounter("peer_tracking_overflow_total", peerStats.OverflowCount);

        uptimeSeconds.Set(uptime.Elapsed.TotalSeconds);
        connectionsActive.Set(snapshot.ConnectionsActive);
        peerTracked.Set(peerStats.TrackedPeers);

        UpdatePeers();
    }

    /// <summary>
    /// Обновляет серии по парам (IP, токен): экспортируются только пары из top-N
    /// по трафику (F-9.6, ограничение кардинальности N-12).
    /// </summary>
    private void UpdatePeers()
    {
        var limit = config.PrometheusTopPeers;
        var current = new HashSet<(string Ip, string Token)>();

        if (config.EnablePerIpStats && limit > 0)
        {
            var peers = TopPeers(snapshotCache.Get(), limit);

            foreach (var peer in peers)
            {
                var ip = peer.Ip;
                var token = peer.TokenName ?? peer.TokenId ?? string.Empty;
                current.Add((ip, token));

                peerBytesUp.WithLabels(ip, token).IncTo(peer.BytesUp);
                peerBytesDown.WithLabels(ip, token).IncTo(peer.BytesDown);
                peerConnectionsTotal.WithLabels(ip, token).IncTo(peer.TotalConnections);
                peerWsErrors.WithLabels(ip, token).IncTo(peer.WsErrors);
                peerConnectionsActive.WithLabels(ip, token).Set(peer.ActiveConnections);
            }
        }

        foreach (var stale in exportedPeers.Where(peer => !current.Contains(peer)).ToArray())
        {
            string[] labels = [stale.Ip, stale.Token];
            peerBytesUp.RemoveLabelled(labels);
            peerBytesDown.RemoveLabelled(labels);
            peerConnectionsTotal.RemoveLabelled(labels);
            peerWsErrors.RemoveLabelled(labels);
            peerConnectionsActive.RemoveLabelled(labels);
        }

        exportedPeers.Clear();
        exportedPeers.UnionWith(current);
    }

    /// <summary>
    /// Отбирает top-N пар по трафику без полной сортировки: ограниченная куча даёт
    /// <c>O(n log k)</c> вместо <c>O(n log n)</c> (P0-1). Результат упорядочен по
    /// убыванию трафика, затем по возрастанию IP (как прежняя
    /// <c>OrderByDescending(...).ThenBy(...)</c>).
    /// </summary>
    private static List<PeerStatsSnapshot> TopPeers(IReadOnlyList<PeerStatsSnapshot> peers, int limit)
    {
        if (peers.Count <= limit)
        {
            return [.. peers];
        }

        // Мин-куча: первым выходит «худший» (меньше трафика, при равенстве — больший IP),
        // поэтому вершина всегда худшая из top-N и вытесняется новым элементом.
        var queue = new PriorityQueue<PeerStatsSnapshot, (long TotalBytes, string Ip)>(
            Comparer<(long TotalBytes, string Ip)>.Create(static (a, b) =>
            {
                var byBytes = a.TotalBytes.CompareTo(b.TotalBytes);
                if (byBytes != 0)
                {
                    return byBytes;
                }

                return string.CompareOrdinal(b.Ip, a.Ip);
            }));

        foreach (var peer in peers)
        {
            var priority = (peer.TotalBytes, peer.Ip);
            if (queue.Count < limit)
            {
                queue.Enqueue(peer, priority);
            }
            else if (queue.TryPeek(out _, out var worst) && IsBetter(peer, worst))
            {
                queue.Dequeue();
                queue.Enqueue(peer, priority);
            }
        }

        var result = new List<PeerStatsSnapshot>(queue.Count);
        while (queue.TryDequeue(out var peer, out _))
        {
            result.Add(peer);
        }

        result.Reverse();
        return result;
    }

    private static bool IsBetter(PeerStatsSnapshot candidate, (long TotalBytes, string Ip) worst) =>
        candidate.TotalBytes > worst.TotalBytes
        || (candidate.TotalBytes == worst.TotalBytes
            && string.CompareOrdinal(candidate.Ip, worst.Ip) < 0);

    private void SetCounter(string name, long value) => counters[name].IncTo(value);

    /// <summary>
    /// Создаёт счётчик в реестре этого приложения.
    /// </summary>
    /// <remarks>
    /// Фабрика <see cref="MetricFactory"/>, полученная из
    /// <c>Metrics.WithCustomRegistry</c>, пишет метрики в реестр экземпляра, а не в
    /// <c>Metrics.DefaultRegistry</c>, поэтому несколько приложений в одном процессе
    /// не конфликтуют именами.
    /// </remarks>
    private Counter CreateCounter(string name, string help, string[]? labelNames = null) =>
        factory.CreateCounter(name, help, labelNames ?? []);

    /// <summary>
    /// Создаёт gauge в реестре этого приложения.
    /// </summary>
    private Gauge CreateGauge(string name, string help, string[]? labelNames = null) =>
        factory.CreateGauge(name, help, labelNames ?? []);
}
