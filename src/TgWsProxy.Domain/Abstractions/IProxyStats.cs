namespace TgWsProxy.Domain.Abstractions;

/// <summary>
/// Счётчики работы прокси. Набор и формат сводки соответствуют upstream stats.py.
/// </summary>
public interface IProxyStats
{
    /// <summary>
    /// Увеличивает общий счетчик входящих подключений.
    /// </summary>
    void IncConnectionsTotal();

    /// <summary>
    /// Увеличивает счётчик активных клиентских сессий.
    /// </summary>
    void IncConnectionsActive();

    /// <summary>
    /// Уменьшает счётчик активных клиентских сессий.
    /// </summary>
    void DecConnectionsActive();

    /// <summary>
    /// Увеличивает счетчик подключений, обслуженных через прямой WebSocket.
    /// </summary>
    void IncConnectionsWs();

    /// <summary>
    /// Увеличивает счетчик media-подключений, обслуженных через HTTP/2 мультиплексирование.
    /// </summary>
    void IncConnectionsH2();

    /// <summary>
    /// Увеличивает счётчик установленных TCP-соединений HTTP/2 транспорта.
    /// </summary>
    void IncH2TcpConnections();

    /// <summary>
    /// Увеличивает счётчик HTTP/2 запросов.
    /// </summary>
    void IncH2Requests();

    /// <summary>
    /// Увеличивает счётчик ошибок HTTP/2.
    /// </summary>
    void IncH2Errors();

    /// <summary>
    /// Увеличивает счетчик подключений, переведенных на TCP fallback.
    /// </summary>
    void IncConnectionsTcpFallback();

    /// <summary>
    /// Увеличивает счетчик подключений через Cloudflare proxy fallback.
    /// </summary>
    void IncConnectionsCfProxy();

    /// <summary>
    /// Увеличивает счётчик подключений через домен-фронтинг (Fake TLS маскировка).
    /// </summary>
    void IncConnectionsFronting();

    /// <summary>
    /// Увеличивает счётчик подключений с некорректным MTProto handshake (неверный secret).
    /// </summary>
    void IncConnectionsBad();

    /// <summary>
    /// Увеличивает счётчик подключений, отклонённых списком IP-доступа (--allow-ip/--deny-ip).
    /// </summary>
    void IncConnectionsBlocked();

    /// <summary>
    /// Увеличивает счётчик подключений, замаскированных под чужой домен.
    /// </summary>
    void IncConnectionsMasked();

    /// <summary>
    /// Увеличивает счётчик подключений, отклонённых из-за превышения лимита --max-connections.
    /// </summary>
    void IncConnectionsLimited();

    /// <summary>
    /// Увеличивает счётчик подключений, отклонённых ограничителем частоты --rate-limit.
    /// </summary>
    void IncConnectionsRateLimited();

    /// <summary>
    /// Увеличивает счетчик ошибок WebSocket.
    /// </summary>
    void IncWsErrors();

    /// <summary>
    /// Увеличивает счетчик успешных попаданий в пул WebSocket.
    /// </summary>
    void IncPoolHit();

    /// <summary>
    /// Увеличивает счетчик промахов по пулу WebSocket.
    /// </summary>
    void IncPoolMiss();

    /// <summary>
    /// Увеличивает счётчик попаданий в пул CF worker соединений.
    /// </summary>
    void IncCfPoolHit();

    /// <summary>
    /// Увеличивает счётчик промахов по пулу CF worker соединений.
    /// </summary>
    void IncCfPoolMiss();

    /// <summary>
    /// Добавляет объем исходящего трафика от клиента к апстриму.
    /// </summary>
    /// <param name="bytes">Количество байт.</param>
    void AddBytesUp(long bytes);

    /// <summary>
    /// Добавляет объем входящего трафика от апстрима к клиенту.
    /// </summary>
    /// <param name="bytes">Количество байт.</param>
    void AddBytesDown(long bytes);

    /// <summary>
    /// Возвращает человекочитаемое сводное состояние счетчиков.
    /// </summary>
    string Summary();

    /// <summary>
    /// Снимает согласованный числовой снимок счётчиков для выдачи в систему мониторинга.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    StatsSnapshot Snapshot();
}
