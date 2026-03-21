using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure;

internal sealed class ProxyServer(Config cfg, IClientSessionHandler sessionHandler, ILogger<ProxyServer> logger) : IProxyServer
{
    private long _connectionSeq;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        LogLinks();

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
                _ = Task.Factory.StartNew(
                    () => sessionHandler.HandleAsync(client, context, cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Current
                    ).Unwrap();
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
    private void LogLinks()
    {
        logger.LogInformation("Telegram WS Bridge Proxy listening on {Host}:{Port}", cfg.Host, cfg.Port);

        if (cfg.Credentials.Count > 0)
        {
            foreach (var config in cfg.Credentials)
            {
                var link = $"https://t.me/socks?server={cfg.Host}&port={cfg.Port}&user={config.Login}&pass={config.Password}";
                logger.LogInformation("Telegram WS Bridge {link}", link);
            }
            return;
        }

        var linkNoUserNoPass = $"https://t.me/socks?server={cfg.Host}&port={cfg.Port}";
        logger.LogInformation("Telegram WS Bridge {link}", linkNoUserNoPass);
    }
}
