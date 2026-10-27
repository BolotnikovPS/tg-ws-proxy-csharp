namespace TgWsProxy.Domain;

/// <summary>
/// Настройки fallback через Cloudflare-proxied WebSocket домены. Вложенный блок <c>cfProxy</c>
/// в config.json.
///
/// Все ключи блока — категория «слушатель»: изменение требует перезапуска слушателя, так как
/// пул исходящих соединений и маршруты fallback пересобираются заново (F-2.11).
/// </summary>
public sealed class CfProxyConfig
{
    /// <summary>Разрешает fallback через Cloudflare-proxied WebSocket домены.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>HTTP/2 мультиплексирование media-трафика через Cloudflare (по умолчанию включено).</summary>
    public bool H2Media { get; set; } = true;

    /// <summary>
    /// Пользовательские CF-proxy домены для H2 media и WS fallback. Пусто — используется
    /// встроенный пул с периодическим обновлением с GitHub.
    /// </summary>
    public List<string> Domains { get; } = [];

    /// <summary>Домены Cloudflare Worker (пробуются раньше остальных методов fallback).</summary>
    public List<string> WorkerDomains { get; } = [];
}