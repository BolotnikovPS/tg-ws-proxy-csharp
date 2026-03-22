using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Application.Logic;

internal sealed class ClientSessionHandler(
    Config cfg,
    Dictionary<int, string> dcOpt,
    IRawWebSocketFactory wsFactory,
    ITcpBridgeService bridgeService,
    IMtProtoInspector mtProtoInspector,
    IWsRoutingState wsRoutingState,
    IProxyStats stats,
    ILogger<ClientSessionHandler> logger
    ) : IClientSessionHandler
{
    private const int WsDefaultTimeoutSeconds = 10;
    private const int WsFailTimeoutSeconds = 2;
    private static readonly TimeSpan SocksIoTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WsPoolMaxAge = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DcFailCooldown = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, (int Dc, bool IsMedia)> IpToDc = new(StringComparer.Ordinal)
    {
        ["149.154.175.50"] = (1, false),
        ["149.154.175.51"] = (1, false),
        ["149.154.175.53"] = (1, false),
        ["149.154.175.54"] = (1, false),
        ["149.154.175.52"] = (1, true),
        ["149.154.167.41"] = (2, false),
        ["149.154.167.50"] = (2, false),
        ["149.154.167.51"] = (2, false),
        ["149.154.167.220"] = (2, false),
        ["95.161.76.100"] = (2, false),
        ["149.154.167.151"] = (2, true),
        ["149.154.167.222"] = (2, true),
        ["149.154.167.223"] = (2, true),
        ["149.154.162.123"] = (2, true),
        ["149.154.175.100"] = (3, false),
        ["149.154.175.101"] = (3, false),
        ["149.154.175.102"] = (3, true),
        ["149.154.167.91"] = (4, false),
        ["149.154.167.92"] = (4, false),
        ["149.154.164.250"] = (4, true),
        ["149.154.166.120"] = (4, true),
        ["149.154.166.121"] = (4, true),
        ["149.154.167.118"] = (4, true),
        ["149.154.165.111"] = (4, true),
        ["91.108.56.100"] = (5, false),
        ["91.108.56.101"] = (5, false),
        ["91.108.56.116"] = (5, false),
        ["91.108.56.126"] = (5, false),
        ["149.154.171.5"] = (5, false),
        ["91.108.56.102"] = (5, true),
        ["91.108.56.128"] = (5, true),
        ["91.108.56.151"] = (5, true),
        ["91.105.192.100"] = (203, false)
    };

    public async Task HandleAsync(TcpClient client, ClientContext context, CancellationToken cancellationToken)
    {
        stats.IncConnectionsTotal();
        using var _ = client;
        client.NoDelay = true;
        await using var stream = client.GetStream();

        logger.LogInformation("[{Scope}] Client connected", context.Scope);

        try
        {
            if (!await HandleSocks5Auth(stream, cfg.Credentials, context, cancellationToken))
            {
                logger.LogWarning("[{Scope}] SOCKS5 authentication failed", context.Scope);
                return;
            }

            var req = await IoUtil.ReadExact(stream, 4, SocksIoTimeout, cancellationToken);
            if (req[1] != 1)
            {
                await stream.WriteAsync(Socks5Reply(0x07), cancellationToken);
                return;
            }

            var atyp = req[3];
            string dst;
            if (atyp == 1)
            {
                dst = new IPAddress(await IoUtil.ReadExact(stream, 4, SocksIoTimeout, cancellationToken)).ToString();
            }
            else if (atyp == 3)
            {
                var dlen = (await IoUtil.ReadExact(stream, 1, SocksIoTimeout, cancellationToken))[0];
                dst = Encoding.ASCII.GetString(await IoUtil.ReadExact(stream, dlen, SocksIoTimeout, cancellationToken));
            }
            else
            {
                await stream.WriteAsync(Socks5Reply(0x08), cancellationToken);
                return;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(await IoUtil.ReadExact(stream, 2, SocksIoTimeout, cancellationToken));
            if (dst.Contains(':'))
            {
                await stream.WriteAsync(Socks5Reply(0x05), cancellationToken);
                return;
            }

            await stream.WriteAsync(Socks5Reply(0x00), cancellationToken);
            if (!IsTelegramIp(dst))
            {
                stats.IncConnectionsPassthrough();
                logger.LogDebug("[{Scope}] Non-Telegram destination {Dst}:{Port}, TCP passthrough", context.Scope, dst, port);
                await bridgeService.TcpPassthroughAsync(stream, dst, port, context.Scope);
                return;
            }

            var init = await IoUtil.ReadExact(stream, 64, InitReadTimeout, cancellationToken);
            if (mtProtoInspector.IsHttpTransport(init))
            {
                stats.IncConnectionsHttpRejected();
                logger.LogWarning("[{Scope}] HTTP transport detected on Telegram route; closing", context.Scope);
                return;
            }

            var (dc, isMedia) = mtProtoInspector.DcFromInit(init);
            if (dc is null && IpToDc.TryGetValue(dst, out var ipInfo))
            {
                dc = ipInfo.Dc;
                isMedia = ipInfo.IsMedia;
                if (dcOpt.ContainsKey(dc.Value))
                {
                    // Keep Python behavior: patch with dc for media, -dc otherwise.
                    var patchRaw = (short)(isMedia.Value ? dc.Value : -dc.Value);
                    init = mtProtoInspector.PatchInitDc(init, patchRaw);
                    logger.LogDebug("[{Scope}] MTProto init patched using destination IP map (DC{Dc}, media={IsMedia})", context.Scope, dc, isMedia);
                }
            }

            if (dc is null || !dcOpt.TryGetValue(dc.Value, out var targetIp))
            {
                logger.LogWarning("[{Scope}] Unknown DC for {Dst}:{Port}, fallback TCP", context.Scope, dst, port);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallbackAsync(stream, dst, port, init, context.Scope);
                return;
            }

            var mediaFlag = isMedia ?? true;
            var dcKey = (dc.Value, mediaFlag);
            if (wsRoutingState.IsBlacklisted(dcKey))
            {
                logger.LogDebug("[{Scope}] DC{Dc} media={IsMedia} WS blacklisted; using TCP fallback", context.Scope, dc, mediaFlag);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallbackAsync(stream, dst, port, init, context.Scope);
                return;
            }

            var wsTimeout = wsRoutingState.IsInFailCooldown(dcKey, DateTimeOffset.UtcNow) ? TimeSpan.FromSeconds(WsFailTimeoutSeconds) : TimeSpan.FromSeconds(WsDefaultTimeoutSeconds);
            var domains = WsDomains(dc.Value, mediaFlag);
            var ws = await TryTakePooledWs(dcKey);
            var sawRedirect = false;
            var allRedirects = true;
            if (ws is null)
            {
                SchedulePoolRefill(dcKey, targetIp, domains, wsTimeout, context.Scope, cancellationToken);
                foreach (var domain in domains)
                {
                    try
                    {
                        logger.LogDebug("[{Scope}] Try WS {Domain} via {TargetIp}", context.Scope, domain, targetIp);
                        ws = await wsFactory.ConnectAsync(targetIp, domain, "/apiws", context.Scope, wsTimeout);
                        wsRoutingState.ClearFailCooldown(dcKey);
                        SchedulePoolRefill(dcKey, targetIp, domains, wsTimeout, context.Scope, cancellationToken);
                        break;
                    }
                    catch (WsHandshakeException ex) when (ex.IsRedirect)
                    {
                        stats.IncWsErrors();
                        sawRedirect = true;
                        logger.LogWarning("[{Scope}] WS redirect for {Domain}: {StatusLine}", context.Scope, domain, ex.StatusLine);
                        ws = null;
                    }
                    catch (TimeoutException ex)
                    {
                        stats.IncWsErrors();
                        allRedirects = false;
                        logger.LogWarning(ex, "[{Scope}] WS connect timeout ({Domain} via {TargetIp})", context.Scope, domain, targetIp);
                        ws = null;
                    }
                    catch (Exception ex)
                    {
                        stats.IncWsErrors();
                        allRedirects = false;
                        logger.LogError(ex, "[{Scope}] WS connect failed ({Domain} via {TargetIp})", context.Scope, domain, targetIp);
                        ws = null;
                    }
                }
            }

            if (ws is null)
            {
                if (sawRedirect && allRedirects)
                {
                    wsRoutingState.AddBlacklist(dcKey);
                    logger.LogWarning("[{Scope}] DC{Dc} media={IsMedia} switched to permanent TCP fallback due to redirects", context.Scope, dc, mediaFlag);
                }
                else
                {
                    wsRoutingState.SetFailCooldown(dcKey, DateTimeOffset.UtcNow.Add(DcFailCooldown));
                }
                logger.LogWarning("[{Scope}] WS unavailable, fallback TCP {Dst}:{Port}", context.Scope, dst, port);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallbackAsync(stream, dst, port, init, context.Scope);
                return;
            }

            stats.IncConnectionsWs();
            logger.LogInformation("[{Scope}] WS bridge started DC{Dc} -> {Dst}:{Port}", context.Scope, dc, dst, port);
            await ws.Send(init);
            await bridgeService.BridgeWsAsync(stream, ws, context.Scope, init);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] Operation canceled", context.Scope);
        }
        catch (TimeoutException)
        {
            logger.LogDebug("[{Scope}] Client read timeout", context.Scope);
        }
        catch (IOException ex) when (IsExpectedDisconnect(ex))
        {
            logger.LogDebug("[{Scope}] Client disconnected during I/O", context.Scope);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] Client socket disconnected", context.Scope);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Unhandled client processing error", context.Scope);
        }
        finally
        {
            logger.LogInformation("[{Scope}] Client disconnected", context.Scope);
        }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        foreach (var kv in dcOpt)
        {
            var dc = kv.Key;
            var targetIp = kv.Value;
            if (string.IsNullOrWhiteSpace(targetIp))
            {
                continue;
            }

            foreach (var media in new[] { false, true })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dcKey = (dc, media);
                var domains = WsDomains(dc, media);
                SchedulePoolRefill(dcKey, targetIp, domains, TimeSpan.FromSeconds(WsDefaultTimeoutSeconds), "warmup", cancellationToken);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Выполняет SOCKS5-согласование и, при необходимости, проверку логина/пароля.
    /// </summary>
    /// <param name="stream">Сетевой поток клиентского подключения.</param>
    /// <param name="credentials">Список допустимых учетных данных SOCKS5.</param>
    /// <param name="cancellationToken">Токен отмены операций чтения/записи.</param>
    /// <returns><see langword="true"/>, если аутентификация успешна; иначе <see langword="false"/>.</returns>
    private async Task<bool> HandleSocks5Auth(NetworkStream stream, List<AuthCredential> credentials, ClientContext context, CancellationToken cancellationToken)
    {
        var greeting = await IoUtil.ReadExact(stream, 2, SocksIoTimeout, cancellationToken);
        if (greeting[0] != 0x05)
        {
            return false;
        }

        var methods = await IoUtil.ReadExact(stream, greeting[1], SocksIoTimeout, cancellationToken);

        if (credentials.Count > 0)
        {
            if (!methods.Contains((byte)0x02))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xff }, cancellationToken);
                return false;
            }
            await stream.WriteAsync(new byte[] { 0x05, 0x02 }, cancellationToken);
            var verUlen = await IoUtil.ReadExact(stream, 2, SocksIoTimeout, cancellationToken);
            if (verUlen[0] != 0x01)
            {
                return false;
            }

            var user = Encoding.UTF8.GetString(await IoUtil.ReadExact(stream, verUlen[1], SocksIoTimeout, cancellationToken));
            var plen = (await IoUtil.ReadExact(stream, 1, SocksIoTimeout, cancellationToken))[0];
            var pass = Encoding.UTF8.GetString(await IoUtil.ReadExact(stream, plen, SocksIoTimeout, cancellationToken));
            var ok = credentials.Any(c => c.Login == user && c.Password == pass);

            logger.LogInformation("[{Scope}] Client user: {user}", context.Scope, user);

            await stream.WriteAsync(ok ? [0x01, 0x00] : new byte[] { 0x01, 0x01 }, cancellationToken);
            return ok;
        }

        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xff }, cancellationToken);
            return false;
        }
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);
        return true;
    }

    /// <summary>
    /// Формирует стандартный SOCKS5-ответ с указанным статусом.
    /// </summary>
    /// <param name="status">Код статуса SOCKS5.</param>
    /// <returns>Бинарный пакет ответа SOCKS5.</returns>
    private static byte[] Socks5Reply(byte status) => [0x05, status, 0x00, 0x01, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Возвращает упорядоченный список доменов WebSocket для заданного DC и типа трафика.
    /// </summary>
    /// <param name="dc">Идентификатор дата-центра Telegram.</param>
    /// <param name="isMedia">Признак media-маршрута.</param>
    /// <returns>Список доменов для попыток подключения в порядке приоритета.</returns>
    private static List<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203)
        {
            dc = 2;
        }

        return isMedia
            ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"]
            : [$"kws{dc}.web.telegram.org", $"kws{dc}-1.web.telegram.org"];
    }

    /// <summary>
    /// Проверяет, относится ли исключение к ожидаемому разрыву клиентского соединения.
    /// </summary>
    /// <param name="ex">Исключение ввода/вывода, возникшее при работе с сокетом.</param>
    /// <returns><see langword="true"/>, если отключение считается штатным.</returns>
    private static bool IsExpectedDisconnect(IOException ex) => ex is EndOfStreamException || (ex.InnerException is SocketException se &&
            se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) || string.Equals(ex.Message, "Connection closed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Определяет, принадлежит ли IPv4-адрес подсетям Telegram.
    /// </summary>
    /// <param name="dst">IPv4-адрес назначения в текстовом виде.</param>
    /// <returns><see langword="true"/>, если адрес относится к известным подсетям Telegram.</returns>
    private static bool IsTelegramIp(string dst)
    {
        if (!IPAddress.TryParse(dst, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = ip.GetAddressBytes();
        var value = ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
        return InRange(value, "185.76.151.0", "185.76.151.255") ||
               InRange(value, "149.154.160.0", "149.154.175.255") ||
               InRange(value, "91.105.192.0", "91.105.193.255") ||
               InRange(value, "91.108.0.0", "91.108.255.255");
    }

    /// <summary>
    /// Проверяет, входит ли числовой IP в указанный диапазон.
    /// </summary>
    /// <param name="value">Числовое представление проверяемого IP.</param>
    /// <param name="from">Нижняя граница диапазона в формате IPv4.</param>
    /// <param name="to">Верхняя граница диапазона в формате IPv4.</param>
    /// <returns><see langword="true"/>, если значение находится в диапазоне включительно.</returns>
    private static bool InRange(uint value, string from, string to)
    {
        var start = ToUint(from);
        var end = ToUint(to);
        return value >= start && value <= end;
    }

    /// <summary>
    /// Преобразует строковый IPv4-адрес в 32-битное целое в сетевом порядке.
    /// </summary>
    /// <param name="ip">IPv4-адрес в строковом виде.</param>
    /// <returns>32-битное беззнаковое значение адреса.</returns>
    private static uint ToUint(string ip)
    {
        var octets = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    /// <summary>
    /// Пытается взять живое WebSocket-соединение из пула для указанного DC.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута.</param>
    /// <returns>Соединение из пула или <see langword="null"/>, если подходящее не найдено.</returns>
    private async Task<IRawWebSocket> TryTakePooledWs((int Dc, bool IsMedia) dcKey)
    {
        var ws = wsRoutingState.TryTakePooledWs(dcKey, DateTimeOffset.UtcNow, WsPoolMaxAge);

        if (ws is null)
        {
            stats.IncPoolMiss();
        }
        else
        {
            stats.IncPoolHit();
        }

        await Task.CompletedTask;
        return ws;
    }

    /// <summary>
    /// Запускает фоновое пополнение пула WebSocket-соединений для указанного DC.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута.</param>
    /// <param name="targetIp">Целевой IP-адрес Telegram DC.</param>
    /// <param name="domains">Список доменов для попыток WebSocket-подключения.</param>
    /// <param name="timeout">Таймаут отдельной попытки подключения.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="cancellationToken">Токен отмены фонового refill-процесса.</param>
    private void SchedulePoolRefill((int Dc, bool IsMedia) dcKey, string targetIp, List<string> domains, TimeSpan timeout, string scope, CancellationToken cancellationToken)
    {
        if (!wsRoutingState.TryBeginRefill(dcKey))
        {
            return;
        }

        BackgroundTaskRunner.RunDetachedSafe(
            async ct =>
            {
                try
                {
                    var ws = await ConnectOneForPool(targetIp, domains, timeout, scope);
                    if (ws is null)
                    {
                        return;
                    }

                    var evicted = wsRoutingState.AddToPool(dcKey, ws, DateTimeOffset.UtcNow, 4);
                    foreach (var stale in evicted)
                    {
                        BackgroundTaskRunner.RunDetachedSafe(
                            async closeCt => await CloseSilently(stale, closeCt),
                            logger,
                            $"[{scope}] close evicted ws",
                            ct);
                    }
                }
                finally
                {
                    wsRoutingState.EndRefill(dcKey);
                }
            },
            logger,
            $"[{scope}] ws pool refill",
            cancellationToken);
    }

    /// <summary>
    /// Закрывает WS-соединение без проброса исключений в вызывающий код.
    /// </summary>
    /// <param name="ws">Соединение для закрытия.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    private static async Task CloseSilently(IRawWebSocket ws, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ws.Close();
        }
        catch
        {
            // best-effort close
        }
    }

    /// <summary>
    /// Создает одно WebSocket-соединение для пула, перебирая список доменов.
    /// </summary>
    /// <param name="targetIp">Целевой IP-адрес Telegram DC.</param>
    /// <param name="domains">Список доменов для подключения.</param>
    /// <param name="timeout">Таймаут попытки подключения.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <returns>Подключенный WebSocket либо <see langword="null"/> при неудаче.</returns>
    private async Task<IRawWebSocket> ConnectOneForPool(string targetIp, List<string> domains, TimeSpan timeout, string scope)
    {
        foreach (var domain in domains)
        {
            try
            {
                return await wsFactory.ConnectAsync(targetIp, domain, "/apiws", scope, timeout);
            }
            catch (WsHandshakeException ex) when (ex.IsRedirect)
            {
                continue;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
