#nullable enable

using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure;

internal sealed class TcpBridgeService(ILogger<TcpBridgeService> logger, IProxyStats stats) : ITcpBridgeService
{
    public async Task BridgeWsAsync(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init, CancellationToken cancellationToken)
    {
        var splitter = init is { Length: >= 64 } ? new MtProtoMsgSplitter(init) : null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;
        var up = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (!ct.IsCancellationRequested)
            {
                var n = await client.ReadAsync(buf, ct);
                if (n == 0)
                {
                    break;
                }

                var payload = buf.AsSpan(0, n).ToArray();
                stats.AddBytesUp(payload.Length);
                if (splitter is null)
                {
                    await ws.Send(payload, ct);
                    continue;
                }

                var parts = splitter.Split(payload);
                if (parts.Count <= 1)
                {
                    await ws.Send(parts[0], ct);
                }
                else
                {
                    await ws.SendBatch(parts, ct);
                }
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var data = await ws.Recv(ct);
                if (data is null)
                {
                    break;
                }

                stats.AddBytesDown(data.Length);
                await client.WriteAsync(data, ct);
            }
        }, ct);

        await Task.WhenAny(up, down);
        linkedCts.Cancel();
        await Task.WhenAll(IgnoreTaskErrors(up, scope, "client->ws"), IgnoreTaskErrors(down, scope, "ws->client"));
        try
        {
            await ws.Close(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Failed to close WS connection", scope);
        }
    }

    public Task TcpFallbackAsync(NetworkStream client, string dst, int port, byte[] init, string scope, CancellationToken cancellationToken)
        => BridgeTcpAsync(client, dst, port, scope, init, "fallback", cancellationToken);

    public Task TcpPassthroughAsync(NetworkStream client, string dst, int port, string scope, CancellationToken cancellationToken)
        => BridgeTcpAsync(client, dst, port, scope, null, "passthrough", cancellationToken);

    /// <summary>
    /// Создает двунаправленный TCP-мост между клиентом и удаленным хостом.
    /// </summary>
    /// <param name="client">Поток клиентского подключения.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="init">Необязательный инициализационный пакет для предварительной отправки.</param>
    /// <param name="mode">Режим моста (fallback/passthrough) для логов.</param>
    private async Task BridgeTcpAsync(NetworkStream client, string dst, int port, string scope, byte[]? init, string mode, CancellationToken cancellationToken)
    {
        using var remote = new TcpClient();
        try
        {
            await remote.ConnectAsync(dst, port, cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            logger.LogWarning("[{Scope}] TCP {Mode} connect unavailable {Dst}:{Port} ({SocketError})", scope, mode, dst, port, ex.SocketErrorCode);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TCP {Mode} connect failed {Dst}:{Port}", scope, mode, dst, port);
            return;
        }

        remote.NoDelay = true;
        await using var rs = remote.GetStream();
        if (init is not null)
        {
            try
            {
                await rs.WriteAsync(init, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Scope}] Failed to send init packet to fallback target", scope);
                return;
            }
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;
        var t1 = PipeAsync(client, rs, true, ct);
        var t2 = PipeAsync(rs, client, false, ct);
        await Task.WhenAny(t1, t2);
        linkedCts.Cancel();
        await Task.WhenAll(
            IgnorePipeTaskErrors(t1, scope, $"{mode}:client->remote"),
            IgnorePipeTaskErrors(t2, scope, $"{mode}:remote->client"));
    }

    /// <summary>
    /// Копирует данные из одного потока в другой и учитывает переданный трафик.
    /// </summary>
    /// <param name="src">Исходный поток чтения.</param>
    /// <param name="dst">Целевой поток записи.</param>
    /// <param name="upstream">Признак направления client-&gt;remote.</param>
    /// <param name="ct">Токен отмены операции копирования.</param>
    private async Task PipeAsync(Stream src, Stream dst, bool upstream, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            var n = await src.ReadAsync(buf, ct);
            if (n == 0)
            {
                break;
            }

            if (upstream)
            {
                stats.AddBytesUp(n);
            }
            else
            {
                stats.AddBytesDown(n);
            }

            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    /// <summary>
    /// Ожидает завершение задачи моста и подавляет ожидаемые ошибки отключения.
    /// </summary>
    /// <param name="task">Задача канала моста.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="channel">Логическое имя канала передачи.</param>
    private async Task IgnoreTaskErrors(Task task, string scope, string channel)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} canceled", scope, channel);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se &&
                                     se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} connection closed by peer", scope, channel);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} connection closed by peer", scope, channel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Bridge channel failed: {Channel}", scope, channel);
        }
    }

    /// <summary>
    /// Ожидает завершение TCP-задачи и мягко обрабатывает типовые ошибки разрыва.
    /// </summary>
    /// <param name="task">Задача канала TCP fallback/passthrough.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="channel">Логическое имя канала передачи.</param>
    private async Task IgnorePipeTaskErrors(Task task, string scope, string channel)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] TCP fallback {Channel} canceled", scope, channel);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Peer closed/reset the TCP connection; this is expected during disconnects.
            logger.LogDebug("[{Scope}] TCP fallback {Channel} connection reset by peer", scope, channel);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            logger.LogDebug("[{Scope}] TCP fallback {Channel} connection reset by peer", scope, channel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TCP fallback channel failed: {Channel}", scope, channel);
        }
    }
}
