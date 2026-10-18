using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Session;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class ProxyServer(
    Config cfg,
    Dictionary<int, string> dcOpt,
    IClientSessionHandler sessionHandler,
    PeerAccessControl accessControl,
    ConnectionLimiter connectionLimiter,
    PeerRateLimiter peerRateLimiter,
    IPeerStatsRegistry peerStats,
    IActiveSessionRegistry activeSessions,
    IProxyStats stats,
    ILogger<ProxyServer> logger) : IProxyServer
{
    /// <summary>Период проверки живости слушающего сокета (порт upstream <c>LISTENER_CHECK_INTERVAL</c>).</summary>
    private const double ListenerCheckIntervalSeconds = 5.0;

    /// <summary>Пауза перед пересозданием слушателя (порт upstream <c>LISTENER_RESTART_DELAY</c>).</summary>
    private const int ListenerRestartDelayMs = 1000;

    /// <summary>Синхронизация запросов перепривязки (F-7.6: одновременно не более одного).</summary>
    private readonly object _rebindSync = new();

    private long _connectionSeq;

    /// <summary>
    /// Взводится, пока слушающий сокет открыт и цикл приёма работает; читается /healthz
    /// без блокировок, поэтому достаточно <c>volatile</c>.
    /// </summary>
    private volatile bool _listening;

    /// <summary>Адрес, на который нужно перепривязаться после остановки приёма.</summary>
    private IPEndPoint? _requestedEndpoint;

    /// <summary>Запрос перепривязки взведён и ещё не обработан циклом.</summary>
    private bool _rebindPending;

    /// <summary>Сигнал о результате привязки нового адреса (значение — <see cref="SocketException"/>).</summary>
    private TaskCompletionSource<object?>? _rebindSignal;

    /// <summary>Токен текущей генерации цикла приёма: отменяется при перепривязке.</summary>
    private CancellationTokenSource? _acceptCts;

    /// <summary>Завершается, когда текущая генерация цикла приёма фактически остановлена.</summary>
    private TaskCompletionSource<bool>? _acceptExited;

    public bool IsListening => _listening;

    public async Task Run(CancellationToken cancellationToken)
    {
        await LogLinks();

        var endpoint = new IPEndPoint(IPAddress.Parse(cfg.Host), cfg.Port);

        while (!cancellationToken.IsCancellationRequested)
        {
            bool rebindPending;
            IPEndPoint? requested;
            lock (_rebindSync)
            {
                rebindPending = _rebindPending;
                requested = _requestedEndpoint;
            }

            // Пока привязка не подтверждена, работаем на прежнем адресе: откат не требует
            // отдельной ветки — достаточно не фиксировать новый endpoint.
            var bindTarget = rebindPending && requested is not null ? requested : endpoint;

            Socket listener;
            try
            {
                listener = CreateListener(bindTarget);
            }
            catch (SocketException ex)
            {
                _listening = false;

                if (rebindPending)
                {
                    // Новый адрес занят: сообщаем контроллеру, продолжаем на прежней привязке.
                    CompleteRebind(ex);
                    try
                    {
                        await Task.Delay(ListenerRestartDelayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                logger.LogError(ex, "Failed to bind listener on {Endpoint}", bindTarget);
                return;
            }

            if (rebindPending)
            {
                endpoint = bindTarget;
                CompleteRebind(null);
                logger.LogInformation("Listener bound to {Endpoint}", bindTarget);
            }

            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _acceptCts = acceptCts;
            _acceptExited = exited;

            bool died;
            try
            {
                _listening = true;
                died = await AcceptLoop(listener, acceptCts.Token);
            }
            finally
            {
                _listening = false;
                _acceptCts = null;
                _acceptExited = null;
                listener.Dispose();
                exited.TrySetResult(true);
            }

            // Перепривязка запрошена во время работы: цикл не завершается, а идёт на новый адрес.
            lock (_rebindSync)
            {
                rebindPending = _rebindPending;
            }

            if (rebindPending)
            {
                continue;
            }

            if (!died)
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

    public async Task RebindAsync(
        IPEndPoint endpoint,
        Func<CancellationToken, Task>? betweenStopAndBind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var acceptCts = _acceptCts;
        var exited = _acceptExited;
        if (acceptCts is null || exited is null)
        {
            throw new InvalidOperationException("The listener is not running.");
        }

        TaskCompletionSource<object?> signal;
        lock (_rebindSync)
        {
            if (_rebindPending)
            {
                throw new InvalidOperationException("A listener rebind is already pending.");
            }

            signal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _rebindSignal = signal;
            _requestedEndpoint = endpoint;
            _rebindPending = true;
        }

        try
        {
            // (2) Останавливаем accept-loop и watchdog и ждём фактического выхода.
            try
            {
                acceptCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Генерация уже завершилась сама.
            }

            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // (3)-(4) Контроллер останавливает пулы и разрывает активные сессии.
            if (betweenStopAndBind is not null)
            {
                await betweenStopAndBind(cancellationToken).ConfigureAwait(false);
            }

            // (5) Ждём результата привязки нового адреса.
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Привязка не состоялась или операция прервана: снимаем запрос, чтобы цикл
            // остался на прежнем адресе и не перепривязался «втихую» позже.
            lock (_rebindSync)
            {
                _rebindPending = false;
                _requestedEndpoint = null;
                _rebindSignal = null;
            }

            throw;
        }
    }

    /// <summary>Сообщает ожидающему результат привязки и снимает флаг перепривязки.</summary>
    private void CompleteRebind(SocketException? error)
    {
        TaskCompletionSource<object?>? signal;
        lock (_rebindSync)
        {
            _rebindPending = false;
            _requestedEndpoint = null;
            signal = _rebindSignal;
            _rebindSignal = null;
        }

        if (error is null)
        {
            signal?.TrySetResult(null);
        }
        else
        {
            signal?.TrySetException(error);
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

                // Адрес пира без порта — ключ учёта статистики по IP (нормализуется в реестре).
                var peerAddress = (client.RemoteEndPoint as IPEndPoint)?.Address;
                var context = new ClientContext(scope, peer, connectionId, peerAddress);

                // ACL проверяется до рукопожатия по реальному IP пира (PROXY protocol не подделывает адрес).
                if (client.RemoteEndPoint is IPEndPoint remote && !accessControl.IsAllowed(remote.Address))
                {
                    stats.IncConnectionsTotal();
                    stats.IncConnectionsBlocked();
                    peerStats.RecordBlocked(remote.Address);
                    logger.LogWarning("[{Scope}] connection rejected by IP ACL", scope);
                    client.Dispose();
                    continue;
                }

                // Ограничители тоже работают до рукопожатия: отклонённые соединения не тратят
                // время на чтение init и не занимают слот активной сессии.
                var slotAcquired = false;
                if (client.RemoteEndPoint is IPEndPoint limitedPeer)
                {
                    if (connectionLimiter.IsEnabled)
                    {
                        if (!connectionLimiter.TryAcquire())
                        {
                            stats.IncConnectionsTotal();
                            stats.IncConnectionsLimited();
                            peerStats.RecordLimited(limitedPeer.Address);
                            logger.LogWarning(
                                "[{Scope}] connection rejected by max-connections limit ({Active}/{Limit})",
                                scope,
                                connectionLimiter.Active,
                                connectionLimiter.Limit);
                            client.Dispose();
                            continue;
                        }

                        slotAcquired = true;
                    }

                    if (peerRateLimiter.IsEnabled && !peerRateLimiter.TryAcquire(limitedPeer.Address))
                    {
                        stats.IncConnectionsTotal();
                        stats.IncConnectionsRateLimited();
                        peerStats.RecordRateLimited(limitedPeer.Address);
                        if (slotAcquired)
                        {
                            connectionLimiter.Release();
                            slotAcquired = false;
                        }

                        logger.LogWarning(
                            "[{Scope}] connection rejected by per-IP rate limit ({Limit}/{Window}s)",
                            scope,
                            peerRateLimiter.Limit,
                            peerRateLimiter.WindowSeconds);
                        client.Dispose();
                        continue;
                    }
                }

                var slotHeld = slotAcquired;

                // Учёт per-IP открывается только для принятой сессии и закрывается в её finally,
                // чтобы active-счётчик не «протекал» на отклонённых соединениях.
                if (peerAddress is { } acceptedPeer)
                {
                    peerStats.ConnectionOpened(acceptedPeer);
                }

                // F-7.4: сессия попадает в реестр живых сессий, чтобы перезапуск слушателя
                // и отзыв токена могли её разорвать и посчитать.
                var lease = activeSessions.Register(client, scope);

                BackgroundTaskRunner.RunDetachedSafe(
                    async ct =>
                    {
                        try
                        {
                            await sessionHandler.Handle(client, context, lease, ct);
                        }
                        finally
                        {
                            // F-9.1: сессия закрывается в той же паре (IP, токен), в которой
                            // её учёл BindToken после успешного handshake.
                            var boundTokenId = lease.TokenId;
                            lease.Dispose();

                            if (peerAddress is { } closedPeer)
                            {
                                peerStats.ConnectionClosed(closedPeer, boundTokenId);
                            }

                            if (slotHeld)
                            {
                                connectionLimiter.Release();
                            }
                        }
                    },
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
    /// (порт upstream блока логирования в <c>_run</c>). Ссылки печатаются для каждого
    /// включённого токена (F-8.7), сам секрет в лог не попадает — только маска (N-8).
    /// </summary>
    internal Task LogLinks()
    {
        var linkHost = GetLinkHost(cfg.Host);
        var ftls = cfg.FakeTlsDomain;
        var domainHex = "";
        if (!string.IsNullOrWhiteSpace(ftls))
        {
            domainHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(ftls)).ToLowerInvariant();
        }

        logger.LogInformation("{Sep}", new string('=', 60));
        logger.LogInformation("  Telegram MTProto WS Bridge Proxy");
        logger.LogInformation("  Listening on   {Host}:{Port}", cfg.Host, cfg.Port);

        var enabled = cfg.Tokens.Where(static t => t.Enabled).ToList();
        logger.LogInformation("  Tokens:        {Enabled} enabled of {Total}", enabled.Count, cfg.Tokens.Count);

        foreach (var token in enabled)
        {
            var ddLink = $"tg://proxy?server={linkHost}&port={cfg.Port}&secret=dd{token.Secret}";
            logger.LogInformation("  [{Name}] secret={Masked}", token.Name, SecretMask.Mask(token.Secret));
            logger.LogInformation("  [{Name}] dd: {Link}", token.Name, ddLink);
            if (domainHex.Length > 0)
            {
                var eeLink = $"tg://proxy?server={linkHost}&port={cfg.Port}&secret=ee{token.Secret}{domainHex}";
                logger.LogInformation("  [{Name}] ee: {Link}", token.Name, eeLink);
            }

            // WEB proxy (browser-relay) ссылка — только если bridge включён и внешний адрес задан.
            // Bridge-URL (с capability) в лог не попадает: capability не логируется (N-W.4/§3.4).
            if (cfg.WebProxyEnabled)
            {
                var webLink = WebProxyLinkBuilder.BuildTgLink(cfg, token);
                if (webLink is not null)
                {
                    logger.LogInformation("  [{Name}] web: {Link}", token.Name, webLink);
                }
            }
        }

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

        if (connectionLimiter.IsEnabled)
        {
            logger.LogInformation("  Max conns:     {Limit}", connectionLimiter.Limit);
        }

        if (peerRateLimiter.IsEnabled)
        {
            logger.LogInformation("  Rate limit:    {Limit} per {Window}s per IP", peerRateLimiter.Limit, peerRateLimiter.WindowSeconds);
        }

        logger.LogInformation("{Sep}", new string('=', 60));
        logger.LogInformation(
            "  Connect:       {Hint}",
            enabled.Count == 0
                ? "no enabled tokens - handshake is impossible until a token is added"
                : $"use the link of the token issued to the device ({enabled.Count} available)");
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
