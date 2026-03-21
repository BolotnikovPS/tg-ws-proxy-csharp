#nullable enable

namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocket
{
    /// <summary>
    /// Отправляет бинарные данные в WebSocket-соединение.
    /// </summary>
    /// <param name="data">Полезная нагрузка для отправки.</param>
    Task Send(byte[] data);

    /// <summary>
    /// Получает следующий бинарный фрейм из WebSocket-соединения.
    /// </summary>
    /// <returns>Полученные данные либо <see langword="null"/>, если соединение закрыто.</returns>
    Task<byte[]?> Recv();

    /// <summary>
    /// Корректно закрывает WebSocket-соединение и освобождает ресурсы.
    /// </summary>
    Task Close();
}
