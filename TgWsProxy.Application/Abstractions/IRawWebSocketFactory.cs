namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocketFactory
{
    /// <summary>
    /// Устанавливает TLS/WebSocket-соединение к заданному IP и домену.
    /// </summary>
    /// <param name="ip">Целевой IP-адрес для TCP-подключения.</param>
    /// <param name="domain">Домен для TLS SNI и HTTP Host.</param>
    /// <param name="path">Путь WebSocket-эндпоинта.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="timeout">Таймаут подключения; при <see langword="null"/> используется значение по умолчанию.</param>
    /// <returns>Инициализированное WebSocket-соединение.</returns>
    Task<IRawWebSocket> Connect(string ip, string domain, string path, string scope, TimeSpan? timeout = null);
}
