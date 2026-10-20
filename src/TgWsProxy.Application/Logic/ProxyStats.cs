using System.Globalization;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application.Logic;

internal sealed class ProxyStats : IProxyStats
{
    private long connectionsTotal;
    private long connectionsActive;
    private long connectionsWs;
    private long connectionsH2;
    private long h2TcpConnections;
    private long h2Requests;
    private long h2Errors;
    private long connectionsTcpFallback;
    private long connectionsCfProxy;
    private long connectionsFronting;
    private long connectionsBad;
    private long connectionsBlocked;
    private long connectionsMasked;
    private long connectionsLimited;
    private long connectionsRateLimited;
    private long wsErrors;
    private long bytesUp;
    private long bytesDown;
    private long poolHits;
    private long poolMisses;
    private long cfPoolHits;
    private long cfPoolMisses;

    public void IncConnectionsTotal() => Interlocked.Increment(ref connectionsTotal);

    public void IncConnectionsActive() => Interlocked.Increment(ref connectionsActive);

    public void DecConnectionsActive() => Interlocked.Decrement(ref connectionsActive);

    public void IncConnectionsWs() => Interlocked.Increment(ref connectionsWs);

    public void IncConnectionsH2() => Interlocked.Increment(ref connectionsH2);

    public void IncH2TcpConnections() => Interlocked.Increment(ref h2TcpConnections);

    public void IncH2Requests() => Interlocked.Increment(ref h2Requests);

    public void IncH2Errors() => Interlocked.Increment(ref h2Errors);

    public void IncConnectionsTcpFallback() => Interlocked.Increment(ref connectionsTcpFallback);

    public void IncConnectionsCfProxy() => Interlocked.Increment(ref connectionsCfProxy);

    public void IncConnectionsFronting() => Interlocked.Increment(ref connectionsFronting);

    public void IncConnectionsBad() => Interlocked.Increment(ref connectionsBad);

    public void IncConnectionsBlocked() => Interlocked.Increment(ref connectionsBlocked);

    public void IncConnectionsMasked() => Interlocked.Increment(ref connectionsMasked);

    public void IncConnectionsLimited() => Interlocked.Increment(ref connectionsLimited);

    public void IncConnectionsRateLimited() => Interlocked.Increment(ref connectionsRateLimited);

    public void IncWsErrors() => Interlocked.Increment(ref wsErrors);

    public void IncPoolHit() => Interlocked.Increment(ref poolHits);

    public void IncPoolMiss() => Interlocked.Increment(ref poolMisses);

    public void IncCfPoolHit() => Interlocked.Increment(ref cfPoolHits);

    public void IncCfPoolMiss() => Interlocked.Increment(ref cfPoolMisses);

    public void AddBytesUp(long bytes) => Interlocked.Add(ref bytesUp, bytes);

    public void AddBytesDown(long bytes) => Interlocked.Add(ref bytesDown, bytes);

    public string Summary()
    {
        var hit = Interlocked.Read(ref poolHits);
        var miss = Interlocked.Read(ref poolMisses);
        var poolTotal = hit + miss;
        var pool = poolTotal > 0 ? $"{hit}/{poolTotal}" : "n/a";
        var cfHit = Interlocked.Read(ref cfPoolHits);
        var cfMiss = Interlocked.Read(ref cfPoolMisses);
        var cfTotal = cfHit + cfMiss;
        var cfPool = cfTotal > 0 ? $"{cfHit}/{cfTotal}" : "n/a";

        return $"total={Interlocked.Read(ref connectionsTotal)} " +
               $"active={Interlocked.Read(ref connectionsActive)} " +
               $"ws={Interlocked.Read(ref connectionsWs)} " +
               $"h2={Interlocked.Read(ref connectionsH2)} " +
               $"h2_tcp={Interlocked.Read(ref h2TcpConnections)} " +
               $"h2_req={Interlocked.Read(ref h2Requests)} " +
               $"h2_err={Interlocked.Read(ref h2Errors)} " +
               $"tcp_fb={Interlocked.Read(ref connectionsTcpFallback)} " +
               $"cf={Interlocked.Read(ref connectionsCfProxy)} " +
               $"front={Interlocked.Read(ref connectionsFronting)} " +
               $"bad={Interlocked.Read(ref connectionsBad)} " +
               $"blocked={Interlocked.Read(ref connectionsBlocked)} " +
               $"masked={Interlocked.Read(ref connectionsMasked)} " +
               $"limited={Interlocked.Read(ref connectionsLimited)} " +
               $"rate_limited={Interlocked.Read(ref connectionsRateLimited)} " +
               $"err={Interlocked.Read(ref wsErrors)} " +
               $"pool={pool} " +
               $"cf_pool={cfPool} " +
               $"up={HumanBytes(Interlocked.Read(ref bytesUp))} " +
               $"down={HumanBytes(Interlocked.Read(ref bytesDown))}";
    }

    public StatsSnapshot Snapshot() => new()
    {
        ConnectionsTotal = Interlocked.Read(ref connectionsTotal),
        ConnectionsActive = Interlocked.Read(ref connectionsActive),
        ConnectionsWs = Interlocked.Read(ref connectionsWs),
        ConnectionsH2 = Interlocked.Read(ref connectionsH2),
        H2TcpConnections = Interlocked.Read(ref h2TcpConnections),
        H2Requests = Interlocked.Read(ref h2Requests),
        H2Errors = Interlocked.Read(ref h2Errors),
        ConnectionsTcpFallback = Interlocked.Read(ref connectionsTcpFallback),
        ConnectionsCfProxy = Interlocked.Read(ref connectionsCfProxy),
        ConnectionsFronting = Interlocked.Read(ref connectionsFronting),
        ConnectionsBad = Interlocked.Read(ref connectionsBad),
        ConnectionsBlocked = Interlocked.Read(ref connectionsBlocked),
        ConnectionsMasked = Interlocked.Read(ref connectionsMasked),
        ConnectionsLimited = Interlocked.Read(ref connectionsLimited),
        ConnectionsRateLimited = Interlocked.Read(ref connectionsRateLimited),
        WsErrors = Interlocked.Read(ref wsErrors),
        BytesUp = Interlocked.Read(ref bytesUp),
        BytesDown = Interlocked.Read(ref bytesDown),
        PoolHits = Interlocked.Read(ref poolHits),
        PoolMisses = Interlocked.Read(ref poolMisses),
        CfPoolHits = Interlocked.Read(ref cfPoolHits),
        CfPoolMisses = Interlocked.Read(ref cfPoolMisses),
    };

    /// <summary>
    /// Приводит число байт к человекочитаемому виду (аналог upstream human_bytes).
    /// </summary>
    internal static string HumanBytes(long n)
    {
        double value = n;
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (Math.Abs(value) < 1024)
            {
                return value.ToString("0.0", CultureInfo.InvariantCulture) + unit;
            }
            value /= 1024;
        }
        return value.ToString("0.0", CultureInfo.InvariantCulture) + "TB";
    }
}
