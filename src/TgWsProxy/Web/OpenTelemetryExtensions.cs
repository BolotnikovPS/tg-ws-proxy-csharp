using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TgWsProxy.Domain;

namespace TgWsProxy.Web;

/// <summary>
/// Регистрация OpenTelemetry (OTLP). Трассы и метрики — через OpenTelemetry SDK; логи — через
/// <c>Serilog.Sinks.OpenTelemetry</c> (см. Program.cs), чтобы лог-события уходили в тот же backend
/// и коррелировали с трассами по TraceId/SpanId (гармонирует с <c>Serilog.Enrichers.Span</c>).
///
/// Включается только при <see cref="OtelConfig.Enabled"/>: по умолчанию выключено и не влияет на
/// поведение. Блок <c>otel</c> — «только старт»: SDK и синки собираются один раз при запуске.
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Добавляет OTLP-телеаметрию. Использует посигнальный <c>AddOtlpExporter</c>, а не единый
    /// <c>UseOtlpExporter()</c>, чтобы включать метрики независимо от трасс; смешивать оба
    /// подхода нельзя. Логи в этот SDK-конвейер осознанно не подключаются: ими владеет Serilog.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    /// <param name="config">Действующая конфигурация (блок <c>otel</c>).</param>
    /// <param name="appVersion">Версия сборки для ресурса <c>service.version</c>.</param>
    /// <returns>Та же коллекция сервисов.</returns>
    public static IServiceCollection AddProxyOpenTelemetry(
        this IServiceCollection services,
        Config config,
        string appVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Otel.Enabled)
        {
            return services;
        }

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ResolveServiceName(config), serviceVersion: appVersion));

        // Трассами управляет OpenTelemetry SDK: он сам ставит ActivityListener на
        // "Microsoft.AspNetCore" (с сэмплированием), поэтому ручной InstallRequestActivityListener
        // в Program.cs не нужен — он ставится только когда OTel-трассы выключены.
        if (config.Otel.ExportTraces)
        {
            builder.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => ConfigureExporter(options, config)));
        }

        // Метрики OTel опциональны и additive: собственные метрики приложения уже отдаются
        // через prometheus-net на /metrics, OTel добавляет стандартные server/runtime-серии.
        if (config.Otel.ExportMetrics)
        {
            builder.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options => ConfigureExporter(options, config)));
        }

        return services;
    }

    /// <summary>Имя сервиса для ресурса OTel (<c>service.name</c>).</summary>
    internal static string ResolveServiceName(Config config)
    {
        var name = config.Otel.ServiceName.Trim();
        return name.Length == 0 ? "tg-ws-proxy" : name;
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Config config)
    {
        if (!string.IsNullOrWhiteSpace(config.Otel.Endpoint))
        {
            options.Endpoint = new Uri(config.Otel.Endpoint, UriKind.Absolute);
        }

        // Пустой протокол оставляем как есть: SDK определяет его по endpoint либо читает
        // стандартную переменную OTEL_EXPORTER_OTLP_ENDPOINT / значение по умолчанию.
        options.Protocol = config.Otel.Protocol.Trim() switch
        {
            "grpc" => OtlpExportProtocol.Grpc,
            "http" => OtlpExportProtocol.HttpProtobuf,
            _ => options.Protocol,
        };
    }
}