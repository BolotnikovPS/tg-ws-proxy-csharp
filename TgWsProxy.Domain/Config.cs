namespace TgWsProxy.Domain;

public sealed class Config
{
    public int Port { get; set; } = 1080;
    public string Host { get; set; } = "127.0.0.1";
    public List<string> DcIp { get; } = [];
    public List<AuthCredential> Credentials { get; } = [];
    public bool Verbose { get; set; }
    public string LogPath { get; set; }

    /// <summary>
    /// Разрешает TLS-подключение к серверам с невалидным сертификатом. По умолчанию выключено
    /// (безопасно). Использовать только для диагностики.
    /// </summary>
    public bool AllowInvalidCertificates { get; set; }

    /// <summary>
    /// Максимальный размер полезной нагрузки одного WebSocket-фрейма (в байтах). Используется для
    /// защиты от DoS при чтении фреймов.
    /// </summary>
    public int WsMaxFrameBytes { get; set; } = 1024 * 1024; // 1 MiB

    /// <summary>
    /// Таймаут чтения HTTP-ответа рукопожатия WebSocket (секунды). TCP+TLS ограничены
    /// default(значение, 10).
    /// </summary>
    public int WsConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Размер SO_RCVBUF/SO_SNDBUF для клиентского SOCKS-сокета и исходящего WSS (килобайты, минимум 4).
    /// </summary>
    public int SocketBufferKb { get; set; } = 256;

    /// <summary>
    /// Максимум заранее открытых WS на пару (DC, media); 0 отключает пул.
    /// </summary>
    public int WsPoolSize { get; set; } = 0;

    /// <summary>
    /// При записи в файл: лимит размера одного файла в МБ перед ротацией; 0 — почасовая ротация
    /// (старое поведение).
    /// </summary>
    public double LogMaxMegabytes { get; set; }

    /// <summary>
    /// Число архивных лог-файлов при ротации по размеру
    /// </summary>
    public int LogRetainedFileCount { get; set; }

    /// <summary>
    /// Разрешает fallback через Cloudflare-proxied WebSocket домены.
    /// </summary>
    public bool CfProxyEnabled { get; set; } = true;

    /// <summary>
    /// Приоритет CF Proxy над TCP fallback (true = CF first, false = TCP first).
    /// </summary>
    public bool CfProxyPriority { get; set; } = true;

    /// <summary>
    /// Базовый домен для CF Proxy fallback (например, pclead.co.uk).
    /// </summary>
    public string CfProxyDomain { get; set; } = "pclead.co.uk";

    /// <summary>
    /// MTProto proxy secrets (32 hex chars каждый). Если пусто — автогенерация одного секрета.
    /// </summary>
    public List<string> Secrets { get; } = [];
}
