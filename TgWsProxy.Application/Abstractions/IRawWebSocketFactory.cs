namespace TgWsProxy.Application.Abstractions;

public interface IRawWebSocketFactory
{
    Task<IRawWebSocket> ConnectAsync(string ip, string domain, string path, string scope, TimeSpan? timeout = null);
}
