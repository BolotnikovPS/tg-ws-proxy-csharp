using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Сводит снимок реестра «IP, токен» в запрошенный разрез (F-9.4):
/// <c>pair</c> — как есть, <c>ip</c> — сумма по адресу, <c>token</c> — сумма по токену.
/// </summary>
internal static class PeerStatsAggregator
{
    /// <summary>Разрез по паре «адрес, токен» (по умолчанию).</summary>
    public const string GroupPair = "pair";

    /// <summary>Разрез по адресу: токен не учитывается.</summary>
    public const string GroupIp = "ip";

    /// <summary>Разрез по токену: адрес не учитывается.</summary>
    public const string GroupToken = "token";

    /// <summary>Нормализует параметр <c>group</c>; неизвестное значение трактуется как <c>pair</c>.</summary>
    public static string NormalizeGroup(string? group) => group?.Trim().ToLowerInvariant() switch
    {
        GroupIp => GroupIp,
        GroupToken => GroupToken,
        _ => GroupPair,
    };

    /// <summary>Сводит строки снимка в выбранный разрез.</summary>
    /// <param name="rows">Снимок реестра.</param>
    /// <param name="group">Разрез: <c>ip</c>, <c>token</c> или <c>pair</c>.</param>
    public static IReadOnlyList<PeerStatsSnapshot> Aggregate(IEnumerable<PeerStatsSnapshot> rows, string? group)
    {
        var mode = NormalizeGroup(group);
        var list = rows as IReadOnlyList<PeerStatsSnapshot> ?? rows.ToList();
        if (mode == GroupPair)
        {
            return list;
        }

        // В разрезе по адресу токен схлопывается, в разрезе по токену — адрес.
        var groups = mode == GroupIp
            ? list.GroupBy(static row => row.Ip, StringComparer.Ordinal)
            : list.GroupBy(static row => row.TokenId ?? "", StringComparer.Ordinal);

        var result = new List<PeerStatsSnapshot>(list.Count);
        foreach (var bucket in groups)
        {
            var ip = mode == GroupIp ? bucket.Key : "";
            var tokenId = mode == GroupIp || bucket.Key.Length == 0 ? null : bucket.Key;
            var tokenName = mode == GroupIp
                ? null
                : bucket.Select(static row => row.TokenName).FirstOrDefault(static name => !string.IsNullOrEmpty(name));

            result.Add(new PeerStatsSnapshot(
                ip,
                tokenId,
                tokenName,
                bucket.Sum(static row => row.ActiveConnections),
                bucket.Sum(static row => row.TotalConnections),
                bucket.Sum(static row => row.BlockedConnections),
                bucket.Sum(static row => row.LimitedConnections),
                bucket.Sum(static row => row.RateLimitedConnections),
                bucket.Sum(static row => row.BytesUp),
                bucket.Sum(static row => row.BytesDown),
                bucket.Sum(static row => row.WsErrors),
                bucket.Sum(static row => row.PoolHits),
                bucket.Sum(static row => row.PoolMisses),
                bucket.Min(static row => row.FirstSeen),
                bucket.Max(static row => row.LastSeen)));
        }

        return result;
    }
}
