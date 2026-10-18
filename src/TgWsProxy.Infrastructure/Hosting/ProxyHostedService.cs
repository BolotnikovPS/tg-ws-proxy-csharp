using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.Metrics;
using TgWsProxy.Infrastructure.Session;

namespace TgWsProxy.Infrastructure.Hosting;

/// <summary>
/// Запускает прокси-сервер как hosted-сервис (F-1.1), инкапсулируя прежнюю логику
/// старта и остановки из <c>Program.cs</c>, и перезапускает листенер внутри процесса (§ 2.7).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/> намеренно не блокирует вызывающего: <see cref="IProxyServer.Run"/>
/// работает до отмены, а хост обязан завершить старт, чтобы поднять Kestrel и остальные сервисы.
/// Поэтому цикл приёма соединений уходит в фоновую задачу, а <see cref="Completion"/>
/// позволяет точке входа дождаться его завершения.
/// </para>
/// <para>
/// Порядок остановки сохранён прежним (F-1.3): <c>cfDomainRefresher.Stop()</c> →
/// <c>wsPool.Reset()</c> → <c>cfWorkerPool.Reset()</c> → <c>cfH2Pool.CloseAsync()</c>.
/// </para>
/// </remarks>
/// <remarks>Создаёт hosted-сервис со всеми зависимостями прокси из DI.</remarks>
public sealed class ProxyHostedService(
    IProxyServer server,
    IClientSessionHandler sessionHandler,
    IProxyStats stats,
    IWsPool wsPool,
    ICfWorkerPool cfWorkerPool,
    ICfDomainRefresher cfDomainRefresher,
    ICfH2Pool cfH2Pool,
    ITcpBridgeService bridgeService,
    IActiveSessionRegistry activeSessions,
    MetricsHistory history,
    Config config,
    Dictionary<int, string> dcOptions,
    ILogger<ProxyHostedService> logger
    ) : IHostedService, IAsyncDisposable, IListenerController
{
    /// <summary>Предельное время перезапуска листенера (F-7.6).</summary>
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Серия перезапусков: второй одновременный запрос отклоняется (F-7.6).</summary>
    private readonly SemaphoreSlim _restartGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Task? _statsTask;
    private Task? _historyTask;
    private int _started;
    private volatile bool _faulted;

    private volatile bool _restarting;
    private int _restartCount;
    private DateTimeOffset? _lastRestartAt;

    /// <summary>Признак того, что прокси уже запущен (хостом или вручную).</summary>
    public bool IsStarted => Volatile.Read(ref _started) == 1;

    /// <summary>Признак аварийного завершения цикла приёма соединений.</summary>
    public bool Faulted => _faulted;

    /// <summary>
    /// Завершается, когда цикл приёма соединений остановлен. Точка входа ждёт эту задачу,
    /// чтобы сохранить прежний код возврата процесса.
    /// </summary>
    public Task Completion => _runTask ?? Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Состояние выводится из фактического сокета: пока идёт перезапуск — <see cref="ListenerState.Restarting"/>,
    /// иначе <see cref="ListenerState.Listening"/> при открытом порте и <see cref="ListenerState.Unavailable"/>
    /// в промежутке между откатом неудачной привязки и повторным bind. Так <c>/healthz/ready</c>
    /// и панель никогда не показывают «слушает», когда порт закрыт (F-7.5, F-7.7).
    /// </remarks>
    public ListenerState State => _restarting
        ? ListenerState.Restarting
        : server.IsListening ? ListenerState.Listening : ListenerState.Unavailable;

    /// <inheritdoc />
    public int RestartCount => Volatile.Read(ref _restartCount);

    /// <inheritdoc />
    public DateTimeOffset? LastRestartAt => _lastRestartAt;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        StartSubsystems(token);

        _statsTask = RunPeriodicAsync(
            TimeSpan.FromSeconds(60),
            () => logger.LogInformation("stats: {Summary} | pool: {Pool}", stats.Summary(), wsPool.FormatPoolSummary()),
            token);

        // Наполнение кольцевого буфера для /api/metrics/timeseries. Отдельная задача,
        // не зависящая от 60-секундного логирования: шаг выборки задан окном графика.
        _historyTask = RunPeriodicAsync(
            TimeSpan.FromSeconds(history.StepSeconds),
            () => history.Record(DateTimeOffset.UtcNow, stats.Snapshot()),
            token,
            runImmediately: true);

        _runTask = Task.Run(() => RunProxyAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await StopSubsystemsAsync().ConfigureAwait(false);

        await IgnoreCancellationAsync(_statsTask).ConfigureAwait(false);
        await IgnoreCancellationAsync(_historyTask).ConfigureAwait(false);
        await IgnoreCancellationAsync(_runTask).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ListenerRestartResult> RestartAsync(Config candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!await _restartGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new ListenerRestartInProgressException();
        }

        try
        {
            _restarting = true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RestartTimeout);

            var oldHost = config.Host;
            var oldPort = config.Port;
            var endpoint = new IPEndPoint(IPAddress.Parse(candidate.Host), candidate.Port);
            var dropped = 0;

            try
            {
                await server.RebindAsync(
                    endpoint,
                    async ct =>
                    {
                        // (3) Подсистемы останавливаются до новой привязки.
                        await StopSubsystemsAsync().ConfigureAwait(false);

                        // (4) Живые сессии разрываются немедленно (F-7.4).
                        dropped = activeSessions.CloseAll();
                    },
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                // Прежняя привязка восстановлена самим сервером: возвращаем подсистемы в работу,
                // чтобы прокси не остался без пулов.
                StartSubsystems(_cts?.Token ?? CancellationToken.None);
                logger.LogWarning(ex, "Listener rebind to {Host}:{Port} failed, rolled back", candidate.Host, candidate.Port);
                return ListenerRestartResult.Failed($"Cannot bind {candidate.Host}:{candidate.Port}: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                StartSubsystems(_cts?.Token ?? CancellationToken.None);
                return ListenerRestartResult.Failed(
                    $"Listener restart timed out after {RestartTimeout.TotalSeconds:0} s.");
            }

            // (6) Конфигурация переносится в живой Config после успешной привязки (F-7.3),
            // чтобы прогреваемые пулы уже видели новые значения.
            ApplyCandidate(candidate);

            // (7) Пулы и refresher поднимаются на новой конфигурации.
            StartSubsystems(_cts?.Token ?? CancellationToken.None);

            Interlocked.Increment(ref _restartCount);
            _lastRestartAt = DateTimeOffset.UtcNow;

            logger.LogInformation(
                "Listener restarted: {OldHost}:{OldPort} -> {NewHost}:{NewPort}, {Dropped} session(s) dropped",
                oldHost,
                oldPort,
                candidate.Host,
                candidate.Port,
                dropped);

            return ListenerRestartResult.Ok(dropped);
        }
        finally
        {
            _restarting = false;
            _restartGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Остановка при выгрузке процесса не должна бросать.
        }

        _cts?.Dispose();
        _cts = null;
        _restartGate.Dispose();
    }

    /// <summary>Поднимает пулы, refresher и прогрев — общий путь старта и перезапуска.</summary>
    private void StartSubsystems(CancellationToken token)
    {
        bridgeService.ResetTcpBackoff();
        wsPool.Reset();
        cfWorkerPool.Reset();
        cfDomainRefresher.Start(token);

        sessionHandler.Warmup(token);
        cfWorkerPool.Warmup();
    }

    /// <summary>Останавливает пулы и refresher в порядке F-1.3.</summary>
    private async Task StopSubsystemsAsync()
    {
        cfDomainRefresher.Stop();
        wsPool.Reset();
        cfWorkerPool.Reset();

        try
        {
            // CloseAsync освобождает HTTP/2-каналы Cloudflare; ждать долго не нужно.
            await cfH2Pool.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "HTTP/2 pool close failed during listener restart");
        }
    }

    /// <summary>
    /// Переносит значения проверенного кандидата в живой <see cref="Config"/> и обновляет карту
    /// DC-адресов, на которую опираются пулы.
    /// </summary>
    private void ApplyCandidate(Config candidate)
    {
        ConfigDtoMapper.Apply(ConfigDtoMapper.FromConfig(candidate), config);

        var updated = ConfigDefaults.ParseDcIpList(candidate.DcIp);
        dcOptions.Clear();
        foreach (var (dc, ip) in updated)
        {
            dcOptions[dc] = ip;
        }
    }

    private async Task RunProxyAsync(CancellationToken token)
    {
        try
        {
            await server.Run(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Штатная остановка по SIGTERM/Ctrl+C.
        }
        catch (Exception ex)
        {
            _faulted = true;
            logger.LogCritical(ex, "Server crashed");
        }
    }

    private async Task RunPeriodicAsync(
        TimeSpan interval,
        Action action,
        CancellationToken token,
        bool runImmediately = false)
    {
        try
        {
            if (runImmediately)
            {
                action();
            }

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Один сбойный тик не должен останавливать фоновую задачу.
                    logger.LogDebug(ex, "Periodic task failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо при остановке сервиса.
        }
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
