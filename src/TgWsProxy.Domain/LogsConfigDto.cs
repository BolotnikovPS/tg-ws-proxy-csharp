namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление вложенного блока <c>logs</c> для <c>config.json</c> и Web API.
/// <c>null</c> на уровне блока — не менять его; подчинённые ключи также nullable (отсутствующий
/// ключ остаётся как есть). Все ключи блока — «только старт» (см. <see cref="LogsConfig"/>).
/// </summary>
public sealed class LogsConfigDto
{
    public string? Path { get; set; }

    public double? MaxMegabytes { get; set; }

    public int? RetainedFileCount { get; set; }
}