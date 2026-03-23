#nullable enable

namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocket
{
    /// <summary>
    /// Отправляет бинарные данные в WebSocket-соединение.
    /// </summary>
    /// <param name="data">Полезная нагрузка для отправки.</param>
    Task Send(byte[] data, CancellationToken cancellationToken);

    /// <summary>Отправляет несколько бинарных кадров подряд (один сброс в сокет), как send_batch в Python.</summary>
    Task SendBatch(IReadOnlyList<byte[]> parts, CancellationToken cancellationToken);

    /// <summary>
    /// Получает следующий бинарный фрейм из WebSocket-соединения.
    /// </summary>
    /// <returns>Полученные данные либо <see langword="null"/>, если соединение закрыто.</returns>
    Task<byte[]?> Recv(CancellationToken cancellationToken);

    /// <summary>
    /// Корректно закрывает WebSocket-соединение и освобождает ресурсы.
    /// </summary>
    Task Close(CancellationToken cancellationToken);
}
