namespace TgWsProxy.Domain;

/// <summary>
/// Настройки WEB proxy (browser-relay) bridge-листенера. Вложенный блок <c>webProxy</c>
/// в config.json.
///
/// Bridge — отдельный HTTPS/WS-листенер (Kestrel endpoint) для браузерных клиентов
/// Telegram Desktop. Ключи <c>enabled</c>, <c>port</c>, <c>basePath</c>,
/// <c>certPath</c>, <c>certPassword</c> — «только старт» (привязка и TLS настраиваются при
/// запуске, адрес всегда все интерфейсы); <c>publicHost</c>, <c>carrierMode</c> и лимиты
/// ресурсов — «горячо».
/// </summary>
public sealed class WebProxyConfig
{
    /// <summary>
    /// Включает bridge-листенер (F-W.1). По умолчанию выключен: при выключении bridge
    /// не занимает порт и не регистрирует маршруты. Категория «слушатель»/«только старт».
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Порт bridge-листенера (HTTPS). Должен отличаться от <see cref="Config.Port"/> (MTProxy)
    /// и от порта панели (80) — F-W.5. Стартовый ключ. Адрес привязки всегда все интерфейсы.
    /// </summary>
    public int Port { get; set; } = 443;

    /// <summary>
    /// Базовый путь bridge-сайта: пусто — корень, иначе префикс вида «v1». Участвует в
    /// контексте HMAC bridge-capability (v2). Стартовый ключ.
    /// </summary>
    public string BasePath { get; set; } = "";

    /// <summary>
    /// Внешний домен для bridge-ссылки, когда TLS терминируется за reverse-proxy.
    /// Пусто — bridge-ссылка не строится (адрес привязки служебный). Горячий ключ.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Путь к PFX/сертификату для TLS bridge-листенера. Обязателен при
    /// <see cref="Enabled"/> (N-W.1). Стартовый ключ.
    /// </summary>
    public string CertPath { get; set; } = "";

    /// <summary>
    /// Пароль PFX-сертификата bridge. Пусто — сертификат без пароля. В <c>GET /api/config</c>
    /// маскируется. Стартовый ключ.
    /// </summary>
    public string CertPassword { get; set; } = "";

    /// <summary>
    /// Носитель (carrier) bridge-сессии: <c>https</c> — HTTPS long-poll (<c>POST /api/v1/up</c> и
    /// <c>POST /api/v1/down</c>), <c>websocket</c> — один WebSocket <c>GET /api/v1/ws</c> с
    /// подпротоколом <c>tproxy-v1.&lt;token&gt;</c>. Значение попадает в bridge-страницу и в
    /// заголовок <c>X-Carrier-Mode</c> (F-W.24). Горячий ключ.
    /// </summary>
    public string CarrierMode { get; set; } = "https";

    /// <summary>
    /// Предел одновременных relay-сессий WEB proxy. Сверх предела <c>POST /api/v1/session</c>
    /// отвечает <c>503</c> с <c>Retry-After</c>. Горячий ключ.
    /// </summary>
    public int MaxSessions { get; set; } = 128;

    /// <summary>
    /// Предел одновременных relay-сессий с одного IP клиента. <c>0</c> — без ограничения.
    /// Горячий ключ.
    /// </summary>
    public int MaxSessionsPerIp { get; set; }

    /// <summary>
    /// Предел одновременно открытых потоков (stream) в одной relay-сессии. OPEN сверх предела
    /// отклоняется кадром <c>CLOSE</c>. Горячий ключ.
    /// </summary>
    public int MaxStreamsPerSession { get; set; } = 128;

    /// <summary>Предел одновременно открытых потоков во всех relay-сессиях WEB proxy. Горячий ключ.</summary>
    public int MaxStreams { get; set; } = 4096;

    /// <summary>
    /// Предел объёма исходящей очереди одной сессии (несобранные батчи). При превышении сессия
    /// закрывается: клиент обязан забирать нисходящие батчи. Горячий ключ.
    /// </summary>
    public int MaxPendingBytesPerSession { get; set; } = 32 * 1024 * 1024;

    /// <summary>Предел числа кадров в исходящей очереди одной сессии. Горячий ключ.</summary>
    public int MaxPendingItemsPerSession { get; set; } = 16384;
}