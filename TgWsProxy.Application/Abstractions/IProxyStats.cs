namespace TgWsProxy.Application.Abstractions;

public interface IProxyStats
{
    /// <summary>
    /// Увеличивает общий счетчик входящих подключений.
    /// </summary>
    void IncConnectionsTotal();

    /// <summary>
    /// Увеличивает счетчик подключений, обслуженных через WebSocket.
    /// </summary>
    void IncConnectionsWs();

    /// <summary>
    /// Увеличивает счетчик подключений, переведенных на TCP fallback.
    /// </summary>
    void IncConnectionsTcpFallback();

    /// <summary>
    /// Увеличивает счетчик прямых TCP passthrough-подключений.
    /// </summary>
    void IncConnectionsPassthrough();

    /// <summary>
    /// Увеличивает счетчик отклоненных HTTP-транспортов.
    /// </summary>
    void IncConnectionsHttpRejected();

    /// <summary>
    /// Увеличивает счетчик подключений через Cloudflare proxy fallback.
    /// </summary>
    void IncConnectionsCfProxy();

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
}
