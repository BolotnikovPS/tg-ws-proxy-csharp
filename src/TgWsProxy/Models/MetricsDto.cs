using TgWsProxy.Domain;

namespace TgWsProxy.Models;

/// <summary>
/// Ответ <c>GET /api/info</c>: версия, время работы и состояние подсистем прокси (F-3.6).
/// </summary>
public sealed record InfoDto(
    string Version,
    long UptimeSeconds,
    DateTimeOffset StartedAt,
    bool Listening,
    bool H2Enabled,
    bool CfProxyEnabled,
    bool MaskingConfigured,
    bool ProxyProtocolEnabled,
    int MaxConnections,
    int RateLimitPerIp,
    int RateLimitWindowSeconds,
    int DcCount,
    int ListenerPort,
    string ListenerState,
    int ListenerRestarts,
    DateTimeOffset? LastListenerRestart,
    int TokensTotal,
    int TokensEnabled);

/// <summary>
/// Токен доступа в ответе <c>GET /api/tokens</c> (F-8.8). Секрет приходит только маской,
/// кроме ответов на создание и ротацию, где он выдаётся один раз.
/// </summary>
public sealed record TokenDto(
    string Id,
    string Name,
    string Secret,
    bool Enabled,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUsedAt,
    int ActiveSessions = 0)
{
    /// <summary>Строит DTO из модели токена, маскируя секрет (N-8).</summary>
    /// <param name="token">Токен из живой конфигурации.</param>
    /// <param name="revealSecret">Вернуть секрет в открытом виде (только создание и ротация).</param>
    /// <param name="activeSessions">Число живых сессий с этим токеном (F-8.11).</param>
    public static TokenDto From(ProxyToken token, bool revealSecret = false, int activeSessions = 0) => new(
        token.Id,
        token.Name,
        revealSecret ? token.Secret : SecretMask.Mask(token.Secret),
        token.Enabled,
        token.CreatedAt,
        token.LastUsedAt,
        activeSessions);
}

/// <summary>
/// Тело запроса на создание токена (F-8.8). Секрет необязателен: без него сервер генерирует
/// 32 hex-символа, что исключает слабые секреты из панели.
/// </summary>
public sealed record CreateTokenRequest(string? Name, string? Secret, bool? Enabled);

/// <summary>
/// Тело запроса на изменение токена (F-8.8). Отсутствующее поле не меняется;
/// маскированный секрет означает «оставить прежний».
/// </summary>
public sealed record UpdateTokenRequest(string? Name, string? Secret, bool? Enabled);

/// <summary>
/// Ответ <c>GET /api/tokens/{id}/link</c>: готовая ссылка для Telegram и полный секрет.
/// Секрет в этом ответе раскрыт (ссылка иначе не собрать), поэтому ответ запрещён
/// к кэшированию (F-8.8, исключение для кнопки «Скопировать ссылку»).
///
/// Поля WEB proxy: <see cref="WebProxyLink"/> — ссылка-приглашение
/// <c>https://t.me/webproxy?…</c> по каноническому шаблону Telegram,
/// <see cref="WebProxyDeepLink"/> — эквивалентный <c>tg://webproxy?…</c> для прямого
/// открытия в клиенте, <see cref="WebProxyBridgeUrl"/> — адрес страницы-носителя
/// с HMAC capability (только для отладки и смоук-клиента, не для Telegram).
/// </summary>
public sealed record TokenLinkDto(
    string TokenId,
    string Name,
    string? Link,
    string Secret,
    string? WebProxyLink,
    string? WebProxyDeepLink,
    string? WebProxyBridgeUrl);

/// <summary>
/// Ответ <c>GET /api/metrics</c>: снимок счётчиков плюс метаданные и производные величины (F-3.2).
/// </summary>
public sealed record MetricsDto(
    long UptimeSeconds,
    string Version,
    long ConnectionsTotal,
    long ConnectionsActive,
    long ConnectionsWs,
    long ConnectionsH2,
    long ConnectionsTcpFallback,
    long ConnectionsCfProxy,
    long ConnectionsFronting,
    long ConnectionsBad,
    long ConnectionsBlocked,
    long ConnectionsLimited,
    long ConnectionsRateLimited,
    long ConnectionsMasked,
    long WsErrors,
    long BytesUp,
    long BytesDown,
    long PoolHits,
    long PoolMisses,
    long CfPoolHits,
    long CfPoolMisses,
    long H2TcpConnections,
    long H2Requests,
    long H2Errors,
    long TrackedPeers,
    long PeerTrackingOverflow,
    bool H2Enabled)
{
    /// <summary>Строит DTO из снимка счётчиков.</summary>
    /// <param name="snapshot">Снимок глобальных счётчиков.</param>
    /// <param name="version">Версия приложения.</param>
    /// <param name="uptimeSeconds">Время работы процесса в секундах.</param>
    /// <param name="h2Enabled">Признак доступности HTTP/2-моста.</param>
    /// <param name="trackedPeers">Сколько адресов сейчас отслеживает per-IP реестр (F-4.5).</param>
    /// <param name="peerTrackingOverflow">Сколько адресов не удалось зарегистрировать из-за лимита (F-4.5).</param>
    public static MetricsDto From(
        StatsSnapshot snapshot,
        string version,
        long uptimeSeconds,
        bool h2Enabled,
        long trackedPeers = 0,
        long peerTrackingOverflow = 0) => new(
        uptimeSeconds,
        version,
        snapshot.ConnectionsTotal,
        snapshot.ConnectionsActive,
        snapshot.ConnectionsWs,
        snapshot.ConnectionsH2,
        snapshot.ConnectionsTcpFallback,
        snapshot.ConnectionsCfProxy,
        snapshot.ConnectionsFronting,
        snapshot.ConnectionsBad,
        snapshot.ConnectionsBlocked,
        snapshot.ConnectionsLimited,
        snapshot.ConnectionsRateLimited,
        snapshot.ConnectionsMasked,
        snapshot.WsErrors,
        snapshot.BytesUp,
        snapshot.BytesDown,
        snapshot.PoolHits,
        snapshot.PoolMisses,
        snapshot.CfPoolHits,
        snapshot.CfPoolMisses,
        snapshot.H2TcpConnections,
        snapshot.H2Requests,
        snapshot.H2Errors,
        trackedPeers,
        peerTrackingOverflow,
        h2Enabled);
}

/// <summary>
/// Элемент ответа <c>GET /api/per-ip</c> — ресурсы, израсходованные одной парой «адрес, токен» (F-4.3, F-9.4).
/// </summary>
/// <param name="Ip">Нормализованный адрес без порта (пусто в разрезе <c>group=token</c>).</param>
/// <param name="TokenId">Идентификатор токена доступа; <c>null</c> — отклонённые соединения (F-9.3).</param>
/// <param name="TokenName">Имя токена на момент снимка; <c>null</c> для отклонённых или удалённых токенов.</param>
/// <param name="ActiveConnections">Активные соединения.</param>
/// <param name="TotalConnections">Всего соединений за время наблюдения.</param>
/// <param name="BytesUp">Отправлено в Telegram.</param>
/// <param name="BytesDown">Получено из Telegram.</param>
/// <param name="WsErrors">Ошибки WebSocket.</param>
/// <param name="Rejected">Суммарно отклонено: ACL + лимит соединений + rate-limit.</param>
/// <param name="PoolHits">Попадания в пул WS.</param>
/// <param name="PoolMisses">Промахи пула WS (уход в fallback).</param>
/// <param name="FirstSeen">Когда запись впервые появилась.</param>
/// <param name="LastSeen">Последняя активность.</param>
public sealed record PeerStatsDto(
    string Ip,
    string? TokenId,
    string? TokenName,
    long ActiveConnections,
    long TotalConnections,
    long BytesUp,
    long BytesDown,
    long WsErrors,
    long Rejected,
    long PoolHits,
    long PoolMisses,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    /// <summary>Строит DTO из снимка реестра.</summary>
    public static PeerStatsDto From(PeerStatsSnapshot peer) => new(
        peer.Ip,
        peer.TokenId,
        peer.TokenName,
        peer.ActiveConnections,
        peer.TotalConnections,
        peer.BytesUp,
        peer.BytesDown,
        peer.WsErrors,
        peer.Rejected,
        peer.PoolHits,
        peer.PoolMisses,
        peer.FirstSeen,
        peer.LastSeen);
}

/// <summary>
/// Элемент ответа <c>GET /api/metrics/by-token</c> — использование трафика одним токеном целиком,
/// без разреза по адресам (F-9.5).
/// </summary>
/// <param name="TokenId">Идентификатор токена.</param>
/// <param name="TokenName">Имя токена (никогда не секрет, N-8).</param>
/// <param name="ActiveConnections">Активные соединения этого токена.</param>
/// <param name="TotalConnections">Всего соединений за время наблюдения.</param>
/// <param name="BytesUp">Отправлено в Telegram.</param>
/// <param name="BytesDown">Получено из Telegram.</param>
/// <param name="WsErrors">Ошибки WebSocket.</param>
/// <param name="Rejected">Отклонённые соединения, попавшие в разрез по этому токену.</param>
/// <param name="PoolHits">Попадания в пул WS.</param>
/// <param name="PoolMisses">Промахи пула WS.</param>
public sealed record TokenUsageDto(
    string TokenId,
    string TokenName,
    long ActiveConnections,
    long TotalConnections,
    long BytesUp,
    long BytesDown,
    long WsErrors,
    long Rejected,
    long PoolHits,
    long PoolMisses);

/// <summary>
/// Ответ <c>GET /api/per-ip</c>: страница отсортированного по трафику списка (F-4.7).
/// </summary>
/// <param name="Offset">Запрошенное смещение.</param>
/// <param name="Limit">Запрошенный размер страницы.</param>
/// <param name="Total">Всего адресов, попавших под фильтр.</param>
/// <param name="TrackedPeers">Всего отслеживаемых адресов в реестре.</param>
/// <param name="PeerTrackingOverflow">Сколько адресов не удалось зарегистрировать из-за лимита.</param>
/// <param name="Items">Элементы текущей страницы.</param>
public sealed record PerIpPageDto(
    int Offset,
    int Limit,
    int Total,
    long TrackedPeers,
    long PeerTrackingOverflow,
    IReadOnlyList<PeerStatsDto> Items);
