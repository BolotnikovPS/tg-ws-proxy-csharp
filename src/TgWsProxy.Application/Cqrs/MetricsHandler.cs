using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.ReadModels;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Обработчики запросов наблюдаемости: сводка, счётчики, временной ряд, разрезы по адресам
/// и токенам, проверка DC. HTTP-слой получает готовые модели чтения и ничего не агрегирует.
/// </summary>
public static class MetricsHandler
{
    /// <summary>Размер страницы <c>/api/per-ip</c> по умолчанию (F-4.7).</summary>
    public const int DefaultPerIpLimit = 50;

    /// <summary>Верхняя граница размера страницы <c>/api/per-ip</c> (F-4.7).</summary>
    public const int MaxPerIpLimit = 500;

    /// <summary>Собирает сводку о процессе, слушателе и действующих лимитах (F-3.6, F-7.8).</summary>
    /// <param name="query">Запрос сводки.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="server">Состояние сетевого слушателя.</param>
    /// <param name="listener">Управление слушателем: состояние, счётчик и время перезапусков.</param>
    /// <param name="runtime">Версия сборки и время старта процесса.</param>
    public static InfoDto Handle(
        GetInfoQuery query,
        Config cfg,
        IProxyServer server,
        IListenerController listener,
        AppRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(runtime);

        return new InfoDto(
            runtime.Version,
            runtime.UptimeSeconds,
            runtime.StartedAt,
            server.IsListening,
            cfg.H2Enabled,
            cfg.CfProxy.Enabled,
            !string.IsNullOrWhiteSpace(cfg.FakeTlsDomain),
            cfg.ProxyProtocol,
            cfg.MaxConnections,
            cfg.RateLimitPerIp,
            cfg.RateLimitWindowSeconds,
            cfg.MtProto.DcIp.Count,
            cfg.MtProto.Port,
            listener.State.ToString().ToLowerInvariant(),
            listener.RestartCount,
            listener.LastRestartAt,
            cfg.Tokens.Count,
            cfg.Tokens.Count(static token => token.Enabled));
    }

    /// <summary>Снимает глобальные счётчики вместе с числом отслеживаемых адресов (F-3.2, F-4.5).</summary>
    /// <param name="query">Запрос снимка счётчиков.</param>
    /// <param name="stats">Источник счётчиков.</param>
    /// <param name="peerStats">Реестр per-IP статистики.</param>
    /// <param name="cfg">Живая конфигурация (нужен признак HTTP/2-моста).</param>
    /// <param name="runtime">Версия сборки и время старта процесса.</param>
    public static MetricsDto Handle(
        GetMetricsQuery query,
        IProxyStats stats,
        IPeerStatsRegistry peerStats,
        Config cfg,
        AppRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(peerStats);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(runtime);

        return MetricsDto.From(
            stats.Snapshot(),
            runtime.Version,
            runtime.UptimeSeconds,
            cfg.H2Enabled,
            peerStats.TrackedPeers,
            peerStats.OverflowCount);
    }

    /// <summary>
    /// Отдаёт временной ряд из кольцевого буфера (F-3.3). Окно зажато в 60…3600 с,
    /// шаг — в 1…300 с, чтобы запрос не мог потребовать неограниченного объёма работы.
    /// </summary>
    /// <param name="query">Запрос ряда.</param>
    /// <param name="history">Читатель буфера.</param>
    /// <param name="cfg">Живая конфигурация: умолчания окна и шага.</param>
    public static TimeseriesDto Handle(GetTimeseriesQuery query, ITimeseriesReader history, Config cfg)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(cfg);

        var windowSeconds = Math.Clamp(query.WindowSeconds ?? cfg.TimeseriesWindowSeconds, 60, 3600);
        var stepSeconds = Math.Clamp(query.StepSeconds ?? cfg.TimeseriesStepSeconds, 1, 300);

        return history.Query(
            TimeSpan.FromSeconds(windowSeconds),
            TimeSpan.FromSeconds(stepSeconds),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Сводит реестр в запрошенный разрез, фильтрует и отдаёт страницу, отсортированную
    /// по трафику (F-3.5, F-4.7, F-9.4).
    /// </summary>
    /// <param name="query">Параметры страницы и фильтров.</param>
    /// <param name="peerStats">Реестр per-IP статистики: общее число адресов и переполнения.</param>
    /// <param name="snapshotCache">Кэш снимка реестра (читает фоновый сэмплер).</param>
    public static PerIpPageDto Handle(
        GetPerIpQuery query,
        IPeerStatsRegistry peerStats,
        PeerStatsSnapshotCache snapshotCache)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(peerStats);
        ArgumentNullException.ThrowIfNull(snapshotCache);

        var pageOffset = Math.Max(0, query.Offset ?? 0);
        var pageLimit = Math.Clamp(query.Limit ?? DefaultPerIpLimit, 1, MaxPerIpLimit);

        IEnumerable<PeerStatsSnapshot> filtered = snapshotCache.Get();

        if (!string.IsNullOrWhiteSpace(query.IpPrefix))
        {
            var needle = query.IpPrefix.Trim();
            filtered = filtered.Where(peer => peer.Ip.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.TokenId))
        {
            var tokenId = query.TokenId.Trim();
            filtered = filtered.Where(peer => string.Equals(peer.TokenId, tokenId, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = PeerStatsAggregator.Aggregate(filtered, query.Group)
            .OrderByDescending(peer => peer.TotalBytes)
            .ThenByDescending(peer => peer.TotalConnections)
            .ThenBy(peer => peer.Ip, StringComparer.Ordinal)
            .ThenBy(peer => peer.TokenId, StringComparer.Ordinal)
            .ToList();

        return new PerIpPageDto(
            pageOffset,
            pageLimit,
            ordered.Count,
            peerStats.TrackedPeers,
            peerStats.OverflowCount,
            [.. ordered.Skip(pageOffset).Take(pageLimit).Select(PeerStatsDto.From)]);
    }

    /// <summary>
    /// Отдаёт расход трафика по каждому токену, включая токены без трафика (F-9.5).
    /// </summary>
    /// <param name="query">Запрос разреза по токенам.</param>
    /// <param name="snapshotCache">Кэш снимка реестра.</param>
    /// <param name="tokens">Реестр токенов: источник полного списка и имён.</param>
    public static IReadOnlyList<TokenUsageDto> Handle(
        GetTokenUsageQuery query,
        PeerStatsSnapshotCache snapshotCache,
        IProxyTokenRegistry tokens)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(snapshotCache);
        ArgumentNullException.ThrowIfNull(tokens);

        var aggregated = PeerStatsAggregator
            .Aggregate(snapshotCache.Get(), PeerStatsAggregator.GroupToken)
            .ToDictionary(peer => peer.TokenId ?? "", StringComparer.Ordinal);

        return [.. tokens.All
            .Select(token =>
            {
                aggregated.TryGetValue(token.Id, out var usage);
                return new TokenUsageDto(
                    token.Id,
                    token.Name,
                    usage?.ActiveConnections ?? 0,
                    usage?.TotalConnections ?? 0,
                    usage?.BytesUp ?? 0,
                    usage?.BytesDown ?? 0,
                    usage?.WsErrors ?? 0,
                    usage?.Rejected ?? 0,
                    usage?.PoolHits ?? 0,
                    usage?.PoolMisses ?? 0);
            })
            .OrderByDescending(item => item.BytesUp + item.BytesDown)
            .ThenBy(item => item.TokenName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Проверяет четыре маршрута до каждого DC с хоста прокси.</summary>
    /// <param name="query">Запрос проверки.</param>
    /// <param name="checker">Реализация проверки доступности.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    public static Task<DcCheckReport> HandleAsync(
        RunDcCheckQuery query,
        IDcReachabilityChecker checker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(checker);

        return checker.CheckAsync(cancellationToken);
    }
}