using System.Text.Json.Serialization;

namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление <see cref="Config"/> для файла <c>config.json</c> и Web API.
///
/// Все скалярные поля намеренно nullable: это позволяет отличить «ключ отсутствует в файле»
/// (<c>null</c> — значение остаётся от предыдущего источника) от «ключ задан явно равным
/// значению по умолчанию». Без этого нельзя реализовать приоритет источников
/// умолчания → config.json → аргументы командной строки.
///
/// Коллекции тоже nullable: <c>null</c> — не трогать, пустой массив — очистить список
/// (для <c>mtproto.dcIp</c> это отключает пул WS-соединений).
///
/// Вычисляемые свойства (<see cref="Config.H2Enabled"/>) в DTO отсутствуют: они не
/// конфигурируются, а являются производными. Попытка указать их в файле приводит к ошибке
/// «неизвестный ключ».
/// </summary>
public sealed class ConfigDto
{
    /// <summary>
    /// Ядро MTProto-прокси — вложенный блок <c>mtproto</c>. <c>null</c> — не менять блок; внутри
    /// него подчинённые ключи также nullable (отсутствующий ключ остаётся как есть, пустой массив —
    /// очистить коллекцию). Категории ключей см. <see cref="MtProtoConfig"/>.
    /// </summary>
    [JsonPropertyName("mtproto")]
    public MtProtoConfigDto? MtProto { get; set; }

    /// <summary>
    /// Токены доступа прокси (F-8.1) — верхнеуровневый ключ <c>tokens</c>. Применяются и в
    /// MTProto-прокси, и в WEB proxy. <c>null</c> — не трогать, пустой массив — очистить список.
    /// </summary>
    public List<ProxyToken>? Tokens { get; set; }

    public List<string>? AllowIp { get; set; }

    public List<string>? DenyIp { get; set; }

    public bool? Verbose { get; set; }

    public int? MaxConnections { get; set; }

    public int? RateLimitPerIp { get; set; }

    public int? RateLimitWindowSeconds { get; set; }

    public string? WebApiToken { get; set; }

    /// <summary>
    /// Статистика по источникам трафика — вложенный блок <c>stats</c>. <c>null</c> — не менять
    /// блок; подчинённые ключи также nullable. Все ключи блока — «горячо» (см. <see cref="StatsConfig"/>).
    /// </summary>
    public StatsConfigDto? Stats { get; set; }

    /// <summary>Сколько пар (IP, токен) выгружать в Prometheus (F-9.6); 0 — не выгружать.</summary>
    public int? PrometheusTopPeers { get; set; }

    /// <summary>
    /// Включён ли Swagger (F-1.11). Стартовый ключ: маршруты OpenAPI регистрируются
    /// до сборки хоста, поэтому на лету не применяется.
    /// </summary>
    public bool? EnableSwagger { get; set; }

    public int? TimeseriesWindowSeconds { get; set; }

    public int? TimeseriesStepSeconds { get; set; }

    /// <summary>
    /// Настройки записи лога в файл — вложенный блок <c>logs</c> в JSON. <c>null</c> — не менять
    /// блок; внутри него подчинённые ключи также nullable (отсутствующий ключ остаётся как есть).
    /// Все ключи блока — «только старт» (см. <see cref="LogsConfig"/>).
    /// </summary>
    public LogsConfigDto? Logs { get; set; }

    /// <summary>
    /// Настройки fallback через Cloudflare — вложенный блок <c>cfProxy</c>. <c>null</c> — не менять
    /// блок; внутри него подчинённые ключи также nullable. Все ключи блока — категория «слушатель».
    /// </summary>
    public CfProxyConfigDto? CfProxy { get; set; }

    public bool? DisableSecure { get; set; }

    public string? FakeTlsDomain { get; set; }

    public bool? ProxyProtocol { get; set; }

    public bool? ForceTestDc { get; set; }

    /// <summary>
    /// Настройки WEB proxy (browser-relay) bridge-листенера — вложенный блок <c>webProxy</c>.
    /// <c>null</c> — не менять блок; внутри него подчинённые ключи также nullable (отсутствующий
    /// ключ остаётся как есть). Часть ключей «только старт», часть — «горячо»; см.
    /// <see cref="WebProxyConfig"/>.
    /// </summary>
    public WebProxyConfigDto? WebProxy { get; set; }

    /// <summary>
    /// Настройки OpenTelemetry (OTLP) — вложенный блок <c>otel</c> в JSON. <c>null</c> — не менять
    /// блок; внутри него подчинённые ключи также nullable (отсутствующий ключ остаётся как есть).
    /// Все ключи блока — «только старт».
    /// </summary>
    public OtelConfigDto? Otel { get; set; }
}