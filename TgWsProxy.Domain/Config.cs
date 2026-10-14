namespace TgWsProxy.Domain;

public sealed class Config
{
    public int Port { get; set; } = 1443;
    public string Host { get; set; } = "127.0.0.1";

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
    /// </summary>
    public string LogPath { get; set; }

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
    /// MTProto proxy secret (32 hex chars). Если пусто — автогенерация при старте.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// HTTP/2 доступен только при включённом CF fallback, secure-соединениях и без test DC.
    /// </summary>
    public bool H2Enabled => CfProxyH2Media && CfProxyEnabled && !DisableSecure && !ForceTestDc;
}
