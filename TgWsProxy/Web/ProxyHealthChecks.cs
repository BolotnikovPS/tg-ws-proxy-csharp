using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;

namespace TgWsProxy.Web;

/// <summary>
/// Проверка живости процесса (F-3.4): отвечает Healthy, пока обслуживается сам
/// HTTP-запрос. Используется как liveness-проверка <c>/healthz</c>.
/// </summary>
public sealed class SelfHealthCheck : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("process is alive"));
}

/// <summary>
/// Проверка готовности прокси-слушателя (F-3.4): пока идёт перезапуск слушателя,
/// состояние Degraded; если слушатель не поднят — Unhealthy.
/// </summary>
/// <param name="listener">Контроллер слушателя.</param>
public sealed class ListenerHealthCheck(IListenerController listener) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = listener.State;

        return Task.FromResult(state switch
        {
            ListenerState.Listening => HealthCheckResult.Healthy("listener is accepting connections"),
            ListenerState.Restarting => HealthCheckResult.Degraded("listener is restarting"),
            _ => HealthCheckResult.Unhealthy("listener is not available"),
        });
    }
}

/// <summary>
/// Проверка конфигурации (F-3.4): файл читается и каталог доступен для записи —
/// иначе <c>PUT /api/config</c> не сможет сохранить изменения.
/// </summary>
/// <param name="config">Действующая конфигурация.</param>
/// <param name="runtime">Сведения о файле конфигурации.</param>
public sealed class ConfigHealthCheck(Config config, ConfigRuntime runtime) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            ConfigValidator.Validate(config);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "configuration is invalid", exception));
        }

        var path = runtime.Path;
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"configuration file is not readable: {path}", exception));
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"configuration directory does not exist: {directory}"));
        }

        try
        {
            var probe = Path.Combine(directory, $".healthz-{Guid.NewGuid():N}.tmp");
            using (var stream = File.Create(probe))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"configuration directory is not writable: {directory}", exception));
        }

        return Task.FromResult(HealthCheckResult.Healthy("configuration is readable and writable"));
    }
}

/// <summary>
/// Проверка реестра статистики (F-3.4): переполнение таблицы пар (IP, токен)
/// означает потерю данных мониторинга, поэтому состояние Degraded.
/// </summary>
/// <param name="peerStats">Реестр статистики по парам (IP, токен).</param>
public sealed class PeerStatsHealthCheck(IPeerStatsRegistry peerStats) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var overflow = peerStats.OverflowCount;

        return Task.FromResult(overflow > 0
            ? HealthCheckResult.Degraded(
                $"per-peer registry dropped {overflow} entries after reaching its limit")
            : HealthCheckResult.Healthy($"tracking {peerStats.TrackedPeers} pairs"));
    }
}

/// <summary>
/// Публикует длительность health check в Prometheus (F-3.10).
/// </summary>
/// <remarks>
/// Штатный <c>PrometheusHealthCheckPublisher</c> умеет публиковать только статус
/// (в переданный <see cref="PrometheusHealthCheckPublisherOptions.Gauge"/>),
/// поэтому длительность проверок снимается здесь, в реестре того же приложения.
/// </remarks>
/// <param name="healthCheckService">Служба выполнения health check.</param>
/// <param name="metrics">Реестр метрик приложения.</param>
public sealed class HealthDurationPublisher(
    HealthCheckService healthCheckService, ProxyMetrics metrics) : IHealthCheckPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        // Отчёт приходит от службы health check: публикатор статуса уже выполнил
        // проверки, поэтому здесь остаётся только разложить длительности по меткам.
        _ = healthCheckService;
        _ = cancellationToken;

        foreach (var (name, entry) in report.Entries)
        {
            metrics.HealthDurationGauge.WithLabels(name).Set(entry.Duration.TotalSeconds);
        }

        await Task.CompletedTask;
    }
}
