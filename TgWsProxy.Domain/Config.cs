namespace TgWsProxy.Domain;

public sealed class Config
{
    public int Port { get; set; } = 1080;
    public string Host { get; set; } = "127.0.0.1";
    public List<string> DcIp { get; } = [];
    public List<AuthCredential> Credentials { get; } = [];
    public bool Verbose { get; set; }
    public string LogPath { get; set; }

    /// <summary>Таймаут чтения HTTP-ответа рукопожатия WebSocket (секунды). TCP+TLS ограничены default(значение, 10).</summary>
    public int WsConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Размер SO_RCVBUF/SO_SNDBUF для клиентского SOCKS-сокета и исходящего WSS (килобайты, минимум 4).</summary>
    public int SocketBufferKb { get; set; } = 256;

    /// <summary>Максимум заранее открытых WS на пару (DC, media); 0 отключает пул.</summary>
    public int WsPoolSize { get; set; } = 4;

    /// <summary>При записи в файл: лимит размера одного файла в МБ перед ротацией; 0 — почасовая ротация (старое поведение).</summary>
    public double LogMaxMegabytes { get; set; }

    /// <summary>Число архивных лог-файлов при ротации по размеру (как --log-backups в Python).</summary>
    public int LogRetainedFileCount { get; set; }
}
