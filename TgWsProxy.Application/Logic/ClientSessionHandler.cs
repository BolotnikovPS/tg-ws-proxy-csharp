#nullable enable

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic.FakeTls;
using TgWsProxy.Application.Logic.Helpers;
using TgWsProxy.Application.Logic.Models;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Обрабатывает одно клиентское подключение: читает MTProto init (при необходимости через
/// PROXY protocol и Fake TLS), определяет DC и протокол, затем уводит трафик в пул WebSocket
/// или в цепочку fallback (порт upstream <c>_handle_client</c>).
/// </summary>
internal sealed class ClientSessionHandler(
    Config cfg,
    ClientInitReader initReader,
    MaskingProxy maskingProxy,
    ITcpBridgeService bridgeService,
    IMtProtoInspector mtProtoInspector,
    IWsPool wsPool,
    IProxyStats stats,
    ILogger<ClientSessionHandler> logger) : IClientSessionHandler
{
    /// <summary>Ключ secret в байтах (hex из конфигурации).</summary>
    private readonly byte[] secret = Convert.FromHexString(cfg.Secret);

    /// <inheritdoc />
    public async Task Handle(Socket client, ClientContext context, CancellationToken cancellationToken)
    {
        stats.IncConnectionsTotal();
        stats.IncConnectionsActive();
        using var _ = client;

        var stopwatch = Stopwatch.StartNew();
        CountingStream? countingStream = null;
        int? sessionDc = null;
        var sessionMedia = false;
        var sessionRoute = "unknown";
        long sessionUp = 0;
        long sessionDown = 0;

        try
        {
            client.NoDelay = true;
            var sockBuf = Math.Max(4, cfg.SocketBufferKb) * 1024;
            client.ReceiveBufferSize = sockBuf;
            client.SendBufferSize = sockBuf;
            await using var stream = new NetworkStream(client);

            logger.LogInformation("[{Scope}] client connected {Peer}", context.Scope, context.Peer);

            var init = await initReader.ReadAsync(stream, context.Scope, secret, cancellationToken)
                .ConfigureAwait(false);
            var label = init.Label;

            switch (init.Status)
            {
                case ClientInitStatus.Closed:
                    sessionRoute = "closed";
                    return;

                case ClientInitStatus.HttpRedirect:
                    sessionRoute = "redirect";
                    await SendMaskingRedirectAsync(stream, label, cancellationToken).ConfigureAwait(false);
                    return;

                case ClientInitStatus.Masking:
                    sessionRoute = "masking";
                    await maskingProxy
                        .RelayAsync(stream, init.FirstBytes ?? [], cfg.FakeTlsDomain, label, cancellationToken)
                        .ConfigureAwait(false);
                    return;
            }

            var clientStream = init.ClientStream ?? stream;
            countingStream = new CountingStream(clientStream);
            var handshakeBytes = init.Init!;

            var handshake = mtProtoInspector.TryHandshake(handshakeBytes, secret);
            if (handshake is null)
            {
                stats.IncConnectionsBad();
                logger.LogWarning("[{Scope}] bad handshake (wrong secret or proto)", label);
                sessionRoute = "bad";
                return;
            }

            var hs = handshake.Value;
            var isTestDc = cfg.ForceTestDc || hs.Dc >= 10000;
            var dc = hs.Dc;
            if (dc >= 10000)
            {
                logger.LogInformation("[{Scope}] test DC{Dc} -> DC{Dc}", label, dc, dc - 10000);
                dc -= 10000;
            }

            var isMedia = hs.IsMedia;
            sessionDc = dc;
            sessionMedia = isMedia;
            var dcIdx = (short)(isMedia ? -dc : dc);
            var relayInit = mtProtoInspector.GenerateRelayInit(hs.ProtoTag, dcIdx);
            var mediaTag = isMedia ? " media" : "";

            logger.LogDebug(
                "[{Scope}] handshake ok: DC{Dc}{MediaTag} proto=0x{Proto:X8} dcIdx={DcIdx} test={Test}",
                label, dc, mediaTag, BinaryPrimitives.ReadUInt32LittleEndian(hs.ProtoTag), dcIdx, isTestDc);

            using var ctx = BuildCryptoContext(handshakeBytes, relayInit);

            // Splitter нужен и для fallback-маршрутов: там пакеты тоже режутся по одному.
            var splitter = new MsgSplitter(hs.ProtoInt);
            var ws = wsPool.TryGet(dc, isMedia, isTestDc);

            if (ws is null)
            {
                logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} WS pool unavailable -> fallback", label, dc, mediaTag);
                sessionRoute = "fallback";
                var request = new FallbackRequest(
                    countingStream, relayInit, label, dc, isTestDc, isMedia, hs.ProtoTag, ctx, splitter);

                if (await bridgeService.DoFallback(request, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} fallback closed", label, dc, mediaTag);
                }
                else
                {
                    logger.LogWarning("[{Scope}] DC{Dc}{MediaTag} no fallback available", label, dc, mediaTag);
                }

                return;
            }

            stats.IncConnectionsWs();
            sessionRoute = "ws";
            logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} -> WS bridge", label, dc, mediaTag);
            try
            {
                await ws.Send(relayInit, cancellationToken).ConfigureAwait(false);
                await bridgeService
                    .BridgeWsReencrypt(countingStream, ws, label, ctx, dc, isMedia, splitter, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await CloseQuietlyAsync(ws, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Unhandled client processing error", context.Scope);
        }
        finally
        {
            stats.DecConnectionsActive();
            stopwatch.Stop();
            if (countingStream is not null)
            {
                sessionUp = countingStream.BytesRead;
                sessionDown = countingStream.BytesWritten;
            }

            logger.LogInformation(
                "[{Scope}] session ended peer={Peer} dc={Dc} media={Media} route={Route} up={Up}B down={Down}B duration={Duration:F1}s",
                context.Scope,
                context.Peer,
                sessionDc?.ToString() ?? "-",
                sessionMedia,
                sessionRoute,
                sessionUp,
                sessionDown,
                stopwatch.Elapsed.TotalSeconds);
        }
    }

    /// <inheritdoc />
    public void Warmup(CancellationToken cancellationToken)
        => wsPool.Warmup();

    /// <summary>
    /// Отвечает HTTP 301 на домен маскировки, когда клиент пришёл без TLS
    /// (порт upstream <c>_read_client_init</c>).
    /// </summary>
    private async Task SendMaskingRedirectAsync(Stream stream, string label, CancellationToken cancellationToken)
    {
        var redirect = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 301 Moved Permanently\r\n" +
            $"Location: https://{cfg.FakeTlsDomain}/\r\n" +
            $"Content-Length: 0\r\n" +
            $"Connection: close\r\n\r\n");

        try
        {
            await stream.WriteAsync(redirect, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            logger.LogDebug("[{Scope}] masking redirect sent", label);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[{Scope}] masking redirect failed", label);
        }
    }

    /// <summary>
    /// Собирает четыре AES-CTR потока: два для клиента и два для Telegram
    /// (порт upstream <c>_build_crypto_ctx</c>).
    /// </summary>
    /// <param name="init">Init-пакет клиента (obfs2 handshake).</param>
    /// <param name="relayInit">Сгенерированный relay init.</param>
    private CryptoCtx BuildCryptoContext(byte[] init, byte[] relayInit)
    {
        // dec_prekey_iv = init[8:56]; dec_key = SHA256(dec_prekey + secret)
        var clientDecPrekeyIv = init.AsSpan(Protocol.SkipLen, Protocol.PreKeyLen + Protocol.IvLen).ToArray();
        var clientDecKey = DeriveKey(clientDecPrekeyIv.AsSpan(0, Protocol.PreKeyLen));
        var clientDecIv = clientDecPrekeyIv.AsSpan(Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        // enc_prekey_iv = dec_prekey_iv[::-1]
        var clientEncPrekeyIv = clientDecPrekeyIv.ToArray();
        Array.Reverse(clientEncPrekeyIv);
        var clientEncKey = DeriveKey(clientEncPrekeyIv.AsSpan(0, Protocol.PreKeyLen));
        var clientEncIv = clientEncPrekeyIv.AsSpan(Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        var cltDecryptor = new IncrementalCipher(clientDecKey, clientDecIv);
        var cltEncryptor = new IncrementalCipher(clientEncKey, clientEncIv);

        // Пропускаем 64 байта init без выделения памяти.
        cltDecryptor.Skip(Protocol.HandshakeLen);

        // Relay: enc — как есть, dec — реверс prekey_iv.
        var relayEncKey = relayInit.AsSpan(Protocol.SkipLen, Protocol.KeyLen).ToArray();
        var relayEncIv = relayInit.AsSpan(Protocol.SkipLen + Protocol.KeyLen, Protocol.IvLen).ToArray();

        var relayDecPrekeyIv = relayInit.AsSpan(Protocol.SkipLen, Protocol.PreKeyLen + Protocol.IvLen).ToArray();
        Array.Reverse(relayDecPrekeyIv);
        var relayDecKey = relayDecPrekeyIv.AsSpan(0, Protocol.PreKeyLen).ToArray();
        var relayDecIv = relayDecPrekeyIv.AsSpan(Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        var tgEncryptor = new IncrementalCipher(relayEncKey, relayEncIv);
        var tgDecryptor = new IncrementalCipher(relayDecKey, relayDecIv);

        tgEncryptor.Skip(Protocol.HandshakeLen);

        return new CryptoCtx(cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor);
    }

    /// <summary>
    /// Считает ключ как SHA256(prekey + secret).
    /// </summary>
    private byte[] DeriveKey(ReadOnlySpan<byte> prekey)
    {
        var buffer = new byte[prekey.Length + secret.Length];
        prekey.CopyTo(buffer);
        secret.AsSpan().CopyTo(buffer.AsSpan(prekey.Length));
        return System.Security.Cryptography.SHA256.HashData(buffer);
    }

    /// <summary>
    /// Проверяет, относится ли исключение к ожидаемому разрыву клиентского соединения.
    /// </summary>
    private static bool IsExpectedDisconnect(IOException ex) =>
        ex is EndOfStreamException ||
        (ex.InnerException is SocketException se &&
         se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) ||
        string.Equals(ex.Message, "Connection closed", StringComparison.OrdinalIgnoreCase);

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
}
