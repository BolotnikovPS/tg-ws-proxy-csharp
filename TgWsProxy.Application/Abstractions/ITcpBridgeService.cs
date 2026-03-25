#nullable enable

using System.Net.Sockets;

namespace TgWsProxy.Application.Abstractions;

public interface ITcpBridgeService
{
    /// <summary>
    /// Запускает двусторонний мост между клиентским TCP-потоком и WebSocket.
    /// </summary>
    /// <param name="client">Поток клиента.</param>
    /// <param name="ws">Активное WebSocket-соединение.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="init">
    /// Необязательный init MTProto (≥64 байт): включает разбиение апстрима на отдельные WS-кадры
    /// при нескольких сообщениях в одном TCP read.
    /// </param>
    Task BridgeWsAsync(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init, CancellationToken cancellationToken);

    /// <summary>
    /// Переключает сессию на прямой TCP-маршрут с предварительной отправкой init-пакета.
    /// </summary>
    /// <param name="client">Поток клиента.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="init">Инициализационный пакет для немедленной отправки на удаленную сторону.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    Task TcpFallbackAsync(NetworkStream client, string dst, int port, byte[] init, string scope, CancellationToken cancellationToken);

    /// <summary>
    /// Запускает прозрачный TCP-прокси без подмены инициализационных данных.
    /// </summary>
    /// <param name="client">Поток клиента.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    Task TcpPassthroughAsync(NetworkStream client, string dst, int port, string scope, CancellationToken cancellationToken);
}
