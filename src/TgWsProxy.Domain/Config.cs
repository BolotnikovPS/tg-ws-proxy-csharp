namespace TgWsProxy.Domain;

public sealed class Config
{
    public int Port { get; set; } = 1443;
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// Внешний адрес прокси для ссылок <c>https://t.me/proxy</c> (F-8.8). Нужен, когда
    /// <see cref="Host"/> — служебный адрес: в контейнере это <c>0.0.0.0</c>, снаружи такой
    /// адрес недостижим, и ссылку не из чего собрать. Допускается форма <c>host</c>,
    /// <c>host:port</c> и <c>[IPv6]:port</c>. Пусто — берётся <see cref="Host"/> и
    /// <see cref="Port"/>. На привязку слушателя не влияет.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Целевые IP для DC в формате DC:IP. Пусто — используются дефолты upstream
    /// (2:149.154.167.220, 4:149.154.167.220). Явное указание --dc-ip без значения очищает список,
    /// что полностью отключает WS-пул.
    /// </summary>
    public List<string> DcIp { get; } = [];

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
    /// Путь к файлу лога с ротацией. Пусто — только консоль.
    /// Инициализируется пустой строкой, иначе значение остаётся <c>null</c> и
    /// <c>GET /api/config</c> отдаёт <c>logPath: null</c>, а <c>PUT /api/config</c>
    /// видит ложное изменение «null → ""» и требует перезапуск (F-2.11).
    /// </summary>
    public string LogPath { get; set; } = "";

    /// <summary>
    /// Разрешает TLS-подключение к серверам с невалидным сертификатом. По умолчанию выключено
    /// (безопасно). Использовать только для диагностики. Расширение порта, в upstream отсутствует.
    /// </summary>
    public bool AllowInvalidCertificates { get; set; }

    /// <summary>
    /// Максимальный размер полезной нагрузки одного WebSocket-фрейма (в байтах). Используется для
    /// защиты от DoS при чтении фреймов. Расширение порта, в upstream отсутствует.
    /// </summary>
    public int WsMaxFrameBytes { get; set; } = 16 * 1024 * 1024; // 16 MiB

    /// <summary>
    /// Таймаут чтения HTTP-ответа рукопожатия WebSocket (секунды). TCP+TLS ограничены
    /// default(значение, 10). Расширение порта, в upstream отсутствует.
    /// </summary>
    public int WsConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Размер SO_RCVBUF/SO_SNDBUF для клиентского сокета и исходящего WSS (килобайты, минимум 4).
    /// </summary>
    public int SocketBufferKb { get; set; } = 256;

    /// <summary>
    /// Максимум заранее открытых WS на пару (DC, media); 0 отключает пул прямых WS-соединений.
    /// </summary>
    public int WsPoolSize { get; set; } = 4;

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
    /// Порт HTTP-сервера служебных эндпоинтов <c>/metrics</c> (Prometheus) и <c>/healthz</c>.
    /// 0 — сервер мониторинга выключен. Расширение порта, в upstream отсутствует.
    /// </summary>
    public int MetricsPort { get; set; } = 8081;

    /// <summary>
    /// Адрес прослушивания сервера мониторинга. По умолчанию только loopback: эндпоинты не
    /// аутентифицированы, поэтому наружу их следует выставлять осознанно.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public string MetricsHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Bearer-токен для служебного Web API. Пусто — аутентификация выключена и эндпоинты
    /// открыты всем, кто может достучаться до <see cref="MetricsHost"/> (по умолчанию loopback).
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public string WebApiToken { get; set; } = "";

    /// <summary>
    /// Включает учёт статистики по каждому IP-адресу (требование F-4). Выключение экономит
    /// память на инсталляциях с большим числом адресов: <c>/api/per-ip</c> вернёт пустой список.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public bool EnablePerIpStats { get; set; } = true;

    /// <summary>
    /// Время жизни записи per-IP в секундах: записи без активных соединений удаляются фоновой
    /// очисткой. 0 — записи живут до перезапуска. Расширение порта, в upstream отсутствует.
    /// </summary>
    public int PerIpStatsTtlSeconds { get; set; } = 1800;

    /// <summary>
    /// Верхняя граница числа отслеживаемых адресов в реестре per-IP статистики. Новые адреса
    /// сверх предела не учитываются (растёт <c>peerTrackingOverflow</c>) — защита от сканирования портов.
    /// Расширение порта, в upstream отсутствует.
    /// </summary>
    public int PerIpStatsMaxPeers { get; set; } = 100_000;

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
    /// При записи в файл: лимит размера одного файла в МБ перед ротацией (минимум 32 КБ).
    /// </summary>
    public double LogMaxMegabytes { get; set; } = 5;

    /// <summary>
    /// Число архивных лог-файлов при ротации (минимум 1 — ротация без бэкапа не ограничивает размер).
    /// </summary>
    public int LogRetainedFileCount { get; set; } = 1;

    /// <summary>
    /// Разрешает fallback через Cloudflare-proxied WebSocket домены.
    /// </summary>
    public bool CfProxyEnabled { get; set; } = true;

    /// <summary>
    /// Пользовательские CF-proxy домены для H2 media и WS fallback. Пусто — используется
    /// встроенный пул с периодическим обновлением с GitHub.
    /// </summary>
    public List<string> CfProxyDomains { get; } = [];

    /// <summary>
    /// Домены Cloudflare Worker (пробуются раньше остальных методов fallback).
    /// </summary>
    public List<string> CfProxyWorkerDomains { get; } = [];

    /// <summary>
    /// HTTP/2 мультиплексирование media-трафика через Cloudflare (по умолчанию включено).
    /// </summary>
    public bool CfProxyH2Media { get; set; } = true;

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
    /// Токены доступа прокси (F-8.1): именованные MTProto-секреты, по одному на устройство или
    /// группу устройств. Заменяют единственный <c>secret</c> прежних версий, который переносится
    /// сюда при первом запуске (F-8.3). Коллекция только для чтения: список заменяется целиком,
    /// чтобы ссылка на конфигурацию не менялась — так же устроены остальные коллекции.
    /// </summary>
    public List<ProxyToken> Tokens { get; } = [];

    /// <summary>
    /// Включает WEB proxy (browser-relay) bridge-листенер (F-W.1). По умолчанию выключен:
    /// при выключении bridge не занимает порт и не регистрирует маршруты.
    /// Категория «слушатель»: изменение требует перепривязки bridge-листенера.
    /// </summary>
    public bool WebProxyEnabled { get; set; }

    /// <summary>
    /// Адрес привязки bridge-листенера (HTTPS/WS). По умолчанию все интерфейсы: bridge
    /// обслуживает публичный браузерный трафик, а не только loopback. Стартовый ключ.
    /// </summary>
    public string WebProxyHost { get; set; } = "0.0.0.0";

    /// <summary>
    /// Порт bridge-листенера (HTTPS). Должен отличаться от <see cref="Port"/> (MTProxy)
    /// и <see cref="MetricsPort"/> (панель) — F-W.5. Стартовый ключ.
    /// </summary>
    public int WebProxyPort { get; set; } = 443;

    /// <summary>
    /// Базовый путь bridge-сайта: пусто — корень, иначе префикс вида «v1». Участвует в
    /// контексте HMAC bridge-capability (v2). Стартовый ключ.
    /// </summary>
    public string WebProxyBasePath { get; set; } = "";

    /// <summary>
    /// Внешний домен для bridge-ссылки, когда TLS терминируется за reverse-proxy.
    /// Пусто — используется <see cref="WebProxyHost"/>. Горячий ключ.
    /// </summary>
    public string WebProxyPublicHost { get; set; } = "";

    /// <summary>
    /// Путь к PFX/сертификату для TLS bridge-листенера. Обязателен при
    /// <see cref="WebProxyEnabled"/> (N-W.1). Стартовый ключ.
    /// </summary>
    public string WebProxyCertPath { get; set; } = "";

    /// <summary>
    /// Пароль PFX-сертификата bridge. Пусто — сертификат без пароля. В <c>GET /api/config</c>
    /// маскируется. Стартовый ключ.
    /// </summary>
    public string WebProxyCertPassword { get; set; } = "";

    /// <summary>
    /// Носитель (carrier) bridge-сессии: <c>https</c> — HTTPS long-poll (<c>POST /api/v1/up</c> и
    /// <c>POST /api/v1/down</c>), <c>websocket</c> — один WebSocket <c>GET /api/v1/ws</c> с
    /// подпротоколом <c>tproxy-v1.&lt;token&gt;</c>. Значение попадает в bridge-страницу и в
    /// заголовок <c>X-Carrier-Mode</c>; клиент обязан использовать именно этот режим (F-W.24).
    /// Горячий ключ.
    /// </summary>
    public string WebProxyCarrierMode { get; set; } = "https";

    /// <summary>
    /// Предел одновременных relay-сессий WEB proxy. Сверх предела <c>POST /api/v1/session</c>
    /// отвечает <c>503</c> с <c>Retry-After</c>. Горячий ключ.
    /// </summary>
    public int WebProxyMaxSessions { get; set; } = 128;

    /// <summary>
    /// Предел одновременных relay-сессий с одного IP клиента. <c>0</c> — без ограничения.
    /// Горячий ключ.
    /// </summary>
    public int WebProxyMaxSessionsPerIp { get; set; }

    /// <summary>
    /// Предел одновременно открытых потоков (stream) в одной relay-сессии. OPEN сверх предела
    /// отклоняется кадром <c>CLOSE</c>. Горячий ключ.
    /// </summary>
    public int WebProxyMaxStreamsPerSession { get; set; } = 128;

    /// <summary>
    /// Предел одновременно открытых потоков во всех relay-сессиях WEB proxy. Горячий ключ.
    /// </summary>
    public int WebProxyMaxStreams { get; set; } = 4096;

    /// <summary>
    /// Предел объёма исходящей очереди одной сессии (несобранные батчи). При превышении сессия
    /// закрывается: клиент обязан забирать нисходящие батчи. Горячий ключ.
    /// </summary>
    public int WebProxyMaxPendingBytesPerSession { get; set; } = 32 * 1024 * 1024;

    /// <summary>
    /// Предел числа кадров в исходящей очереди одной сессии. Горячий ключ.
    /// </summary>
    public int WebProxyMaxPendingItemsPerSession { get; set; } = 16384;

    /// <summary>
    /// HTTP/2 доступен только при включённом CF fallback, secure-соединениях и без test DC.
    /// </summary>
    public bool H2Enabled => CfProxyH2Media && CfProxyEnabled && !DisableSecure && !ForceTestDc;

    /// <summary>
    /// Точка доступа для ссылок <c>t.me/proxy</c>: <see cref="PublicHost"/>, если он задан,
    /// иначе <see cref="Host"/>. <c>null</c> — внешний адрес не определён: служебный
    /// <see cref="Host"/> (<c>0.0.0.0</c>, <c>::</c>) без явного <see cref="PublicHost"/>
    /// либо некорректный <see cref="PublicHost"/> (валидатор такой конфиг не пропустит).
    /// </summary>
    public PublicEndpoint? ResolvePublicEndpoint()
    {
        var configured = PublicHost?.Trim() ?? "";
        if (configured.Length > 0)
        {
            return PublicEndpoint.TryParse(configured, Port, out var endpoint) ? endpoint : null;
        }

        return PublicEndpoint.TryParse(Host, Port, out var fallback) ? fallback : null;
    }
}
