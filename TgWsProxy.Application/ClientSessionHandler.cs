using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application;

internal sealed class ClientSessionHandler(
    Config cfg,
    Dictionary<int, string> dcOpt,
    IRawWebSocketFactory wsFactory,
    ITcpBridgeService bridgeService,
    IMtProtoInspector mtProtoInspector,
    IProxyStats stats,
    ILogger<ClientSessionHandler> logger) : IClientSessionHandler
{
    private const int WsDefaultTimeoutSeconds = 10;
    private const int WsFailTimeoutSeconds = 2;
    private static readonly TimeSpan SocksIoTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WsPoolMaxAge = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DcFailCooldown = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<(int Dc, bool IsMedia), DateTimeOffset> DcFailUntil = [];
    private static readonly HashSet<(int Dc, bool IsMedia)> WsBlacklist = [];
    private static readonly Dictionary<(int Dc, bool IsMedia), List<(IRawWebSocket Ws, DateTimeOffset Created)>> WsPool = [];
    private static readonly HashSet<(int Dc, bool IsMedia)> WsPoolRefilling = [];
    private static readonly object WsStateLock = new();

    private static readonly Dictionary<string, (int Dc, bool IsMedia)> IpToDc = new(StringComparer.Ordinal)
    {
        ["149.154.175.50"] = (1, false), ["149.154.175.51"] = (1, false),
        ["149.154.175.53"] = (1, false), ["149.154.175.54"] = (1, false),
        ["149.154.175.52"] = (1, true),
        ["149.154.167.41"] = (2, false), ["149.154.167.50"] = (2, false),
        ["149.154.167.51"] = (2, false), ["149.154.167.220"] = (2, false),
        ["95.161.76.100"] = (2, false), ["149.154.167.151"] = (2, true),
        ["149.154.167.222"] = (2, true), ["149.154.167.223"] = (2, true),
        ["149.154.162.123"] = (2, true),
        ["149.154.175.100"] = (3, false), ["149.154.175.101"] = (3, false),
        ["149.154.175.102"] = (3, true),
        ["149.154.167.91"] = (4, false), ["149.154.167.92"] = (4, false),
        ["149.154.164.250"] = (4, true), ["149.154.166.120"] = (4, true),
        ["149.154.166.121"] = (4, true), ["149.154.167.118"] = (4, true),
        ["149.154.165.111"] = (4, true),
        ["91.108.56.100"] = (5, false), ["91.108.56.101"] = (5, false),
        ["91.108.56.116"] = (5, false), ["91.108.56.126"] = (5, false),
        ["149.154.171.5"] = (5, false), ["91.108.56.102"] = (5, true),
        ["91.108.56.128"] = (5, true), ["91.108.56.151"] = (5, true),
        ["91.105.192.100"] = (203, false)
    };

    public async Task HandleAsync(TcpClient client, ClientContext context, CancellationToken cancellationToken)
    {
        stats.IncConnectionsTotal();
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        logger.LogInformation("[{Scope}] Client connected", context.Scope);

        try
        {
            if (!await HandleSocks5Auth(stream, cfg.Credentials, cancellationToken))
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
            if (MtProtoUtil.IsHttpTransport(init))
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
                    init = MtProtoUtil.PatchInitDc(init, patchRaw);
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
            if (IsBlacklisted(dcKey))
            {
                logger.LogDebug("[{Scope}] DC{Dc} media={IsMedia} WS blacklisted; using TCP fallback", context.Scope, dc, mediaFlag);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallbackAsync(stream, dst, port, init, context.Scope);
                return;
            }

            var wsTimeout = IsInFailCooldown(dcKey) ? TimeSpan.FromSeconds(WsFailTimeoutSeconds) : TimeSpan.FromSeconds(WsDefaultTimeoutSeconds);
            var domains = WsDomains(dc.Value, mediaFlag);
            IRawWebSocket? ws = await TryTakePooledWs(dcKey);
            var sawRedirect = false;
            var allRedirects = true;
            if (ws is null)
            {
                SchedulePoolRefill(dcKey, targetIp, domains, wsTimeout, context.Scope);
                foreach (var domain in domains)
                {
                    try
                    {
                        logger.LogDebug("[{Scope}] Try WS {Domain} via {TargetIp}", context.Scope, domain, targetIp);
                        ws = await wsFactory.ConnectAsync(targetIp, domain, "/apiws", context.Scope, wsTimeout);
                        ClearFailCooldown(dcKey);
                        SchedulePoolRefill(dcKey, targetIp, domains, wsTimeout, context.Scope);
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
                    AddBlacklist(dcKey);
                    logger.LogWarning("[{Scope}] DC{Dc} media={IsMedia} switched to permanent TCP fallback due to redirects", context.Scope, dc, mediaFlag);
                }
                else
                {
                    SetFailCooldown(dcKey);
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
                SchedulePoolRefill(dcKey, targetIp, domains, TimeSpan.FromSeconds(WsDefaultTimeoutSeconds), "warmup");
            }
        }

        await Task.CompletedTask;
    }

    private static async Task<bool> HandleSocks5Auth(NetworkStream stream, List<AuthCredential> credentials, CancellationToken cancellationToken)
    {
        var greeting = await IoUtil.ReadExact(stream, 2, SocksIoTimeout, cancellationToken);
        if (greeting[0] != 0x05) return false;
        var methods = await IoUtil.ReadExact(stream, greeting[1], SocksIoTimeout, cancellationToken);

        if (credentials.Count > 0)
        {
            if (!methods.Contains((byte)0x02))
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xff });
                return false;
            }
            await stream.WriteAsync(new byte[] { 0x05, 0x02 });
            var verUlen = await IoUtil.ReadExact(stream, 2, SocksIoTimeout, cancellationToken);
            if (verUlen[0] != 0x01) return false;
            var user = Encoding.UTF8.GetString(await IoUtil.ReadExact(stream, verUlen[1], SocksIoTimeout, cancellationToken));
            var plen = (await IoUtil.ReadExact(stream, 1, SocksIoTimeout, cancellationToken))[0];
            var pass = Encoding.UTF8.GetString(await IoUtil.ReadExact(stream, plen, SocksIoTimeout, cancellationToken));
            var ok = credentials.Any(c => c.Login == user && c.Password == pass);
            await stream.WriteAsync(ok ? new byte[] { 0x01, 0x00 } : new byte[] { 0x01, 0x01 });
            return ok;
        }

        if (!methods.Contains((byte)0x00))
        {
            await stream.WriteAsync(new byte[] { 0x05, 0xff });
            return false;
        }
        await stream.WriteAsync(new byte[] { 0x05, 0x00 });
        return true;
    }

    private static byte[] Socks5Reply(byte status) => [0x05, status, 0x00, 0x01, 0, 0, 0, 0, 0, 0];

    private static List<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203) dc = 2;
        if (isMedia) return [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"];
        return [$"kws{dc}.web.telegram.org", $"kws{dc}-1.web.telegram.org"];
    }

    private static bool IsExpectedDisconnect(IOException ex)
    {
        if (ex is EndOfStreamException) return true;
        if (ex.InnerException is SocketException se &&
            se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            return true;
        }

        return string.Equals(ex.Message, "Connection closed", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool InRange(uint value, string from, string to)
    {
        var start = ToUint(from);
        var end = ToUint(to);
        return value >= start && value <= end;
    }

    private static uint ToUint(string ip)
    {
        var octets = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    private static bool IsInFailCooldown((int Dc, bool IsMedia) dcKey)
    {
        lock (WsStateLock)
        {
            return DcFailUntil.TryGetValue(dcKey, out var until) && until > DateTimeOffset.UtcNow;
        }
    }

    private static void SetFailCooldown((int Dc, bool IsMedia) dcKey)
    {
        lock (WsStateLock)
        {
            DcFailUntil[dcKey] = DateTimeOffset.UtcNow.Add(DcFailCooldown);
        }
    }

    private static void ClearFailCooldown((int Dc, bool IsMedia) dcKey)
    {
        lock (WsStateLock)
        {
            DcFailUntil.Remove(dcKey);
        }
    }

    private static bool IsBlacklisted((int Dc, bool IsMedia) dcKey)
    {
        lock (WsStateLock)
        {
            return WsBlacklist.Contains(dcKey);
        }
    }

    private static void AddBlacklist((int Dc, bool IsMedia) dcKey)
    {
        lock (WsStateLock)
        {
            WsBlacklist.Add(dcKey);
        }
    }

    private async Task<IRawWebSocket?> TryTakePooledWs((int Dc, bool IsMedia) dcKey)
    {
        (IRawWebSocket Ws, DateTimeOffset Created)? item = null;
        lock (WsStateLock)
        {
            if (WsPool.TryGetValue(dcKey, out var bucket))
            {
                while (bucket.Count > 0)
                {
                    var candidate = bucket[0];
                    bucket.RemoveAt(0);
                    if (DateTimeOffset.UtcNow - candidate.Created <= WsPoolMaxAge)
                    {
                        item = candidate;
                        break;
                    }
                }
            }
        }

        if (item is null) stats.IncPoolMiss();
        else stats.IncPoolHit();
        await Task.CompletedTask;
        return item?.Ws;
    }

    private void SchedulePoolRefill((int Dc, bool IsMedia) dcKey, string targetIp, List<string> domains, TimeSpan timeout, string scope)
    {
        lock (WsStateLock)
        {
            if (!WsPoolRefilling.Add(dcKey)) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var ws = await ConnectOneForPool(targetIp, domains, timeout, scope);
                if (ws is null) return;
                lock (WsStateLock)
                {
                    if (!WsPool.TryGetValue(dcKey, out var bucket))
                    {
                        bucket = [];
                        WsPool[dcKey] = bucket;
                    }
                    bucket.Add((ws, DateTimeOffset.UtcNow));
                    while (bucket.Count > 4)
                    {
                        var old = bucket[0];
                        bucket.RemoveAt(0);
                        _ = Task.Run(async () => { try { await old.Ws.Close(); } catch { } });
                    }
                }
            }
            catch
            {
                // best-effort refill
            }
            finally
            {
                lock (WsStateLock)
                {
                    WsPoolRefilling.Remove(dcKey);
                }
            }
        });
    }

    private async Task<IRawWebSocket?> ConnectOneForPool(string targetIp, List<string> domains, TimeSpan timeout, string scope)
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
