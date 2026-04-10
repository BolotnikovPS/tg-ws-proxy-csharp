using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Application.Logic;

internal sealed class ProxyStats : IProxyStats
{
    private long connectionsTotal;
    private long connectionsWs;
    private long connectionsTcpFallback;
    private long connectionsPassthrough;
    private long connectionsHttpRejected;
    private long connectionsCfProxy;
    private long wsErrors;
    private long bytesUp;
    private long bytesDown;
    private long poolHits;
    private long poolMisses;

    public void IncConnectionsTotal() => Interlocked.Increment(ref connectionsTotal);

    public void IncConnectionsWs() => Interlocked.Increment(ref connectionsWs);

    public void IncConnectionsTcpFallback() => Interlocked.Increment(ref connectionsTcpFallback);

    public void IncConnectionsPassthrough() => Interlocked.Increment(ref connectionsPassthrough);

    public void IncConnectionsHttpRejected() => Interlocked.Increment(ref connectionsHttpRejected);

    public void IncConnectionsCfProxy() => Interlocked.Increment(ref connectionsCfProxy);

    public void IncWsErrors() => Interlocked.Increment(ref wsErrors);

    public void IncPoolHit() => Interlocked.Increment(ref poolHits);

    public void IncPoolMiss() => Interlocked.Increment(ref poolMisses);

    public void AddBytesUp(long bytes) => Interlocked.Add(ref bytesUp, bytes);

    public void AddBytesDown(long bytes) => Interlocked.Add(ref bytesDown, bytes);

    public string Summary()
    {
        var hit = Interlocked.Read(ref poolHits);
        var miss = Interlocked.Read(ref poolMisses);
        return $"total={Interlocked.Read(ref connectionsTotal)} ws={Interlocked.Read(ref connectionsWs)} " +
               $"tcp_fb={Interlocked.Read(ref connectionsTcpFallback)} cf={Interlocked.Read(ref connectionsCfProxy)} " +
               $"http_skip={Interlocked.Read(ref connectionsHttpRejected)} pass={Interlocked.Read(ref connectionsPassthrough)} " +
               $"err={Interlocked.Read(ref wsErrors)} pool={hit}/{hit + miss} " +
               $"up={Interlocked.Read(ref bytesUp)}B down={Interlocked.Read(ref bytesDown)}B";
    }
}
