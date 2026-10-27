namespace TgWsProxy.Domain;

public sealed class Config
{
    /// <summary>
    /// Ядро MTProto-прокси (вложенный блок <c>mtproto</c>): привязка слушателя, внешний адрес
    /// ссылок <c>t.me/proxy</c>, DC-апстримы и транспорт WSS-пула.
    /// Категории ключей блока см. <see cref="MtProtoConfig"/>.
    /// </summary>
    public MtProtoConfig MtProto { get; set; } = new MtProtoConfig();

    /// <summary>
    /// Токены доступа прокси (F-8.1): именованные MTProto-секреты по одному на устройство или
    /// группу устройств. Вынесены на верхний уровень конфигурации (ключ <c>tokens</c>), т.к.
    /// применяются и в MTProto-прокси, и в WEB proxy (browser-relay). Горячий ключ — новый набор
    /// применяется без перезапуска слушателя (F-8.9). Коллекция только для чтения: список
    /// заменяется целиком.
    /// </summary>
    public List<ProxyToken> Tokens { get; } = [];

    /// <summary>
    /// Разрешённые IP/CIDR клиентов. Пусто — пускаем всех. Расширение порта, в upstream отсутствует.
    /// </summary>
    public List<string> AllowIp { get; } = [];

    /// <summary>
    /// Запрещённые IP/CIDR клиентов. Пусто — никого не блокируем. Deny имеет приоритет над allow.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public List<string> DenyIp { get; } = [];

    public bool Verbose { get; set; }

    /// <summary>
    /// Максимум одновременных клиентских сессий. 0 — без ограничения. Защита от исчерпания
    /// сокетов: при достижении лимита новые соединения отклоняются до рукопожатия.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Максимум новых подключений с одного IP за окно <see cref="RateLimitWindowSeconds"/>.
    /// 0 — ограничение выключено. Расширение порта, в upstream отсутствует.
    /// </summary>
    public int RateLimitPerIp { get; set; }

    /// <summary>
    /// Размер окна ограничения частоты подключений в секундах. Действует только при
    /// <see cref="RateLimitPerIp"/> больше нуля.
    /// </summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Bearer-токен для служебного Web API. Пусто — аутентификация выключена и эндпоинты
    /// открыты всем, кто может достучаться до веб-сервера. Расширение порта, в upstream отсутствует.
    /// </summary>
    public string WebApiToken { get; set; } = "";

    /// <summary>
    /// Статистика по источникам трафика — пары «IP + токен» (вложенный блок <c>stats</c>, F-4).
    /// Все ключи блока — «горячо»; см. <see cref="StatsConfig"/>.
    /// </summary>
    public StatsConfig Stats { get; set; } = new StatsConfig();

    /// <summary>
    /// Сколько пар (IP, токен) выгружать в Prometheus per-IP серии: отбираются по суммарному
    /// трафику, чтобы не раздувать кардинальность (F-9.6, N-12). 0 — per-IP серии не выгружаются.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public int PrometheusTopPeers { get; set; } = 200;

    /// <summary>
    /// Отдавать ли Swagger UI (<c>/swagger</c>) и OpenAPI-документ
    /// (<c>/swagger/v1/swagger.json</c>). Маршруты регистрируются до сборки хоста,
    /// поэтому ключ стартовый: изменение требует перезапуска процесса (F-1.11).
    /// </summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// Окно временного ряда для графиков на странице, секунды. Значение по умолчанию для
    /// <c>GET /api/metrics/timeseries</c>, если параметр <c>window</c> не задан.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public int TimeseriesWindowSeconds { get; set; } = 900;

    /// <summary>
    /// Шаг сэмплирования временного ряда, секунды. Значение по умолчанию для
    /// <c>GET /api/metrics/timeseries</c>, если параметр <c>step</c> не задан.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public int TimeseriesStepSeconds { get; set; } = 5;

    /// <summary>
    /// Настройки записи лога в файл (вложенный блок <c>logs</c>). Все ключи блока — «только старт»:
    /// конвейер Serilog собирается один раз при запуске процесса и на лету не перенастраивается.
    /// </summary>
    public LogsConfig Logs { get; set; } = new LogsConfig();

    /// <summary>
    /// Настройки fallback через Cloudflare (вложенный блок <c>cfProxy</c>). Все ключи блока —
    /// категория «слушатель»: изменение требует перезапуска слушателя (F-2.11).
    /// </summary>
    public CfProxyConfig CfProxy { get; set; } = new CfProxyConfig();

    /// <summary>
    /// Использовать порт 80 вместо 443 для CF-proxy и CF-worker соединений.
    /// </summary>
    public bool DisableSecure { get; set; }

    /// <summary>
    /// Домен для Fake TLS (ee-secret) маскировки. Пусто — маскировка выключена.
    /// </summary>
    public string FakeTlsDomain { get; set; } = "";

    /// <summary>
    /// Принимать заголовок PROXY protocol v1 (для работы за nginx/haproxy).
    /// </summary>
    public bool ProxyProtocol { get; set; }

    /// <summary>
    /// Принудительно направлять весь трафик в тестовые DC Telegram.
    /// </summary>
    public bool ForceTestDc { get; set; }

    /// <summary>
    /// Настройки WEB proxy (browser-relay) bridge-листенера (вложенный блок <c>webProxy</c>).
    /// Часть ключей — «только старт» (привязка и TLS), часть — «горячо» (домен ссылки, носитель,
    /// лимиты ресурсов); см. <see cref="WebProxyConfig"/>.
    /// </summary>
    public WebProxyConfig WebProxy { get; set; } = new WebProxyConfig();

    /// <summary>
    /// Настройки OpenTelemetry (OTLP): трассы и метрики — через OpenTelemetry SDK, логи — через
    /// <c>Serilog.Sinks.OpenTelemetry</c>. Вложенный блок <c>otel</c> в config.json. По умолчанию
    /// выключено и не влияет на поведение. Все ключи блока — «только старт».
    /// </summary>
    public OtelConfig Otel { get; set; } = new OtelConfig();

    /// <summary>
    /// HTTP/2 доступен только при включённом CF fallback, secure-соединениях и без test DC.
    /// </summary>
    public bool H2Enabled => CfProxy.H2Media && CfProxy.Enabled && !DisableSecure && !ForceTestDc;

    /// <summary>
    /// Точка доступа для ссылок <c>t.me/proxy</c>: только <see cref="MtProtoConfig.PublicHost"/> —
    /// слушатель всегда привязан ко всем интерфейсам, поэтому служебный адрес больше не хранится.
    /// <c>null</c> — <see cref="MtProtoConfig.PublicHost"/> не задан или некорректен
    /// (валидатор такой конфиг не пропустит).
    /// </summary>
    public PublicEndpoint? ResolvePublicEndpoint()
    {
        var mt = MtProto;
        var configured = mt.PublicHost?.Trim() ?? "";
        return configured.Length > 0
            && PublicEndpoint.TryParse(configured, mt.Port, out var endpoint)
                ? endpoint
                : null;
    }
}