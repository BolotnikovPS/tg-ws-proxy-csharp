using System.Net;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
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

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxy.BasePath);
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
    public WebProxyRelayLease OpenSession(ProxyTokenEntry token, IPAddress? peer, string scope)
    {
        stats.IncConnectionsTotal();
        stats.IncConnectionsActive();

        // F-W.21: сессия регистрируется в реестре живых сессий, чтобы отзыв токена (F-8.7)
        // или перезапуск слушателя разрывал её так же, как сокетные TCP-сессии. Разрыв
        // выполняется отменой LifetimeToken, на который подписан менеджер сессий.
        var sessionCts = new CancellationTokenSource();
        var registryLease = sessions.Register(sessionCts.Cancel, scope);
        registryLease.AssignToken(token.Id);

        if (peer is not null)
        {
            // F-9.1: открытие учитывается без токена, затем переезжает в пару (IP, токен).
            peerStats.ConnectionOpened(peer);
            peerStats.BindToken(peer, token.Id);
        }

        tokens.Touch(token.Id);
        logger.LogDebug("[{Scope}] web-proxy session opened token={Token}", scope, token.Name);

        return new WebProxyRelayLease(
            (stream, streamCt) => BridgeStreamAsync(stream, token, scope, peer, streamCt),
            sessionCts.Token,
            () =>
            {
                stats.DecConnectionsActive();
                if (peer is not null)
                {
                    peerStats.ConnectionClosed(peer, token.Id);
                }

                // Идемпотентно: в пути реестра CTS уже отменён (это действие разрыва), в пути
                // DELETE — отменяем, чтобы погасить подписку LifetimeToken. Dispose не вызываем,
                // чтобы не разбирать CTS из собственного callback.
                sessionCts.Cancel();
                registryLease.Dispose();
            });
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
            // Структурированная корреляция relay-потока: tokenId + streamId доступны для поиска
            // во всех событиях этого потока (bad handshake, bridge error и т.д.).
            using var streamScope = logger.BeginScope(new Dictionary<string, object>
            {
                ["TokenId"] = token.Id,
                ["StreamId"] = stream.StreamId,
            });

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
