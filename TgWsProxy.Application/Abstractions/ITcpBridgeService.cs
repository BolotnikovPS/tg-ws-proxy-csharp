#nullable enable

using System.Net.Sockets;
using TgWsProxy.Application.Logic.Helpers;

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
    Task BridgeWs(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init, CancellationToken cancellationToken);

    /// <summary>
    /// Запускает двусторонний мост с ре-шифрованием между клиентским TCP и WebSocket. Клиент
    /// ciphertext → decrypt(clt_key) → encrypt(tg_key) → WS WS data → decrypt(tg_key) →
    /// encrypt(clt_key) → client TCP
    /// </summary>
    Task BridgeWsReencrypt(
        NetworkStream client,
        IRawWebSocket ws,
        string scope,
        byte[] init,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Переключает сессию на прямой TCP-маршрут с предварительной отправкой init-пакета.
    /// </summary>
    /// <param name="client">Поток клиента.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="init">Инициализационный пакет для немедленной отправки на удаленную сторону.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    Task TcpFallback(NetworkStream client, string dst, int port, byte[] init, string scope, CancellationToken cancellationToken);

    /// <summary>
    /// Переключает сессию на TCP fallback с ре-шифрованием.
    /// </summary>
    Task TcpFallbackReencrypt(
        NetworkStream client,
        string dst,
        int port,
        byte[] relayInit,
        string scope,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Запускает прозрачный TCP-прокси без подмены инициализационных данных.
    /// </summary>
    /// <param name="client">Поток клиента.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    Task TcpPassthrough(NetworkStream client, string dst, int port, string scope, CancellationToken cancellationToken);

    /// <summary>
    /// CF Proxy fallback с ре-шифрованием.
    /// </summary>
    Task CfProxyFallback(
        NetworkStream client,
        string scope,
        byte[] relayInit,
        int dc,
        bool isMedia,
        string cfProxyDomain,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken);
}
