namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление <see cref="WebProxyConfig"/> для файла <c>config.json</c> и Web API.
///
/// Скалярные поля намеренно nullable: это позволяет отличить «ключ отсутствует в файле»
/// (<c>null</c> — значение остаётся от предыдущего источника) от «ключ задан явно равным
/// значению по умолчанию», по тому же принципу, что и <see cref="ConfigDto"/>.
/// </summary>
public sealed class WebProxyConfigDto
{
    public bool? Enabled { get; set; }

    public int? Port { get; set; }

    public string? BasePath { get; set; }

    public string? PublicHost { get; set; }

    public string? CertPath { get; set; }

    public string? CertPassword { get; set; }

    public string? CarrierMode { get; set; }

    public int? MaxSessions { get; set; }

    public int? MaxSessionsPerIp { get; set; }

    public int? MaxStreamsPerSession { get; set; }

    public int? MaxStreams { get; set; }

    public int? MaxPendingBytesPerSession { get; set; }

    public int? MaxPendingItemsPerSession { get; set; }
}