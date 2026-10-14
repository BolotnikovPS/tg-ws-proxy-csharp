#nullable enable

using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.Logic.Helpers;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Мост между клиентом и Telegram: WS с ре-шифрованием и цепочка fallback-маршрутов
/// (CF worker → CF proxy → прямой TCP), как в upstream <c>bridge.py</c>.
/// </summary>
internal sealed class TcpBridgeService(
    Config config,
    ILogger<TcpBridgeService> logger,
    IProxyStats stats,
    IRawWebSocketFactory wsFactory,
    ICfWorkerPool cfWorkerPool,
    ICfDomainBalancer balancer,
    ICfH2Pool cfH2Pool,
    H2Bridge h2Bridge) : ITcpBridgeService
{
    /// <summary>Начальная задержка backoff прямого TCP (TCP_BACKOFF_INITIAL).</summary>
    private static readonly TimeSpan TcpBackoffInitial = TimeSpan.FromSeconds(30);

    /// <summary>Максимальная задержка backoff прямого TCP (TCP_BACKOFF_MAX).</summary>
    private static readonly TimeSpan TcpBackoffMax = TimeSpan.FromSeconds(3600);

    /// <summary>Таймаут подключения к CF proxy / CF worker.</summary>
    private static readonly TimeSpan CfConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Размер буфера чтения от клиента (как 65536 в upstream <c>bridge.py</c>).</summary>
    private const int ClientReadBufferSize = 65536;

    private readonly Dictionary<(string Dst, int Port), int> tcpFailures = [];
    private readonly Dictionary<(string Dst, int Port), DateTimeOffset> tcpRetryAfter = [];
    private readonly HashSet<(string Dst, int Port)> tcpConnecting = [];
    private readonly object tcpSync = new();

    /// <inheritdoc />
    public async Task BridgeWsReencrypt(
        Stream client,
        IRawWebSocket ws,
        string scope,
        CryptoCtx ctx,
        int dc,
        bool isMedia,
        MsgSplitter? splitter,
        CancellationToken cancellationToken)
    {
        var mediaTag = isMedia ? " media" : "";
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;
        var started = DateTimeOffset.UtcNow;
        var closeReason = "unknown";

        var up = Task.Run(async () =>
        {
            var cipherBuf = ArrayPool<byte>.Shared.Rent(ClientReadBufferSize);
            var plainBuf = ArrayPool<byte>.Shared.Rent(ClientReadBufferSize);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await client.ReadAsync(cipherBuf.AsMemory(0, ClientReadBufferSize), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        var tail = splitter?.Flush() ?? [];
                        if (tail.Count > 0)
                        {
                            await ws.Send(tail[0], ct).ConfigureAwait(false);
                        }

                        closeReason = "client_eof";
                        break;
                    }

                    stats.AddBytesUp(n);
                    var parts = TransformUpstream(ctx, splitter, cipherBuf, plainBuf, n);
                    if (parts.Count == 0)
                    {
                        continue;
                    }

                    if (parts.Count == 1)
                    {
                        await ws.Send(parts[0], ct).ConfigureAwait(false);
                        continue;
                    }

                    await ws.SendBatch(parts, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                closeReason = "cancelled";
            }
            catch (Exception ex)
            {
                closeReason = DescribeClose(ex);
                logger.LogDebug(ex, "[{Scope}] client->ws ended", scope);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(cipherBuf);
                ArrayPool<byte>.Shared.Return(plainBuf);
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var data = await ws.Recv(ct).ConfigureAwait(false);
                    if (data is null)
                    {
                        closeReason = "ws_close";
                        break;
                    }

                    stats.AddBytesDown(data.Length);
                    // Расшифровка и повторное шифрование на месте в уже выделенном буфере кадра.
                    ctx.TgDecryptor.UpdateInPlace(data);
                    ctx.CltEncryptor.UpdateInPlace(data);
                    await client.WriteAsync(data, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                closeReason = "cancelled";
            }
            catch (Exception ex)
            {
                closeReason = DescribeClose(ex);
                logger.LogDebug(ex, "[{Scope}] ws->client ended", scope);
            }
        }, ct);

        await Task.WhenAny(up, down).ConfigureAwait(false);
        linkedCts.Cancel();
        await Task.WhenAll(
            IgnoreTaskErrors(up, scope, "client->ws"),
            IgnoreTaskErrors(down, scope, "ws->client")).ConfigureAwait(false);

        logger.LogDebug(
            "[{Scope}] WS session DC{Dc}{MediaTag} closed reason={Reason} duration={Duration:F1}s",
            scope, dc, mediaTag, closeReason, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    /// <inheritdoc />
    public async Task<bool> DoFallback(FallbackRequest request, CancellationToken cancellationToken)
    {
        var ipTable = request.IsTestDc ? Protocol.DcTestIps : Protocol.DcDefaultIps;
        var fallbackDst = ipTable.GetValueOrDefault(request.Dc);
        var useCf = config.CfProxyEnabled && !request.IsTestDc;
        var workerDomains = config.CfProxyWorkerDomains;

        var methods = new List<string>(3);
        if (workerDomains.Count > 0 && fallbackDst is not null)
        {
            methods.Add("cf_worker");
        }

        if (useCf)
        {
            methods.Add("cf");
        }

        if (fallbackDst is not null)
        {
            methods.Add("tcp");
        }

        foreach (var method in methods)
        {
            var handled = method switch
            {
                "cf_worker" when fallbackDst is not null =>
                    await CfWorkerFallbackAsync(request, fallbackDst, cancellationToken).ConfigureAwait(false),
                "cf" => await CfFallbackAsync(request, cancellationToken).ConfigureAwait(false),
                "tcp" when fallbackDst is not null =>
                    await TcpFallbackAsync(request, fallbackDst, cancellationToken).ConfigureAwait(false),
                _ => false,
            };

            if (handled)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void ResetTcpBackoff()
    {
        lock (tcpSync)
        {
            tcpFailures.Clear();
            tcpRetryAfter.Clear();
            tcpConnecting.Clear();
        }
    }

    /// <summary>
    /// Fallback через Cloudflare: для media-трафика сначала HTTP/2-мультиплексирование
    /// (<c>/api</c>), затем обычный CF proxy по WebSocket.
    /// </summary>
    private async Task<bool> CfFallbackAsync(FallbackRequest request, CancellationToken cancellationToken)
    {
        if (request is { IsMedia: true, ProtoTag: not null } && config.H2Enabled)
        {
            ICfH2Channel? channel = null;
            try
            {
                channel = await cfH2Pool.OpenAsync(request.Dc, request.Scope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    "[{Scope}] H2 DC{Dc} open failed: {Error}; using WS route",
                    request.Scope, request.Dc, ex.Message);
            }

            if (channel is not null)
            {
                stats.IncConnectionsH2();
                stats.IncConnectionsCfProxy();
                logger.LogInformation(
                    "[{Scope}] DC{Dc} media -> H2 (Cloudflare /api)",
                    request.Scope, request.Dc);
                try
                {
                    await h2Bridge
                        .BridgeAsync(
                            request.Client,
                            channel,
                            request.Crypto,
                            request.ProtoTag,
                            request.Scope,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                return true;
            }
        }

        return await CfProxyFallbackAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fallback через Cloudflare Worker: сначала пул, затем перебор доменов воркеров.
    /// </summary>
    private async Task<bool> CfWorkerFallbackAsync(
        FallbackRequest request,
        string fallbackDst,
        CancellationToken cancellationToken)
    {
        var workerDomains = config.CfProxyWorkerDomains;
        if (workerDomains.Count == 0)
        {
            return false;
        }

        var mediaTag = request.IsMedia ? " media" : "";
        IRawWebSocket? ws = null;

        var pooled = request.IsTestDc
            ? null
            : cfWorkerPool.TryGet(request.Dc, fallbackDst, workerDomains);

        if (pooled is { } hit)
        {
            ws = hit.Ws;
            logger.LogInformation(
                "[{Scope}] DC{Dc}{MediaTag} -> CF worker pool hit via {Domain} for {Dst}",
                request.Scope, request.Dc, mediaTag, hit.Domain, fallbackDst);
        }
        else
        {
            var path = $"{Protocol.WsPath}?dst={Uri.EscapeDataString(fallbackDst)}&dc={request.Dc}";
            foreach (var workerDomain in cfWorkerPool.AvailableDomains(workerDomains))
            {
                logger.LogInformation(
                    "[{Scope}] DC{Dc}{MediaTag} -> trying CF worker {Domain} for {Dst}",
                    request.Scope, request.Dc, mediaTag, workerDomain, fallbackDst);
                try
                {
                    ws = await wsFactory
                        .Connect(
                            workerDomain,
                            workerDomain,
                            path,
                            request.Scope,
                            CfConnectTimeout,
                            secure: !config.DisableSecure)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    cfWorkerPool.ReportFailure(workerDomain, ex);
                    logger.LogWarning(
                        "[{Scope}] DC{Dc}{MediaTag} CF worker {Domain} failed: {Error}",
                        request.Scope, request.Dc, mediaTag, workerDomain, ex.Message);
                }
            }

            if (ws is null)
            {
                return false;
            }
        }

        stats.IncConnectionsCfProxy();
        try
        {
            await ws.Send(request.RelayInit, cancellationToken).ConfigureAwait(false);
            // upstream передаёт splitter=None для worker-маршрута.
            await BridgeWsReencrypt(
                request.Client, ws, request.Scope, request.Crypto,
                request.Dc, request.IsMedia, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await CloseQuietlyAsync(ws, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fallback через CF proxy: перебор базовых доменов из балансировщика.
    /// </summary>
    private async Task<bool> CfProxyFallbackAsync(FallbackRequest request, CancellationToken cancellationToken)
    {
        var mediaTag = request.IsMedia ? " media" : "";
        logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} -> trying CF proxy", request.Scope, request.Dc, mediaTag);

        IRawWebSocket? ws = null;
        string? chosenDomain = null;

        foreach (var baseDomain in balancer.DomainsForDc(request.Dc))
        {
            var domain = $"kws{request.Dc}.{baseDomain}";
            try
            {
                ws = await wsFactory
                    .Connect(
                        domain,
                        domain,
                        Protocol.WsPath,
                        request.Scope,
                        CfConnectTimeout,
                        secure: !config.DisableSecure)
                    .ConfigureAwait(false);
                chosenDomain = baseDomain;
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "[{Scope}] DC{Dc}{MediaTag} CF proxy failed: {Error}",
                    request.Scope, request.Dc, mediaTag, ex.Message);
            }
        }

        if (ws is null)
        {
            return false;
        }

        if (chosenDomain is not null && balancer.UpdateDomainForDc(request.Dc, chosenDomain))
        {
            logger.LogInformation("[{Scope}] Switched active CF domain to {Domain}", request.Scope, chosenDomain);
        }

        stats.IncConnectionsCfProxy();
        try
        {
            await ws.Send(request.RelayInit, cancellationToken).ConfigureAwait(false);
            await BridgeWsReencrypt(
                request.Client, ws, request.Scope, request.Crypto,
                request.Dc, request.IsMedia, request.Splitter, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await CloseQuietlyAsync(ws, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Прямой TCP fallback на 443 с дедупликацией попыток и экспоненциальным backoff.
    /// </summary>
    private async Task<bool> TcpFallbackAsync(
        FallbackRequest request,
        string dst,
        CancellationToken cancellationToken)
    {
        const int port = Protocol.WsPort;
        var key = (dst, port);

        lock (tcpSync)
        {
            if (tcpConnecting.Contains(key) ||
                (tcpRetryAfter.TryGetValue(key, out var after) && DateTimeOffset.UtcNow < after))
            {
                logger.LogDebug(
                    "[{Scope}] TCP fallback to {Dst}:{Port} skipped (backoff or connecting)",
                    request.Scope, dst, port);
                return false;
            }

            tcpConnecting.Add(key);
        }

        Socket? remote = null;
        try
        {
            try
            {
                logger.LogInformation("[{Scope}] TCP fallback to {Dst}:{Port}", request.Scope, dst, port);
                var ipEndPoint = new IPEndPoint(IPAddress.Parse(dst), port);
                remote = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(CfConnectTimeout);
                await remote.ConnectAsync(ipEndPoint, connectCts.Token).ConfigureAwait(false);
                remote.NoDelay = true;
            }
            catch (Exception ex)
            {
                lock (tcpSync)
                {
                    var failures = tcpFailures.GetValueOrDefault(key) + 1;
                    tcpFailures[key] = failures;
                    var delay = Backoff(failures);
                    tcpRetryAfter[key] = DateTimeOffset.UtcNow + delay;
                    logger.LogWarning(
                        "[{Scope}] TCP fallback to {Dst}:{Port} failed, retry in {Delay:F0}s: {Error}",
                        request.Scope, dst, port, delay.TotalSeconds, ex.Message);
                }

                remote?.Dispose();
                return false;
            }

            lock (tcpSync)
            {
                tcpFailures.Remove(key);
                tcpRetryAfter.Remove(key);
            }

            stats.IncConnectionsTcpFallback();
            await using var rs = new NetworkStream(remote, ownsSocket: true);
            await rs.WriteAsync(request.RelayInit, cancellationToken).ConfigureAwait(false);
            await BridgeTcpReencryptAsync(request.Client, rs, request.Scope, request.Crypto, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (tcpSync)
            {
                tcpConnecting.Remove(key);
            }
        }
    }

    /// <summary>
    /// Двунаправленный TCP-мост с ре-шифрованием (client↔Telegram).
    /// </summary>
    private async Task BridgeTcpReencryptAsync(
        Stream client,
        NetworkStream remote,
        string scope,
        CryptoCtx ctx,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var up = Task.Run(async () =>
        {
            var cipherBuf = ArrayPool<byte>.Shared.Rent(ClientReadBufferSize);
            var plainBuf = ArrayPool<byte>.Shared.Rent(ClientReadBufferSize);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await client.ReadAsync(cipherBuf.AsMemory(0, ClientReadBufferSize), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    stats.AddBytesUp(n);
                    // Порядок как в upstream: plain = clt_dec(chunk); chunk = tg_enc(plain).
                    // Оба преобразования — XOR keystream, поэтому идут без аллокаций.
                    ctx.CltDecryptor.UpdateInto(cipherBuf.AsSpan(0, n), plainBuf.AsSpan(0, n));
                    ctx.TgEncryptor.UpdateInPlace(cipherBuf.AsSpan(0, n));
                    await remote.WriteAsync(cipherBuf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] client->remote reencrypt ended", scope);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(cipherBuf);
                ArrayPool<byte>.Shared.Return(plainBuf);
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            var buf = ArrayPool<byte>.Shared.Rent(ClientReadBufferSize);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await remote.ReadAsync(buf.AsMemory(0, ClientReadBufferSize), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    stats.AddBytesDown(n);
                    ctx.TgDecryptor.UpdateInPlace(buf.AsSpan(0, n));
                    ctx.CltEncryptor.UpdateInPlace(buf.AsSpan(0, n));
                    await client.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[{Scope}] remote->client reencrypt ended", scope);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }, ct);

        await Task.WhenAny(up, down).ConfigureAwait(false);
        linkedCts.Cancel();
        await Task.WhenAll(
            IgnoreTaskErrors(up, scope, "reencrypt:client->remote"),
            IgnoreTaskErrors(down, scope, "reencrypt:remote->client")).ConfigureAwait(false);
    }

    /// <summary>
    /// Расшифровывает клиентский фрагмент, заново шифрует его для Telegram и разбивает
    /// на MTProto-пакеты. Выполняется синхронно (вне async-метода), чтобы работать
    /// со <see cref="Span{T}"/> без промежуточных аллокаций.
    /// </summary>
    /// <param name="ctx">Криптоконтекст сессии.</param>
    /// <param name="splitter">Разборщик пакетов либо <see langword="null"/> (CF worker — кадр целиком).</param>
    /// <param name="buffer">Буфер с шифротекстом клиента (перезаписывается шифротекстом для Telegram).</param>
    /// <param name="plainBuffer">Буфер под расшифрованные данные (нужен разборщику для границ пакетов).</param>
    /// <param name="length">Количество валидных байт в буферах.</param>
    /// <returns>Пакеты для отправки в WS.</returns>
    internal static List<byte[]> TransformUpstream(
        CryptoCtx ctx,
        MsgSplitter? splitter,
        byte[] buffer,
        byte[] plainBuffer,
        int length)
    {
        var cipher = buffer.AsSpan(0, length);
        var plain = plainBuffer.AsSpan(0, length);

        // Порядок как в upstream bridge.py: plain = clt_dec(chunk); chunk = tg_enc(plain).
        // Расшифровка и повторное шифрование идут в разные буферы: разборщику нужны оба
        // представления — шифротекст (срезы для WS) и plaintext (границы пакетов).
        ctx.CltDecryptor.UpdateInto(cipher, plain);
        ctx.TgEncryptor.UpdateInto(plain, cipher);

        return splitter?.Split(cipher, plain) ?? [cipher.ToArray()];
    }

    /// <summary>
    /// Закрывает WS-соединение, подавляя ошибки закрытия.
    /// </summary>
    private async Task CloseQuietlyAsync(IRawWebSocket ws, CancellationToken cancellationToken)
    {
        try
        {
            await ws.Close(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WS close failed");
        }
    }

    /// <summary>
    /// Считает задержку экспоненциального backoff (min(initial * 2^(failures-1), max)).
    /// </summary>
    private static TimeSpan Backoff(int failures)
    {
        var exponent = Math.Min(failures - 1, 7);
        var seconds = Math.Min(TcpBackoffInitial.TotalSeconds * Math.Pow(2, exponent), TcpBackoffMax.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Строит короткое имя причины закрытия сессии для лога.
    /// </summary>
    private static string DescribeClose(Exception ex) => ex switch
    {
        SocketException se when se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted => "tcp_reset",
        IOException io when io.InnerException is SocketException inner &&
                            inner.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted => "tcp_reset",
        EndOfStreamException => "tcp_eof",
        WsFrameTooLargeException => "ws_frame_too_large",
        _ => ex.GetType().Name,
    };

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
            await task.ConfigureAwait(false);
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
}
