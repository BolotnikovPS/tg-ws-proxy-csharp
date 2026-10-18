namespace TgWsProxy.Domain;

/// <summary>
/// Снимок счётчиков одной пары «IP-адрес, токен доступа» для <c>GET /api/per-ip</c> и
/// Prometheus-метрик (F-4, F-9.1). Адрес нормализован: IPv4-mapped IPv6 приведён к IPv4,
/// порт отброшен.
/// </summary>
/// <param name="Ip">Текстовое представление адреса (например <c>203.0.113.7</c>).</param>
/// <param name="TokenId">
/// Идентификатор токена доступа либо <see langword="null"/>: отклонённые соединения учитываются
/// по IP без токена (F-9.3), токен на этапе accept ещё неизвестен.
/// </param>
/// <param name="TokenName">
/// Имя токена для отображения и метки <c>token</c> в Prometheus (F-9.6, N-8: никогда не секрет);
/// <see langword="null"/>, если токен удалён.
/// </param>
/// <param name="ActiveConnections">Сессии, которые идут прямо сейчас.</param>
/// <param name="TotalConnections">Всего принятых сессий (включая отклонённые ограничителями).</param>
/// <param name="BlockedConnections">Отклонено по IP ACL.</param>
/// <param name="LimitedConnections">Отклонено по общему лимиту <c>maxConnections</c>.</param>
/// <param name="RateLimitedConnections">Отклонено по лимиту частоты для этого IP.</param>
/// <param name="BytesUp">Отправлено клиентом в сторону Telegram.</param>
/// <param name="BytesDown">Принято от Telegram в сторону клиента.</param>
/// <param name="WsErrors">Ошибки WebSocket/рукопожатия по этой паре.</param>
/// <param name="PoolHits">Попаданий в пул WS-соединений.</param>
/// <param name="PoolMisses">Промахов пула WS-соединений (ушли в fallback).</param>
/// <param name="FirstSeen">Когда пара впервые появилась в реестре.</param>
/// <param name="LastSeen">Последняя активность пары.</param>
public sealed record PeerStatsSnapshot(
    string Ip,
    string? TokenId,
    string? TokenName,
    long ActiveConnections,
    long TotalConnections,
    long BlockedConnections,
    long LimitedConnections,
    long RateLimitedConnections,
    long BytesUp,
    long BytesDown,
    long WsErrors,
    long PoolHits,
    long PoolMisses,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    /// <summary>Суммарный трафик пары в обе стороны — ключ сортировки для <c>/api/per-ip</c>.</summary>
    public long TotalBytes => BytesUp + BytesDown;

    /// <summary>Сколько соединений отклонено всеми ограничителями (F-4.5).</summary>
    public long Rejected => BlockedConnections + LimitedConnections + RateLimitedConnections;
}
