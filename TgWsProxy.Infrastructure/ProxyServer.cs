using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure;

internal sealed class ProxyServer(
    Config cfg,
    IClientSessionHandler sessionHandler,
    ILogger<ProxyServer> logger) : IProxyServer
{
    private long _connectionSeq;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Telegram WS Bridge Proxy listening on {Host}:{Port}", cfg.Host, cfg.Port);
        using var listener = new TcpListener(IPAddress.Parse(cfg.Host), cfg.Port);
        listener.Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var connectionId = Interlocked.Increment(ref _connectionSeq).ToString("D6");
                var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
                var scope = $"{peer}|{connectionId}";
                var context = new ClientContext(scope, peer, connectionId);
                _ = Task.Run(() => sessionHandler.HandleAsync(client, context, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Server accept loop canceled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accept loop failed");
            }
        }
    }
}
