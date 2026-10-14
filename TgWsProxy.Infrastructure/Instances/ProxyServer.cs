using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class ProxyServer(
    Config cfg,
    Dictionary<int, string> dcOpt,
    IClientSessionHandler sessionHandler,
    ILogger<ProxyServer> logger) : IProxyServer
{
    /// <summary>Период проверки живости слушающего сокета (порт upstream <c>LISTENER_CHECK_INTERVAL</c>).</summary>
    private const double ListenerCheckIntervalSeconds = 5.0;

    /// <summary>Пауза перед пересозданием слушателя (порт upstream <c>LISTENER_RESTART_DELAY</c>).</summary>
    private const int ListenerRestartDelayMs = 1000;

    private long _connectionSeq;

    public async Task Run(CancellationToken cancellationToken)
    {
        await LogLinks();

        var ipEndPoint = new IPEndPoint(IPAddress.Parse(cfg.Host), cfg.Port);

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket listener;
            try
            {
                listener = CreateListener(ipEndPoint);
            }
            catch (SocketException ex)
            {
                logger.LogError(ex, "Failed to restart server");
                return;
            }

            bool restart;
            try
            {
                restart = await AcceptLoop(listener, cancellationToken);
            }
            finally
            {
                listener.Dispose();
            }

            if (!restart)
            {
                return;
            }

            logger.LogWarning("Listening socket died, restarting server");
            try
            {
                await Task.Delay(ListenerRestartDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static Socket CreateListener(IPEndPoint ipEndPoint)
    {
        var listener = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(ipEndPoint);
            listener.Listen(int.MaxValue);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Цикл приёма соединений. Возвращает <c>true</c>, если слушающий сокет умер и сервер нужно
    /// пересоздать, и <c>false</c> при штатной остановке (порт upstream <c>_listener_watchdog</c>).
    /// </summary>
    private async Task<bool> AcceptLoop(Socket listener, CancellationToken cancellationToken)
    {
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var died = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = WatchdogAsync(listener, died, watchdogCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task<Socket> acceptTask;
                try
                {
                    acceptTask = listener.AcceptAsync(cancellationToken).AsTask();
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Server accept loop canceled");
                    return false;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Accept loop failed");
                    return true;
                }

                var completed = await Task.WhenAny(acceptTask, died.Task).ConfigureAwait(false);
                if (completed == died.Task)
                {
                    // Принятие осталось висеть: сокет всё равно будет закрыт вызывающей стороной.
                    ObserveFailure(acceptTask);
                    return true;
                }

                Socket client;
                try
                {
                    client = await acceptTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Server accept loop canceled");
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
                catch (SocketException)
                {
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Accept loop failed");
                    return true;
                }

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

            return false;
        }
        finally
        {
            watchdogCts.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Задача-наблюдатель завершается вместе с циклом приёма; её ошибки не важны.
            }
        }
    }

    /// <summary>Периодически проверяет, что слушающий сокет ещё жив.</summary>
    private static async Task WatchdogAsync(
        Socket listener,
        TaskCompletionSource<bool> died,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(ListenerCheckIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
                if (!IsListenerDead(listener))
                {
                    continue;
                }

                died.TrySetResult(true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение цикла приёма.
        }
        catch (ObjectDisposedException)
        {
            died.TrySetResult(true);
        }
    }

    private static bool IsListenerDead(Socket listener)
    {
        try
        {
            var handle = listener.SafeHandle;
            return handle.IsClosed || handle.IsInvalid;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static void ObserveFailure(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>
    /// Пишет в лог параметры запуска и готовые ссылки подключения для Telegram
    /// (порт upstream блока логирования в <c>_run</c>).
    /// </summary>
    private Task LogLinks()
    {
        var linkHost = GetLinkHost(cfg.Host);
        var ftls = cfg.FakeTlsDomain;

        var ddLink = $"tg://proxy?server={linkHost}&port={cfg.Port}&secret=dd{cfg.Secret}";
        var eeLink = "";
        if (!string.IsNullOrWhiteSpace(ftls))
        {
            var domainHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(ftls)).ToLowerInvariant();
            eeLink = $"tg://proxy?server={linkHost}&port={cfg.Port}&secret=ee{cfg.Secret}{domainHex}";
        }

        logger.LogInformation("{Sep}", new string('=', 60));
        logger.LogInformation("  Telegram MTProto WS Bridge Proxy");
        logger.LogInformation("  Listening on   {Host}:{Port}", cfg.Host, cfg.Port);
        logger.LogInformation("  Secret:        {Secret}", cfg.Secret);
        if (!string.IsNullOrWhiteSpace(ftls))
        {
            logger.LogInformation("  Fake TLS:      {FakeTls}", ftls);
        }

        logger.LogInformation("  Target DC IPs:");
        foreach (var dc in dcOpt.Keys.OrderBy(k => k))
        {
            logger.LogInformation("    DC{Dc}: {Ip}", dc, dcOpt[dc]);
        }

        if (cfg.CfProxyEnabled)
        {
            var userDomains = cfg.CfProxyDomains.Count > 0 ? string.Join(", ", cfg.CfProxyDomains) : "auto";
            logger.LogInformation("  CF proxy:      enabled ({Domains})", userDomains);
        }

        if (cfg.CfProxyWorkerDomains.Count > 0)
        {
            logger.LogInformation("  CF worker:     enabled ({Domains})", string.Join(", ", cfg.CfProxyWorkerDomains));
        }

        if (cfg.H2Enabled)
        {
            logger.LogInformation("  CF H2 media:   enabled");
        }

        if (cfg.DisableSecure)
        {
            logger.LogInformation("  No secure:     enabled (port 80 for CF proxy/worker)");
        }

        logger.LogInformation("{Sep}", new string('=', 60));
        logger.LogInformation("  Connect:");
        logger.LogInformation("    {Link}", string.IsNullOrWhiteSpace(ftls) ? ddLink : eeLink);
        logger.LogInformation("{Sep}", new string('=', 60));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Подбирает адрес для ссылки подключения: при прослушивании 0.0.0.0 определяется
    /// исходящий локальный IP (порт upstream <c>get_link_host</c>).
    /// </summary>
    private static string GetLinkHost(string host)
    {
        if (host != "0.0.0.0")
        {
            return host;
        }

        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80));
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch (SocketException)
        {
            return "127.0.0.1";
        }
    }
}
