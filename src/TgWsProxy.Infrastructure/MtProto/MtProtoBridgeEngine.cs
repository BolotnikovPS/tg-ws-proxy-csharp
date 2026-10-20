using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Ядро «после handshake», общее для TCP-режима и web-proxy: по уже принятому init-пакету и
/// выбранному токену строит relay init, криптоконтекст и разборщик MTProto, затем уводит трафик
/// в WS-пул или цепочку fallback. Транспортно-независим: клиент задаётся произвольным
/// <see cref="Stream"/> (TCP — <c>NetworkStream</c>, web-proxy — <c>RelayStream</c>).
/// </summary>
internal sealed class MtProtoBridgeEngine(
    Config cfg,
    IMtProtoInspector mtProtoInspector,
    IWsPool wsPool,
    ITcpBridgeService bridgeService,
    IProxyStats stats,
    IPeerStatsRegistry peerStats,
    ILogger<MtProtoBridgeEngine> logger)
{
    /// <summary>
    /// Выполняет мост клиентского MTProto-потока в Telegram и возвращает маршрут
    /// (<c>"ws"</c> либо <c>"fallback"</c>) для итогового лога сессии.
    /// </summary>
    /// <param name="clientStream">Клиентский поток, из которого уже прочитан MTProto init.</param>
    /// <param name="init">Init-пакет клиента (obfs2 handshake, 64 байта).</param>
    /// <param name="hs">Разобранный handshake (DC, media, proto tag).</param>
    /// <param name="token">Токен, секретом которого прошёл handshake.</param>
    /// <param name="scope">Идентификатор сессии для логов.</param>
    /// <param name="peer">Адрес клиента без порта — ключ статистики по IP (может быть <see langword="null"/>).</param>
    /// <param name="ct">Токен отмены сессии.</param>
    public async Task<string> BridgeAsync(
        Stream clientStream,
        byte[] init,
        MtProtoHandshake hs,
        ProxyTokenEntry token,
        string scope,
        IPAddress? peer,
        CancellationToken ct)
    {
        var isTestDc = cfg.ForceTestDc || hs.Dc >= 10000;
        var dc = hs.Dc;
        if (dc >= 10000)
        {
            // F-8.5: тестовые DC, как и в прежних версиях, остаются включёнными (forceTestDc);
            // режим проверки специально существует для тестовых серверов Telegram.
            logger.LogInformation("[{Scope}] test DC{Dc} -> DC{Dc}", scope, dc, dc - 10000);
            dc -= 10000;
        }

        var isMedia = hs.IsMedia;
        var dcIdx = (short)(isMedia ? -dc : dc);
        var relayInit = mtProtoInspector.GenerateRelayInit(hs.ProtoTag, dcIdx);
        var mediaTag = isMedia ? " media" : "";

        logger.LogDebug(
            "[{Scope}] handshake ok: token={TokenName} DC{Dc}{MediaTag} proto=0x{Proto:X8} dcIdx={DcIdx} test={Test}",
            scope, token.Name, dc, mediaTag, BinaryPrimitives.ReadUInt32LittleEndian(hs.ProtoTag), dcIdx, isTestDc);

        using var ctx = BuildCryptoContext(init, relayInit, token.Secret);

        // Splitter нужен и для fallback-маршрутов: там пакеты тоже режутся по одному.
        var splitter = new MsgSplitter(hs.ProtoInt);
        var tokenId = token.Id;
        var ws = wsPool.TryGet(dc, isMedia, isTestDc);

        if (ws is null)
        {
            peerStats.RecordPoolMiss(peer, tokenId);
            logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} WS pool unavailable -> fallback", scope, dc, mediaTag);
            var request = new FallbackRequest(
                clientStream, relayInit, scope, dc, isTestDc, isMedia, hs.ProtoTag, ctx, splitter,
                peer, tokenId);

            if (await bridgeService.DoFallback(request, ct).ConfigureAwait(false))
            {
                logger.LogInformation("[{Scope}] DC{Dc}{MediaTag} fallback closed", scope, dc, mediaTag);
            }
            else
            {
                logger.LogWarning("[{Scope}] DC{Dc}{MediaTag} no fallback available", scope, dc, mediaTag);
            }

            return "fallback";
        }

        stats.IncConnectionsWs();
        peerStats.RecordPoolHit(peer, tokenId);
        logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} -> WS bridge", scope, dc, mediaTag);
        try
        {
            await ws.Send(relayInit, ct).ConfigureAwait(false);
            await bridgeService
                .BridgeWsReencrypt(clientStream, ws, scope, ctx, dc, isMedia, splitter, peer, tokenId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} WS bridge canceled", scope, dc, mediaTag);
        }
        catch (TimeoutException)
        {
            logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} WS bridge timed out", scope, dc, mediaTag);
        }
        catch (IOException ex) when (IsExpectedDisconnect(ex))
        {
            logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} WS bridge disconnected", scope, dc, mediaTag);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] DC{Dc}{MediaTag} WS bridge socket disconnected", scope, dc, mediaTag);
        }
        catch (Exception ex)
        {
            peerStats.RecordWsError(peer, tokenId);
            logger.LogError(ex, "[{Scope}] DC{Dc}{MediaTag} WS bridge failed", scope, dc, mediaTag);
        }
        finally
        {
            await CloseQuietlyAsync(ws, ct).ConfigureAwait(false);
        }

        return "ws";
    }

    /// <summary>
    /// Собирает четыре AES-CTR потока: два для клиента и два для Telegram
    /// (порт upstream <c>_build_crypto_ctx</c>).
    /// </summary>
    private static CryptoCtx BuildCryptoContext(byte[] init, byte[] relayInit, byte[] secret)
    {
        // dec_prekey_iv = init[8:56]; dec_key = SHA256(dec_prekey + secret)
        var clientDecPrekeyIv = init.AsSpan(Protocol.SkipLen, Protocol.PreKeyLen + Protocol.IvLen).ToArray();
        var clientDecKey = DeriveKey(clientDecPrekeyIv.AsSpan(0, Protocol.PreKeyLen), secret);
        var clientDecIv = clientDecPrekeyIv.AsSpan(Protocol.PreKeyLen, Protocol.IvLen).ToArray();

        // enc_prekey_iv = dec_prekey_iv[::-1]
        var clientEncPrekeyIv = clientDecPrekeyIv.ToArray();
        Array.Reverse(clientEncPrekeyIv);
        var clientEncKey = DeriveKey(clientEncPrekeyIv.AsSpan(0, Protocol.PreKeyLen), secret);
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
    private static byte[] DeriveKey(ReadOnlySpan<byte> prekey, byte[] secret)
    {
        var buffer = new byte[prekey.Length + secret.Length];
        prekey.CopyTo(buffer);
        secret.AsSpan().CopyTo(buffer.AsSpan(prekey.Length));
        return System.Security.Cryptography.SHA256.HashData(buffer);
    }

    /// <summary>
    /// Проверяет, относится ли исключение к ожидаемому разрыву клиентского соединения.
    /// </summary>
    private static bool IsExpectedDisconnect(IOException ex)
        => ex is EndOfStreamException ||
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
