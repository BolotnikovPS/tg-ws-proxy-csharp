namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Транспорт одного relay-соединения WEB proxy: исходящие кадры (как <see cref="IRelayFrameSink"/>)
/// и входящие кадры. Реализация владеет нижележащим соединением (WebSocket в хосте) и обязана
/// быть безопасной для конкурентных вызовов <see cref="IRelayFrameSink.SendAsync"/>.
/// </summary>
public interface IRelayTransport : IRelayFrameSink
{
    /// <summary>
    /// Получает следующий разобранный кадр. <see langword="null"/> — поток закрыт (EOF).
    /// Некорректный кадр (недвоичное WS-сообщение, плохой заголовок) — исключение
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    ValueTask<RelayFrame?> ReceiveAsync(CancellationToken ct);
}
