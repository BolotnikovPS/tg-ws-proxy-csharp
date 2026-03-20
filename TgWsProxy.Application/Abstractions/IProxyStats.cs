namespace TgWsProxy.Application.Abstractions;

public interface IProxyStats
{
    void IncConnectionsTotal();
    void IncConnectionsWs();
    void IncConnectionsTcpFallback();
    void IncConnectionsPassthrough();
    void IncConnectionsHttpRejected();
    void IncWsErrors();
    void IncPoolHit();
    void IncPoolMiss();
    void AddBytesUp(long bytes);
    void AddBytesDown(long bytes);
    string Summary();
}
