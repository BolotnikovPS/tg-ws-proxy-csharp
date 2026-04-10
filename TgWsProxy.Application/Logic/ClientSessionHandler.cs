#nullable enable

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic.Helpers;
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
    private const int WsFailTimeoutSeconds = 2;
    private static readonly TimeSpan InitReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WsPoolMaxAge = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DcFailCooldown = TimeSpan.FromSeconds(30);

    // Telegram protocol constants
    private static readonly byte[] ProtoTagAbridged = [0xEF, 0xEF, 0xEF, 0xEF];
    private static readonly byte[] ProtoTagIntermediate = [0xEE, 0xEE, 0xEE, 0xEE];
    private static readonly byte[] ProtoTagSecure = [0xDD, 0xDD, 0xDD, 0xDD];
    private static readonly byte[] Zero64 = new byte[64];

    private static readonly FrozenDictionary<int, string> DC_DEFAULT_IPS = new Dictionary<int, string>()
    {
        { 1, "149.154.175.50" },
        { 2, "149.154.167.51" },
        { 3, "149.154.175.100" },
        { 4, "149.154.167.91" },
        { 5, "149.154.171.5" },
        { 203, "91.105.192.100" }
    }.ToFrozenDictionary();

    private readonly byte[][] _secretBytesList = [.. cfg.Secrets.Select(s => Convert.FromHexString(s))];

    public async Task Handle(Socket client, ClientContext context, CancellationToken cancellationToken)
    {
        stats.IncConnectionsTotal();
        using var _ = client;
        client.NoDelay = true;
        var sockBuf = Math.Max(4, cfg.SocketBufferKb) * 1024;
        client.ReceiveBufferSize = sockBuf;
        client.SendBufferSize = sockBuf;
        await using var stream = new NetworkStream(client);

        logger.LogInformation("[{Scope}] Client connected", context.Scope);

        var init = Array.Empty<byte>();
        var dc = 2; // default DC for fallback

        try
        {
            // Direct MTProto handshake (no SOCKS5)
            init = await stream.ReadExact(64, InitReadTimeout, cancellationToken);
            if (mtProtoInspector.IsHttpTransport(init))
            {
                stats.IncConnectionsHttpRejected();
                logger.LogWarning("[{Scope}] HTTP transport detected; closing", context.Scope);
                return;
            }

            // Try each secret to find the right one for this client
            byte[]? matchedSecretBytes = null;
            int? matchedDc = null;
            bool? matchedIsMedia = null;
            byte[]? matchedProtoTag = null;

            foreach (var secretBytes in _secretBytesList)
            {
                var (dcVal, isMediaVal) = mtProtoInspector.DcFromInit(init, secretBytes);
                if (dcVal is not null)
                {
                    matchedSecretBytes = secretBytes;
                    matchedDc = dcVal;
                    matchedIsMedia = isMediaVal;
                    matchedProtoTag = GetProtoTag(init, secretBytes);
                    break;
                }
            }

            if (matchedSecretBytes is null || matchedDc is null)
            {
                logger.LogWarning("[{Scope}] Unknown DC with any secret, fallback TCP", context.Scope);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallback(stream, DC_DEFAULT_IPS[2], 443, init, context.Scope, cancellationToken);
                return;
            }

            dc = matchedDc.Value;
            var isMedia = matchedIsMedia;
            var protoTag = matchedProtoTag;

            // DC 203 handling: map to DC 2 for WS domains
            var wsDc = dc == 203 ? 2 : dc;
            var mediaFlag = isMedia ?? true;
            var dcKey = (wsDc, mediaFlag);
            var dcIdx = (short)(mediaFlag ? -dc : dc);

            // Generate relay_init
            var relayInit = mtProtoInspector.GenerateRelayInit(protoTag, dcIdx);
            logger.LogDebug("[{Scope}] relay_init: proto=0x{Proto:X8} dcIdx={DcIdx} rnd_tail={Tail}",
                context.Scope,
                BinaryPrimitives.ReadUInt32LittleEndian(protoTag),
                dcIdx,
                Convert.ToHexString(relayInit.AsSpan(56, 8)));

            // Client-side keys dec_prekey_iv = init[8:56],
            // dec_key = SHA256(dec_prekey + secret)
            var clientDecPrekeyIv = init.AsSpan(8, 48).ToArray();
            var clientDecPrekey = clientDecPrekeyIv.AsSpan(0, 32).ToArray();
            var clientDecIv = clientDecPrekeyIv.AsSpan(32, 16).ToArray();
            var clientDecKey = SHA256.HashData([.. clientDecPrekey, .. matchedSecretBytes]);

            // enc_prekey_iv = dec_prekey_iv[::-1], enc_key = SHA256(enc_prekey + secret)
            var clientEncPrekeyIv = clientDecPrekeyIv.ToArray();
            Array.Reverse(clientEncPrekeyIv);
            var clientEncPrekey = clientEncPrekeyIv.AsSpan(0, 32).ToArray();
            var clientEncIv = clientEncPrekeyIv.AsSpan(32, 16).ToArray();
            var clientEncKey = SHA256.HashData([.. clientEncPrekey, .. matchedSecretBytes]);

            using var cltDecryptor = new IncrementalCipher(clientDecKey, clientDecIv);
            using var cltEncryptor = new IncrementalCipher(clientEncKey, clientEncIv);

            // Fast-forward clt_decryptor past the 64-byte init
            cltDecryptor.Update(Zero64);

            // Relay side keys (from relay_init)
            var relayEncKey = relayInit.AsSpan(8, 32).ToArray();
            var relayEncIv = relayInit.AsSpan(40, 16).ToArray();

            // tg_encryptor uses relay_enc_key/iv (to encrypt data to Telegram) tg_decryptor uses
            // relay_dec_key/iv (to decrypt data from Telegram) relay_dec_prekey_iv = relay_init[8:56][::-1]
            var relayDecPrekeyIv = relayInit.AsSpan(8, 48).ToArray();
            Array.Reverse(relayDecPrekeyIv);
            var relayDecKey = relayDecPrekeyIv.AsSpan(0, 32).ToArray();
            var relayDecIv = relayDecPrekeyIv.AsSpan(32, 16).ToArray();

            using var tgEncryptor = new IncrementalCipher(relayEncKey, relayEncIv);
            using var tgDecryptor = new IncrementalCipher(relayDecKey, relayDecIv);

            // Fast-forward tg_encryptor past the 64-byte init
            tgEncryptor.Update(Zero64);

            if (wsRoutingState.IsBlacklisted(dcKey))
            {
                logger.LogDebug("[{Scope}] DC{Dc} media={IsMedia} WS blacklisted; using TCP fallback", context.Scope, dc, mediaFlag);
                stats.IncConnectionsTcpFallback();
                await DoFallback(
                    stream, DC_DEFAULT_IPS[dc], relayInit, context.Scope, dc, mediaFlag,
                    cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor, cancellationToken);
                return;
            }

            var wsTimeout = wsRoutingState.IsInFailCooldown(dcKey, DateTimeOffset.UtcNow)
                ? TimeSpan.FromSeconds(WsFailTimeoutSeconds)
                : TimeSpan.FromSeconds(cfg.WsConnectTimeoutSeconds);
            var domains = WsDomains(wsDc, mediaFlag);

            // Try to get target IP from config or defaults
            var targetIp = dcOpt.TryGetValue(dc, out var ip)
                ? ip
                : DC_DEFAULT_IPS.GetValueOrDefault(dc);

            if (targetIp is null)
            {
                logger.LogWarning("[{Scope}] No target IP for DC{Dc}, fallback TCP", context.Scope, dc);
                stats.IncConnectionsTcpFallback();
                await DoFallback(
                    stream, DC_DEFAULT_IPS[dc], relayInit, context.Scope, dc, mediaFlag,
                    cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor, cancellationToken);
                return;
            }

            var ws = TryTakePooledWs(dcKey);
            var sawRedirect = false;
            var allRedirects = true;
            if (ws is null)
            {
                SchedulePoolRefill(dcKey, targetIp, domains, wsTimeout, context.Scope, cancellationToken);
                for (var di = 0; di < domains.Count; di++)
                {
                    var domain = domains[di];
                    var moreDomains = di < domains.Count - 1;
                    try
                    {
                        logger.LogDebug("[{Scope}] Try WS {Domain} via {TargetIp}", context.Scope, domain, targetIp);
                        ws = await wsFactory.Connect(targetIp, domain, "/apiws", context.Scope, wsTimeout);
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
                        if (moreDomains)
                        {
                            logger.LogDebug(ex, "[{Scope}] WS connect timeout ({Domain} via {TargetIp}), пробуем следующий хост", context.Scope, domain, targetIp);
                        }
                        else
                        {
                            logger.LogWarning(ex, "[{Scope}] WS connect timeout ({Domain} via {TargetIp})", context.Scope, domain, targetIp);
                        }
                        ws = null;
                    }
                    catch (Exception ex)
                    {
                        stats.IncWsErrors();
                        allRedirects = false;
                        if (moreDomains && ex is IOException)
                        {
                            logger.LogDebug(ex, "[{Scope}] WS connect failed ({Domain} via {TargetIp}), пробуем следующий хост", context.Scope, domain, targetIp);
                        }
                        else
                        {
                            logger.LogError(ex, "[{Scope}] WS connect failed ({Domain} via {TargetIp})", context.Scope, domain, targetIp);
                        }
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
                logger.LogWarning("[{Scope}] WS unavailable, fallback TCP", context.Scope);
                stats.IncConnectionsTcpFallback();
                await bridgeService.TcpFallback(stream, DC_DEFAULT_IPS[dc], 443, init, context.Scope, cancellationToken);
                return;
            }

            stats.IncConnectionsWs();
            logger.LogInformation("[{Scope}] WS bridge DC{Dc} media={IsMedia}", context.Scope, dc, mediaFlag);
            try
            {
                await ws.Send(relayInit, cancellationToken);
                await bridgeService.BridgeWsReencrypt(
                    stream, ws, context.Scope, relayInit,
                    cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor, cancellationToken);
            }
            finally
            {
                await ws.Close(cancellationToken);
            }
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
        catch (WsFrameTooLargeException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            logger.LogWarning(
                "[{Scope}] WS frame too large ({Len} > {Max}); fallback TCP",
                context.Scope,
                ex.FramePayloadLen,
                ex.MaxFramePayloadLen);

            stats.IncConnectionsTcpFallback();
            await bridgeService.TcpFallback(stream, DC_DEFAULT_IPS[dc], 443, init, context.Scope, cancellationToken);
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

    public void Warmup(CancellationToken cancellationToken)
    {
        if (cfg.WsPoolSize <= 0)
        {
            return;
        }

        foreach (var kv in dcOpt)
        {
            var dc = kv.Key;
            var targetIp = kv.Value;
            if (string.IsNullOrWhiteSpace(targetIp))
            {
                continue;
            }

            // DC 203 -> DC 2 for WS domains
            var wsDc = dc == 203 ? 2 : dc;

            foreach (var media in new[] { false, true })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dcKey = (wsDc, media);
                var domains = WsDomains(wsDc, media);
                SchedulePoolRefill(dcKey, targetIp, domains, TimeSpan.FromSeconds(cfg.WsConnectTimeoutSeconds), "warmup", cancellationToken);
            }
        }
    }

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
    /// Пытается взять живое WebSocket-соединение из пула для указанного DC.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута.</param>
    /// <returns>Соединение из пула или <see langword="null"/>, если подходящее не найдено.</returns>
    private IRawWebSocket? TryTakePooledWs((int Dc, bool IsMedia) dcKey)
    {
        var ws = wsRoutingState.TryTakePooledWs(dcKey, DateTimeOffset.UtcNow, WsPoolMaxAge);

        if (ws is null)
        {
            stats.IncPoolMiss();

            return null;
        }

        stats.IncPoolHit();
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
        if (cfg.WsPoolSize <= 0)
        {
            return;
        }

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

                    var evicted = wsRoutingState.AddToPool(dcKey, ws, DateTimeOffset.UtcNow, cfg.WsPoolSize);
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

        await ws.Close(cancellationToken);
    }

    /// <summary>
    /// Создает одно WebSocket-соединение для пула, перебирая список доменов.
    /// </summary>
    /// <param name="targetIp">Целевой IP-адрес Telegram DC.</param>
    /// <param name="domains">Список доменов для подключения.</param>
    /// <param name="timeout">Таймаут попытки подключения.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <returns>Подключенный WebSocket либо <see langword="null"/> при неудаче.</returns>
    private async Task<IRawWebSocket?> ConnectOneForPool(string targetIp, List<string> domains, TimeSpan timeout, string scope)
    {
        foreach (var domain in domains)
        {
            try
            {
                return await wsFactory.Connect(targetIp, domain, "/apiws", scope, timeout);
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

    /// <summary>
    /// Определяет тег протокола из init пакета.
    /// </summary>
    private static byte[]? GetProtoTag(byte[] init, byte[] secretBytes)
    {
        if (init.Length < 64)
        {
            return null;
        }

        try
        {
            var decPrekeyAndIv = init.AsSpan(8, 48).ToArray();
            var decPrekey = decPrekeyAndIv.AsSpan(0, 32).ToArray();
            var decIv = decPrekeyAndIv.AsSpan(32, 16).ToArray();
            var decKey = SHA256.HashData([.. decPrekey, .. secretBytes]);

            var counter = BuildCounterAt(decIv, 56L / 16);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = decKey;
            using var enc = aes.CreateEncryptor();

            var keystream = new byte[16];
            enc.TransformBlock(counter, 0, 16, keystream, 0);

            var tag = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                tag[i] = (byte)(init[56 + i] ^ keystream[8 + i]);
            }

            if (tag.AsSpan().SequenceEqual(ProtoTagAbridged) ||
                tag.AsSpan().SequenceEqual(ProtoTagIntermediate) ||
                tag.AsSpan().SequenceEqual(ProtoTagSecure))
            {
                return tag;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static byte[] BuildCounterAt(byte[] iv, long blockIndex)
    {
        var counter = iv.ToArray();
        var carry = (ulong)blockIndex;
        for (var b = 15; b >= 0 && carry != 0; b--)
        {
            var sum = (ushort)(counter[b] + (carry & 0xFF));
            counter[b] = (byte)sum;
            carry = (carry >> 8) + (uint)(sum >> 8);
        }
        return counter;
    }

    /// <summary>
    /// Выполняет fallback с учетом CF Proxy и TCP fallback конфигурации.
    /// </summary>
    private async Task DoFallback(
        NetworkStream stream,
        string fallbackDst,
        byte[] relayInit,
        string scope,
        int dc,
        bool isMedia,
        IncrementalCipher cltDecryptor,
        IncrementalCipher cltEncryptor,
        IncrementalCipher tgEncryptor,
        IncrementalCipher tgDecryptor,
        CancellationToken cancellationToken)
    {
        var useCf = cfg.CfProxyEnabled;
        var cfFirst = cfg.CfProxyPriority;

        // Build fallback order
        var methods = new List<string>();
        if (useCf && cfFirst)
        {
            methods.Add("cf");
            if (fallbackDst is not null)
            {
                methods.Add("tcp");
            }
        }
        else
        {
            if (fallbackDst is not null)
            {
                methods.Add("tcp");
            }

            if (useCf)
            {
                methods.Add("cf");
            }
        }

        foreach (var method in methods)
        {
            if (method == "cf")
            {
                try
                {
                    await bridgeService.CfProxyFallback(
                        stream, scope, relayInit, dc, isMedia,
                        cfg.CfProxyDomain,
                        cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor,
                        cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{Scope}] CF proxy failed for DC{Dc}", scope, dc);
                }
            }
            if (method == "tcp" && fallbackDst is not null)
            {
                logger.LogInformation("[{Scope}] DC{Dc} -> TCP fallback to {FallbackDst}:443", scope, dc, fallbackDst);
                try
                {
                    await bridgeService.TcpFallbackReencrypt(
                        stream, fallbackDst, 443, relayInit, scope,
                        cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor,
                        cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{Scope}] TCP fallback failed to {FallbackDst}", scope, fallbackDst);
                }
            }
        }

        logger.LogWarning("[{Scope}] DC{Dc} no fallback available", scope, dc);
    }
}
