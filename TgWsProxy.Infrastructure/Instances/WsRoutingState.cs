#nullable enable

using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class WsRoutingState : IWsRoutingState
{
    private readonly Dictionary<(int Dc, bool IsMedia), DateTimeOffset> failUntil = [];
    private readonly HashSet<(int Dc, bool IsMedia)> blacklist = [];
    private readonly Dictionary<(int Dc, bool IsMedia), List<(IRawWebSocket Ws, DateTimeOffset Created)>> pool = [];
    private readonly HashSet<(int Dc, bool IsMedia)> poolRefilling = [];
    private readonly object sync = new();

    public bool IsInFailCooldown((int Dc, bool IsMedia) dcKey, DateTimeOffset now)
    {
        lock (sync)
        {
            return failUntil.TryGetValue(dcKey, out var until) && until > now;
        }
    }

    public void SetFailCooldown((int Dc, bool IsMedia) dcKey, DateTimeOffset until)
    {
        lock (sync)
        {
            failUntil[dcKey] = until;
        }
    }

    public void ClearFailCooldown((int Dc, bool IsMedia) dcKey)
    {
        lock (sync)
        {
            failUntil.Remove(dcKey);
        }
    }

    public bool IsBlacklisted((int Dc, bool IsMedia) dcKey)
    {
        lock (sync)
        {
            return blacklist.Contains(dcKey);
        }
    }

    public void AddBlacklist((int Dc, bool IsMedia) dcKey)
    {
        lock (sync)
        {
            blacklist.Add(dcKey);
        }
    }

    public IRawWebSocket? TryTakePooledWs((int Dc, bool IsMedia) dcKey, DateTimeOffset now, TimeSpan maxAge)
    {
        lock (sync)
        {
            if (!pool.TryGetValue(dcKey, out var bucket))
            {
                return null;
            }

            while (bucket.Count > 0)
            {
                var (Ws, Created) = bucket[0];
                bucket.RemoveAt(0);
                if (now - Created <= maxAge)
                {
                    return Ws;
                }
            }

            return null;
        }
    }

    public bool TryBeginRefill((int Dc, bool IsMedia) dcKey)
    {
        lock (sync)
        {
            return poolRefilling.Add(dcKey);
        }
    }

    public void EndRefill((int Dc, bool IsMedia) dcKey)
    {
        lock (sync)
        {
            poolRefilling.Remove(dcKey);
        }
    }

    public IReadOnlyList<IRawWebSocket> AddToPool((int Dc, bool IsMedia) dcKey, IRawWebSocket ws, DateTimeOffset createdAt, int maxPoolSize)
    {
        var evicted = new List<IRawWebSocket>();
        lock (sync)
        {
            if (!pool.TryGetValue(dcKey, out var bucket))
            {
                bucket = [];
                pool[dcKey] = bucket;
            }

            bucket.Add((ws, createdAt));
            while (bucket.Count > maxPoolSize)
            {
                evicted.Add(bucket[0].Ws);
                bucket.RemoveAt(0);
            }
        }
        return evicted;
    }

    public string FormatBlacklistSummary()
    {
        lock (sync)
        {
            if (blacklist.Count == 0)
            {
                return "none";
            }

            var keys = blacklist.OrderBy(k => k.Dc).ThenBy(k => k.IsMedia).ToList();
            return string.Join(", ", keys.Select(k => $"DC{k.Dc}{(k.IsMedia ? "m" : "")}"));
        }
    }
}
