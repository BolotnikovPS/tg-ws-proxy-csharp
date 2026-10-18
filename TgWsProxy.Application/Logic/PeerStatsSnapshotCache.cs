using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Кэш снимка реестра «IP, токен» с коротким TTL (оптимизация P0-2). Страница панели
/// опрашивает <c>/api/metrics</c>, <c>/api/per-ip</c> и <c>/api/metrics/by-token</c> несколько
/// раз в секунду, а Prometheus дополнительно скрейпит <c>/metrics</c>; без кэша каждый такой
/// запрос заново строил полный снимок реестра — аллокацию на каждую отслеживаемую пару.
/// Кэш делит один снимок между всеми потребителями в пределах окна TTL.
/// </summary>
public sealed class PeerStatsSnapshotCache
{
    private readonly IPeerStatsRegistry _registry;
    private readonly long _ttlMilliseconds;
    private readonly object _gate = new();
    private IReadOnlyList<PeerStatsSnapshot>? _cached;
    private long _cachedAtMilliseconds;

    /// <summary>Создаёт кэш со стандартным TTL в одну секунду.</summary>
    public PeerStatsSnapshotCache(IPeerStatsRegistry registry)
        : this(registry, TimeSpan.FromSeconds(1))
    {
    }

    /// <summary>Создаёт кэш с явным TTL (для тестов).</summary>
    internal PeerStatsSnapshotCache(IPeerStatsRegistry registry, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _ttlMilliseconds = (long)Math.Max(1, ttl.TotalMilliseconds);
    }

    /// <summary>Возвращает снимок реестра, пересчитывая его не чаще одного раза за TTL.</summary>
    public IReadOnlyList<PeerStatsSnapshot> Get()
    {
        var now = Environment.TickCount64;

        var cached = Volatile.Read(ref _cached);
        if (cached is not null && now - Volatile.Read(ref _cachedAtMilliseconds) < _ttlMilliseconds)
        {
            return cached;
        }

        lock (_gate)
        {
            cached = Volatile.Read(ref _cached);
            if (cached is not null && now - Volatile.Read(ref _cachedAtMilliseconds) < _ttlMilliseconds)
            {
                return cached;
            }

            var snapshot = _registry.Snapshot();
            Volatile.Write(ref _cachedAtMilliseconds, now);
            Volatile.Write(ref _cached, snapshot);
            return snapshot;
        }
    }
}
