namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление вложенного блока <c>stats</c>. <c>null</c> на уровне блока — не менять
/// его; подчинённые ключи также nullable. Все ключи блока — «горячо» (см. <see cref="StatsConfig"/>).
/// </summary>
public sealed class StatsConfigDto
{
    public bool? Enabled { get; set; }

    public int? TtlSeconds { get; set; }

    public int? MaxPeers { get; set; }
}