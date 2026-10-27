namespace TgWsProxy.Domain;

/// <summary>
/// Настройки экспорта телеметрии по OTLP (OpenTelemetry). Вложенный блок <c>otel</c> в config.json.
///
/// Трассы и метрики экспортируются через OpenTelemetry SDK, логи — через
/// <c>Serilog.Sinks.OpenTelemetry</c> (см. хост). Все ключи блока — «только старт»: SDK и синки
/// собираются один раз при запуске процесса и на лету не переключаются.
/// </summary>
public sealed class OtelConfig
{
    /// <summary>Включает экспорт телеметрии по OTLP. По умолчанию выключено и ни на что не влияет.</summary>
    public bool Enabled { get; set; }

    /// <summary>Имя сервиса в ресурсе OTel (<c>service.name</c>).</summary>
    public string ServiceName { get; set; } = "tg-ws-proxy";

    /// <summary>
    /// OTLP-endpoint, например <c>http://localhost:4318</c>. Пусто — используется значение по
    /// умолчанию SDK либо переменная окружения <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Протокол OTLP: <c>grpc</c> (порт 4317), <c>http</c> (порт 4318) либо пусто (автоопределение).
    /// </summary>
    public string Protocol { get; set; } = "";

    /// <summary>Экспортировать трассы (ASP.NET Core + исходящий HttpClient).</summary>
    public bool ExportTraces { get; set; } = true;

    /// <summary>Экспортировать логи через Serilog.Sinks.OpenTelemetry.</summary>
    public bool ExportLogs { get; set; } = true;

    /// <summary>
    /// Экспортировать метрики OTel (ASP.NET Core + runtime) в дополнение к Prometheus-метрикам
    /// приложения. По умолчанию выключено.
    /// </summary>
    public bool ExportMetrics { get; set; }
}