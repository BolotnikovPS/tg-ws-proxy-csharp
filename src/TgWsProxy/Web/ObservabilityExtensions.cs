using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using TgWsProxy.Domain;

namespace TgWsProxy.Web;

/// <summary>
/// Регистрация наблюдаемости приложения: единственный реестр метрик Prometheus и
/// health checks (F-3.1, F-3.4, F-3.10).
/// </summary>
/// <remarks>
/// Вынесено из <c>Program.cs</c>, чтобы тестовый хост поднимал ровно тот же набор
/// сервисов: эндпоинты <c>/metrics</c> и <c>/healthz/ready</c> без этих регистраций
/// падают при построении маршрутов, а не при запросе.
/// </remarks>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Добавляет реестр метрик, health checks и публикаторы их статусов.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="config">Действующая конфигурация (нужна проверке конфигурации).</param>
    /// <returns>Та же коллекция сервисов.</returns>
    public static IServiceCollection AddProxyObservability(this IServiceCollection services, Config config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // F-3.1: единственный реестр метрик приложения. /metrics и публикаторы
        // health check пишут в него, поэтому второго источника истины нет.
        services.AddSingleton<ProxyMetrics>();

        // F-3.4/F-3.10: liveness — self, readiness — слушатель, конфигурация и
        // реестр статистики. Статусы публикует prometheus-net.AspNetCore.HealthChecks
        // в наш Gauge, длительность проверок — собственный публикатор в том же реестре.
        services
            .AddHealthChecks()
            .AddCheck<SelfHealthCheck>("self", tags: ["live"])
            .AddCheck<ListenerHealthCheck>("proxy-listener", tags: ["ready"])
            .AddCheck<WebProxyHealthCheck>("web-proxy-bridge", tags: ["ready"])
            .AddCheck<ConfigHealthCheck>("config", tags: ["ready"])
            .AddCheck<PeerStatsHealthCheck>("stats-registry", tags: ["ready"])
            .ForwardToPrometheus();

        services.AddOptions<PrometheusHealthCheckPublisherOptions>()
            .Configure<ProxyMetrics>((options, metrics) => options.Gauge = metrics.HealthStatusGauge);

        services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(5);
            options.Period = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<IHealthCheckPublisher, HealthDurationPublisher>();

        return services;
    }
}
