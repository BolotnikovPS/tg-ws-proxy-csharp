#nullable enable

namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocket
{
    /// <summary>
    /// Признак того, что соединение уже закрыто (локально или удалённой стороной).
    /// Используется пулом для отбраковки протухших соединений.
    /// </summary>
    bool IsClosed { get; }

    /// <summary>
    /// Признак того, что удалённая сторона уже закрыла соединение, даже если мы
    /// ещё не читали из него (аналог upstream <c>reader.at_eof()</c>).
    /// Пул обязан отбраковывать такие соединения: запись в них «успешна»,
    /// но ответа уже не будет.
    /// </summary>
    bool IsRemoteClosed { get; }

    /// <summary>
    /// Отправляет бинарные данные в WebSocket-соединение.
    /// </summary>
    /// <param name="data">Полезная нагрузка для отправки.</param>
    Task Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Отправляет несколько бинарных кадров подряд (один сброс в сокет).
    /// </summary>
    Task SendBatch(IReadOnlyList<ReadOnlyMemory<byte>> parts, CancellationToken cancellationToken);

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
