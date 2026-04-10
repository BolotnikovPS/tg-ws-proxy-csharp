#nullable enable

using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic.Helpers;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class TcpBridgeService(
    ILogger<TcpBridgeService> logger,
    IProxyStats stats,
    IRawWebSocketFactory wsFactory
    ) : ITcpBridgeService
{
    public async Task BridgeWs(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init, CancellationToken cancellationToken)
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
                    continue;
                }

                await ws.SendBatch(parts, ct);
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

        await ws.Close(ct);
    }

    public async Task BridgeWsReencrypt(
        NetworkStream client,
        IRawWebSocket ws,
        string scope,
        byte[] init,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken)
    {
        var splitter = new MtProtoMsgSplitter(init);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var up = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await client.ReadAsync(buf, ct);
                    if (n == 0)
                    {
                        var tail = splitter.Flush();
                        if (tail.Count > 0)
                        {
                            await ws.Send(tail[0], ct);
                        }
                        break;
                    }

                    stats.AddBytesUp(n);
                    // Decrypt client data
                    var plain = cltDecryptor.Update(buf.AsSpan(0, n).ToArray());
                    // Encrypt for Telegram
                    var encrypted = tgEncryptor.Update(plain);

                    var parts = splitter.Split(encrypted);
                    if (parts.Count == 0)
                    {
                        logger.LogDebug("[{Scope}] client->ws: {Len} bytes → 0 parts after split", scope, n);
                        continue;
                    }

                    if (parts.Count == 1)
                    {
                        await ws.Send(parts[0], ct);
                        logger.LogDebug("[{Scope}] client->ws: {Len} bytes → 1 part of {PartLen} bytes", scope, n, parts[0].Length);
                        continue;
                    }

                    await ws.SendBatch(parts, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] tcp->ws reencrypt ended", scope);
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var data = await ws.Recv(ct);
                    if (data is null)
                    {
                        logger.LogDebug("[{Scope}] ws->tcp: WS Recv returned null (peer closed)", scope);
                        break;
                    }

                    stats.AddBytesDown(data.Length);
                    // Decrypt Telegram data
                    var plain = tgDecryptor.Update(data);
                    // Encrypt for client
                    var encrypted = cltEncryptor.Update(plain);
                    await client.WriteAsync(encrypted, ct);
                    logger.LogDebug("[{Scope}] ws->tcp: {Len} bytes received", scope, data.Length);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] ws->tcp reencrypt ended", scope);
            }
        }, ct);

        await Task.WhenAny(up, down);
        linkedCts.Cancel();
        await Task.WhenAll(IgnoreTaskErrors(up, scope, "client->ws"), IgnoreTaskErrors(down, scope, "ws->client"));
    }

    public Task TcpFallback(NetworkStream client, string dst, int port, byte[] init, string scope, CancellationToken cancellationToken)
        => BridgeTcp(client, dst, port, scope, init, "fallback", cancellationToken);

    public async Task TcpFallbackReencrypt(
        NetworkStream client,
        string dst,
        int port,
        byte[] relayInit,
        string scope,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken)
    {
        var ipEndPoint = new IPEndPoint(IPAddress.Parse(dst), port);
        using var remote = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await remote.ConnectAsync(ipEndPoint, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Scope}] TCP fallback connect failed {Dst}:{Port}", scope, dst, port);
            return;
        }

        stats.IncConnectionsTcpFallback();
        remote.NoDelay = true;
        await using var rs = new NetworkStream(remote);

        try
        {
            await rs.WriteAsync(relayInit, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Failed to send relay_init to fallback", scope);
            return;
        }

        await BridgeTcpReencrypt(client, rs, scope, cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor, cancellationToken);
    }

    public async Task CfProxyFallback(
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
        CancellationToken cancellationToken)
    {
        var domain = $"kws{dc}.{cfProxyDomain}";
        var mediaTag = isMedia ? " media" : "";
        logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} -> CF proxy wss://{Domain}/apiws", scope, dc, mediaTag, domain);

        IRawWebSocket? ws = null;
        try
        {
            ws = await wsFactory.Connect(domain, domain, "/apiws", scope, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Scope}] DC{Dc}{MediaTag} CF proxy {Domain} failed", scope, dc, mediaTag, domain);
        }

        if (ws is null)
        {
            return;
        }

        stats.IncConnectionsCfProxy();
        try
        {
            await ws.Send(relayInit, cancellationToken);
            await BridgeWsReencrypt(client, ws, scope, relayInit, cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor, cancellationToken);
        }
        finally
        {
            await ws.Close(cancellationToken);
        }
    }

    public Task TcpPassthrough(NetworkStream client, string dst, int port, string scope, CancellationToken cancellationToken)
        => BridgeTcp(client, dst, port, scope, null, "passthrough", cancellationToken);

    /// <summary>
    /// Создает двунаправленный TCP-мост между клиентом и удаленным хостом.
    /// </summary>
    /// <param name="client">Поток клиентского подключения.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="init">Необязательный инициализационный пакет для предварительной отправки.</param>
    /// <param name="mode">Режим моста (fallback/passthrough) для логов.</param>
    private async Task BridgeTcp(NetworkStream client, string dst, int port, string scope, byte[]? init, string mode, CancellationToken cancellationToken)
    {
        var ipEndPoint = new IPEndPoint(IPAddress.Parse(dst), port);
        using var remote = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await remote.ConnectAsync(ipEndPoint, cancellationToken);
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
        await using var rs = new NetworkStream(remote);
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
        var t1 = Pipe(client, rs, true, ct);
        var t2 = Pipe(rs, client, false, ct);
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
    private async Task Pipe(Stream src, Stream dst, bool upstream, CancellationToken ct)
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

    /// <summary>
    /// Двунаправленный TCP-мост с ре-шифрованием.
    /// </summary>
    private async Task BridgeTcpReencrypt(
        NetworkStream client,
        NetworkStream remote,
        string scope,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var t1 = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await client.ReadAsync(buf, ct);
                    if (n == 0)
                    {
                        break;
                    }

                    stats.AddBytesUp(n);
                    var plain = cltDecryptor.Update(buf.AsSpan(0, n).ToArray());
                    var encrypted = tgEncryptor.Update(plain);
                    await remote.WriteAsync(encrypted, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] client->remote reencrypt ended", scope);
            }
        }, ct);

        var t2 = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await remote.ReadAsync(buf, ct);
                    if (n == 0)
                    {
                        break;
                    }

                    stats.AddBytesDown(n);
                    var plain = tgDecryptor.Update(buf.AsSpan(0, n).ToArray());
                    var encrypted = cltEncryptor.Update(plain);
                    await client.WriteAsync(encrypted, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] remote->client reencrypt ended", scope);
            }
        }, ct);

        await Task.WhenAny(t1, t2);
        linkedCts.Cancel();
        await Task.WhenAll(
            IgnorePipeTaskErrors(t1, scope, "reencrypt:client->remote"),
            IgnorePipeTaskErrors(t2, scope, "reencrypt:remote->client"));
    }
}
