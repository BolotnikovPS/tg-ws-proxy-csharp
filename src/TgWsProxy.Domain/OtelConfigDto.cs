namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление <see cref="OtelConfig"/> для файла <c>config.json</c> и Web API.
///
/// Скалярные поля намеренно nullable: это позволяет отличить «ключ отсутствует в файле»
/// (<c>null</c> — значение остаётся от предыдущего источника) от «ключ задан явно равным
/// значению по умолчанию», по тому же принципу, что и <see cref="ConfigDto"/>.
/// </summary>
public sealed class OtelConfigDto
{
    public bool? Enabled { get; set; }

    public string? ServiceName { get; set; }

    public string? Endpoint { get; set; }

    public string? Protocol { get; set; }

    public bool? ExportTraces { get; set; }

    public bool? ExportLogs { get; set; }

    public bool? ExportMetrics { get; set; }
}