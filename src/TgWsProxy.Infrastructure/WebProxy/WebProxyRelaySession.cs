using System.Net;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Relay-сессия WEB proxy: привязка канала к токену по HMAC bridge-capability, затем relay-цикл
/// (мультиплексор потоков + MTProto-мосты поверх транспорта). Транспорт (WebSocket) принадлежит
/// хосту; здесь — только транспортно-независимая часть поверх <see cref="IRelayTransport"/>.
/// </summary>
internal sealed class WebProxyRelaySession(
    Config cfg,
    IMtProtoInspector inspector,
    MtProtoBridgeEngine engine,
    IProxyTokenRegistry tokens,
    IProxyStats stats,
    IPeerStatsRegistry peerStats,
    IActiveSessionRegistry sessions,
    ILogger<WebProxyRelaySession> logger) : IWebProxyRelaySession
{
    /// <inheritdoc />
    public ProxyTokenEntry? ResolveToken(string host, string? capability)
    {
        if (capability is not { Length: > 0 })
        {
            return null;
        }

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        var candidates = tokens.HandshakeOrder;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Secret.Length == 0)
            {
                continue;
            }

            if (BridgeCapabilityVerifier.Verify(candidate.Secret, host, basePath, capability))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task RunAsync(
        IRelayTransport transport,
        ProxyTokenEntry token,
        IPAddress? peer,
        string scope,
        CancellationToken ct)
    {
        stats.IncConnectionsTotal();
        stats.IncConnectionsActive();

        // F-W.21: поток регистрируется в реестре живых сессий, чтобы отзыв токена (F-8.7)
        // или перезапуск слушателя разрывал его так же, как сокетные TCP-сессии. Разрыв
        // выполняется отменой связанного токена отмены, на котором спит relay-цикл.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var lease = sessions.Register(sessionCts.Cancel, scope);
        lease.AssignToken(token.Id);

        try
        {
            if (peer is not null)
            {
                // F-9.1: открытие учитывается без токена, затем переезжает в пару (IP, токен).
                peerStats.ConnectionOpened(peer);
                peerStats.BindToken(peer, token.Id);
            }

            tokens.Touch(token.Id);
            logger.LogDebug("[{Scope}] web-proxy bridge connected token={Token}", scope, token.Name);

            Func<RelayStream, CancellationToken, Task> startBridge = (stream, streamCt) =>
                BridgeStreamAsync(stream, token, scope, peer, streamCt);

            await using var multiplexer = new RelayMultiplexer(transport, startBridge);

            while (true)
            {
                var frame = await transport.ReceiveAsync(sessionCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                await multiplexer.HandleInboundFrameAsync(frame.Value, sessionCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] web-proxy bridge canceled", scope);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("[{Scope}] web-proxy bridge protocol error: {Message}", scope, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] web-proxy bridge failed", scope);
        }
        finally
        {
            stats.DecConnectionsActive();
            if (peer is not null)
            {
                peerStats.ConnectionClosed(peer, token.Id);
            }
        }
    }

    /// <summary>
    /// Мост одного relay-потока: читает init (64 байта), проверяет секрет привязанного токена
    /// и передаёт поток в <see cref="MtProtoBridgeEngine"/>.
    /// </summary>
    private async Task BridgeStreamAsync(
        RelayStream stream,
        ProxyTokenEntry token,
        string scope,
        IPAddress? peer,
        CancellationToken ct)
    {
        try
        {
            var init = new byte[Protocol.HandshakeLen];
            await ReadExactlyAsync(stream, init, ct).ConfigureAwait(false);

            var handshake = inspector.TryHandshake(init, token.Secret);
            if (handshake is null)
            {
                stats.IncConnectionsBad();
                logger.LogWarning("[{Scope}] web-proxy stream bad handshake", scope);
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await engine
                .BridgeAsync(stream, init, handshake.Value, token, scope, peer, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Поток закрыт или соединение разорвано — штатная ситуация.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[{Scope}] web-proxy stream bridge error", scope);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
