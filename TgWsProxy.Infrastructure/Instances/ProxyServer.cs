using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class ProxyServer(Config cfg, IClientSessionHandler sessionHandler, ILogger<ProxyServer> logger) : IProxyServer
{
    private long _connectionSeq;

    public async Task Run(CancellationToken cancellationToken)
    {
        await LogLinks();

        var ipEndPoint = new IPEndPoint(IPAddress.Parse(cfg.Host), cfg.Port);
        using var listener = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(ipEndPoint);
        listener.Listen(int.MaxValue);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(cancellationToken);
                var connectionId = Interlocked.Increment(ref _connectionSeq).ToString("D6");
                var peer = client.RemoteEndPoint?.ToString() ?? "?";
                var scope = $"{peer}|{connectionId}";
                var context = new ClientContext(scope, peer, connectionId);
                BackgroundTaskRunner.RunDetachedSafe(
                    async ct => await sessionHandler.Handle(client, context, ct),
                    logger,
                    $"[{scope}] client session",
                    cancellationToken);
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

    /// <summary>
    /// Пишет в лог параметры запуска и готовые ссылки подключения для Telegram.
    /// </summary>
    private async Task LogLinks()
    {
        logger.LogInformation("Telegram WS Bridge Proxy listening on {Host}:{Port}", cfg.Host, cfg.Port);

        for (var i = 0; i < cfg.Secrets.Count; i++)
        {
            var tgLink = $"tg://proxy?server={cfg.Host}&port={cfg.Port}&secret={cfg.Secrets[i]}";
            logger.LogInformation("MTProto connect link #{Num}: {Link}", i + 1, tgLink);
        }
    }
}
