namespace TgWsProxy.Domain;

/// <summary>
/// Ядро MTProto-прокси (вложенный блок <c>mtproto</c>): порт слушателя (адрес всегда все интерфейсы),
/// внешний адрес для ссылок <c>t.me/proxy</c>, список DC и транспортные ключи WSS-пула.
/// 
/// Категории ключей (F-2.11):
/// <list type="bullet">
///   <item><description><see cref="Port"/> — «только старт» (привязка слушателя; адрес всегда все интерфейсы);</description></item>
///   <item><description><see cref="PublicHost"/> — «горячо»;</description></item>
///   <item><description><see cref="DcIp"/> и транспорт к DC (<see cref="AllowInvalidCertificates"/>,
///   <see cref="WsMaxFrameBytes"/>, <see cref="WsConnectTimeoutSeconds"/>, <see cref="SocketBufferKb"/>,
///   <see cref="WsPoolSize"/>) — «слушатель» (требуют перезапуска слушателя, активные сессии рвутся).</description></item>
/// </list>
/// </summary>
public sealed class MtProtoConfig
{
    /// <summary>Порт слушателя MTProto-прокси. Ключ «только старт».</summary>
    public int Port { get; set; } = 1443;

    /// <summary>
    /// Внешний адрес для ссылок <c>https://t.me/proxy</c> (F-8.8). Формы <c>host</c>,
    /// <c>host:port</c>, <c>[IPv6]:port</c>. Пусто — внешний адрес не определён (слушатель
    /// всегда привязан ко всем интерфейсам), ссылка не строится. Горячий ключ.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Целевые IP для DC в формате <c>DC:IP</c>. Пусто — дефолты upstream. Категория «слушатель».
    /// </summary>
    public List<string> DcIp { get; } = [];

    /// <summary>Разрешает TLS-подключение к DC с невалидным сертификатом (диагностика). «Слушатель».</summary>
    public bool AllowInvalidCertificates { get; set; }

    /// <summary>Максимальный размер полезной нагрузки одного WebSocket-фрейма (байты). «Слушатель».</summary>
    public int WsMaxFrameBytes { get; set; } = 16 * 1024 * 1024; // 16 MiB

    /// <summary>Таймаут чтения HTTP-ответа рукопожатия WebSocket (секунды). «Слушатель».</summary>
    public int WsConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Размер SO_RCVBUF/SO_SNDBUF для клиентского сокета и исходящего WSS (КБ, минимум 4). «Слушатель».</summary>
    public int SocketBufferKb { get; set; } = 256;

    /// <summary>Максимум заранее открытых WS на пару (DC, media); 0 отключает пул. «Слушатель».</summary>
    public int WsPoolSize { get; set; } = 4;
}