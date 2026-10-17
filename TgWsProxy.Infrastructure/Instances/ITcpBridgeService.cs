#nullable enable

using System.Net;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Мост между клиентским TCP-потоком и Telegram (WS, CF proxy, CF worker, H2 или прямой TCP).
/// </summary>
public interface ITcpBridgeService
{
    /// <summary>
    /// Двусторонний мост с ре-шифрованием: клиентский шифротекст расшифровывается ключом клиента
    /// и заново шифруется ключом Telegram перед отправкой в WS; обратное направление — зеркально.
    /// </summary>
    /// <param name="client">Клиентский TCP-поток.</param>
    /// <param name="ws">Активное WS-соединение с Telegram.</param>
    /// <param name="scope">Идентификатор сессии для логов.</param>
    /// <param name="ctx">Набор AES-CTR потоков сессии.</param>
    /// <param name="dc">Номер дата-центра (для логов).</param>
    /// <param name="isMedia">Признак media-трафика (для логов).</param>
    /// <param name="splitter">Разборщик MTProto-пакетов либо <see langword="null"/>.</param>
    /// <param name="peer">Адрес клиента без порта для статистики по IP (может быть <see langword="null"/>).</param>
    /// <param name="tokenId">Идентификатор токена сессии — вторая часть ключа учёта (F-9.2).</param>
    /// <param name="cancellationToken">Токен отмены сессии.</param>
    Task BridgeWsReencrypt(
        Stream client,
        IRawWebSocket ws,
        string scope,
        CryptoCtx ctx,
        int dc,
        bool isMedia,
        MsgSplitter? splitter,
        IPAddress? peer,
        string? tokenId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Перебирает доступные методы fallback в порядке upstream: CF worker → CF proxy (H2 для media)
    /// → прямой TCP. Возвращает <see langword="true"/>, если один из методов сработал.
    /// </summary>
    /// <param name="request">Параметры fallback-запроса.</param>
    /// <param name="cancellationToken">Токен отмены сессии.</param>
    /// <returns><see langword="true"/>, если сессия была обслужена fallback-маршрутом.</returns>
    Task<bool> DoFallback(FallbackRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Сбрасывает состояние TCP-бэкоффа (вызывается при старте прокси, аналог <c>reset_tcp_backoff</c>).
    /// </summary>
    void ResetTcpBackoff();
}
