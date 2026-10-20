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
/// (для <c>dcIp</c> это отключает пул WS-соединений).
///
/// Вычисляемые свойства (<see cref="Config.H2Enabled"/>) в DTO отсутствуют: они не
/// конфигурируются, а являются производными. Попытка указать их в файле приводит к ошибке
/// «неизвестный ключ».
/// </summary>
public sealed class ConfigDto
{
    public int? Port { get; set; }

    public string? Host { get; set; }

    /// <summary>
    /// Внешний адрес для ссылок <c>t.me/proxy</c>: <c>host</c>, <c>host:port</c> или
    /// <c>[IPv6]:port</c>. Пусто — используется <see cref="Host"/>. Горячий ключ.
    /// </summary>
    public string? PublicHost { get; set; }

    public List<string>? DcIp { get; set; }

    public List<string>? AllowIp { get; set; }

    public List<string>? DenyIp { get; set; }

    public bool? Verbose { get; set; }

    public string? LogPath { get; set; }

    public bool? AllowInvalidCertificates { get; set; }

    public int? WsMaxFrameBytes { get; set; }

    public int? WsConnectTimeoutSeconds { get; set; }

    public int? SocketBufferKb { get; set; }

    public int? WsPoolSize { get; set; }

    public int? MaxConnections { get; set; }

    public int? RateLimitPerIp { get; set; }

    public int? RateLimitWindowSeconds { get; set; }

    public int? MetricsPort { get; set; }

    public string? MetricsHost { get; set; }

    public string? WebApiToken { get; set; }

    public bool? EnablePerIpStats { get; set; }

    public int? PerIpStatsTtlSeconds { get; set; }

    public int? PerIpStatsMaxPeers { get; set; }

    /// <summary>Сколько пар (IP, токен) выгружать в Prometheus (F-9.6); 0 — не выгружать.</summary>
    public int? PrometheusTopPeers { get; set; }

    /// <summary>
    /// Включён ли Swagger (F-1.11). Стартовый ключ: маршруты OpenAPI регистрируются
    /// до сборки хоста, поэтому на лету не применяется.
    /// </summary>
    public bool? EnableSwagger { get; set; }

    public int? TimeseriesWindowSeconds { get; set; }

    public int? TimeseriesStepSeconds { get; set; }

    public double? LogMaxMegabytes { get; set; }

    public int? LogRetainedFileCount { get; set; }

    public bool? CfProxyEnabled { get; set; }

    public List<string>? CfProxyDomains { get; set; }

    public List<string>? CfProxyWorkerDomains { get; set; }

    public bool? CfProxyH2Media { get; set; }

    public bool? DisableSecure { get; set; }

    public string? FakeTlsDomain { get; set; }

    public bool? ProxyProtocol { get; set; }

    public bool? ForceTestDc { get; set; }

    /// <summary>Включить WEB proxy (browser-relay) bridge-листенер (F-W.1). Категория «слушатель».</summary>
    public bool? WebProxyEnabled { get; set; }

    /// <summary>Адрес привязки bridge-листенера. Стартовый ключ.</summary>
    public string? WebProxyHost { get; set; }

    /// <summary>Порт bridge-листенера (≠ <c>port</c>, ≠ <c>metricsPort</c>, F-W.5). Стартовый ключ.</summary>
    public int? WebProxyPort { get; set; }

    /// <summary>Базовый путь bridge-сайта (<c>""</c> — корень). Стартовый ключ.</summary>
    public string? WebProxyBasePath { get; set; }

    /// <summary>Внешний домен для bridge-ссылки за reverse-proxy. Горячий ключ.</summary>
    public string? WebProxyPublicHost { get; set; }

    /// <summary>PFX/сертификат для TLS bridge-листенера (обязателен при <c>webProxyEnabled</c>). Стартовый ключ.</summary>
    public string? WebProxyCertPath { get; set; }

    /// <summary>Пароль PFX (в GET API маскируется). Стартовый ключ.</summary>
    public string? WebProxyCertPassword { get; set; }

    /// <summary>
    /// Носитель bridge-сессии: <c>https</c> (long-poll) либо <c>websocket</c> (F-W.24).
    /// Горячий ключ.
    /// </summary>
    public string? WebProxyCarrierMode { get; set; }

    /// <summary>Предел одновременных relay-сессий WEB proxy. Горячий ключ.</summary>
    public int? WebProxyMaxSessions { get; set; }

    /// <summary>Предел relay-сессий с одного IP (<c>0</c> — без ограничения). Горячий ключ.</summary>
    public int? WebProxyMaxSessionsPerIp { get; set; }

    /// <summary>Предел потоков в одной relay-сессии. Горячий ключ.</summary>
    public int? WebProxyMaxStreamsPerSession { get; set; }

    /// <summary>Предел потоков во всех relay-сессиях. Горячий ключ.</summary>
    public int? WebProxyMaxStreams { get; set; }

    /// <summary>Предел исходящей очереди сессии в байтах. Горячий ключ.</summary>
    public int? WebProxyMaxPendingBytesPerSession { get; set; }

    /// <summary>Предел исходящей очереди сессии в кадрах. Горячий ключ.</summary>
    public int? WebProxyMaxPendingItemsPerSession { get; set; }

    /// <summary>
    /// Токены доступа прокси (F-8.1). Прежний единственный ключ <c>secret</c> отсутствует:
    /// он переносится в этот список при чтении файла (F-8.3),
    /// а для записи всегда используется полный набор токенов.
    /// </summary>
    public List<ProxyToken>? Tokens { get; set; }
}
