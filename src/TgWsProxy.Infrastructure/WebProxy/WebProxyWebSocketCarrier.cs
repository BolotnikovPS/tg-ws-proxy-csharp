using Microsoft.Extensions.Logging;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Дуплексный канал WebSocket-носителя: одно бинарное сообщение в каждую сторону несёт батч
/// relay-кадров. Абстракция отделяет протокол носителя от ASP.NET Core, чтобы цикл можно было
/// тестировать без Kestrel.
/// </summary>
public interface IWebProxyWebSocketChannel
{
    /// <summary>
    /// Читает следующий батч клиента. <see langword="null"/> — соединение закрыто штатно.
    /// </summary>
    ValueTask<byte[]?> ReceiveBatchAsync(CancellationToken ct);

    /// <summary>Отправляет батч кадров одним бинарным сообщением.</summary>
    ValueTask SendBatchAsync(ReadOnlyMemory<byte> batch, CancellationToken ct);

    /// <summary>Отправляет ping (держит соединение живым, пока нет данных).</summary>
    ValueTask SendPingAsync(CancellationToken ct);
}

/// <summary>
/// Носитель <c>websocket</c>: одна WebSocket-сессия на relay-сессию, батчи кадров в обе стороны.
/// Заменяет long-poll пару <c>POST /api/v1/up</c> + <c>POST /api/v1/down</c> при
/// <c>webProxyCarrierMode = websocket</c> (F-W.24).
/// </summary>
public static class WebProxyWebSocketCarrier
{
    /// <summary>
    /// Гоняет дуплексный обмен до закрытия любой из сторон. Половина, завершившаяся первой,
    /// отменяет вторую; отмена и разрыв соединения не считаются ошибкой.
    /// </summary>
    /// <param name="session">Relay-сессия, созданная через <c>POST /api/v1/session</c>.</param>
    /// <param name="channel">Канал WebSocket.</param>
    /// <param name="idleTimeout">
    /// Предел простоя приёмника исходящих данных; по истечении отправляется ping (эталон — 2×
    /// длительности long-poll).
    /// </param>
    /// <param name="logger">Лог протокольных нарушений.</param>
    /// <param name="ct">Отмена при остановке сервера.</param>
    public static async Task RunAsync(
        WebProxyHttpSession session,
        IWebProxyWebSocketChannel channel,
        TimeSpan idleTimeout,
        ILogger logger,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var uplink = RunUplinkAsync(session, channel, logger, token);
        var downlink = RunDownlinkAsync(session, channel, idleTimeout, token);

        await Task.WhenAny(uplink, downlink).ConfigureAwait(false);
        linked.Cancel();

        try
        {
            await Task.WhenAll(uplink, downlink).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение носителя.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogDebug(ex, "web-proxy websocket carrier closed with transport error");
        }
    }

    private static async Task RunUplinkAsync(
        WebProxyHttpSession session,
        IWebProxyWebSocketChannel channel,
        ILogger logger,
        CancellationToken ct)
    {
        long seq = 0;
        while (!ct.IsCancellationRequested)
        {
            var payload = await channel.ReceiveBatchAsync(ct).ConfigureAwait(false);
            if (payload is null)
            {
                return; // закрытие инициировал клиент
            }

            if (payload.Length == 0)
            {
                logger.LogDebug("web-proxy websocket uplink: empty batch, closing session");
                return;
            }

            seq++;
            var result = await session.ProcessUpAsync(seq, payload, ct).ConfigureAwait(false);
            if (!result.Accepted)
            {
                logger.LogDebug("web-proxy websocket uplink: batch {Seq} rejected, closing session", seq);
                return;
            }
        }
    }

    private static async Task RunDownlinkAsync(
        WebProxyHttpSession session,
        IWebProxyWebSocketChannel channel,
        TimeSpan idleTimeout,
        CancellationToken ct)
    {
        // Стартовый курсор совпадает с X-Down-Cursor=0 из ответа на создание сессии.
        long cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleTimeout);

            var downlink = await session.PollAsync(cursor, idle.Token).ConfigureAwait(false);
            if (downlink.Closed)
            {
                return;
            }

            if (downlink.Batch is null)
            {
                // Нет данных за время простоя — keepalive, иначе прокси на пути закроет соединение.
                await channel.SendPingAsync(ct).ConfigureAwait(false);
                continue;
            }

            await channel.SendBatchAsync(downlink.Batch, ct).ConfigureAwait(false);
            cursor = downlink.Cursor;
        }
    }
}
