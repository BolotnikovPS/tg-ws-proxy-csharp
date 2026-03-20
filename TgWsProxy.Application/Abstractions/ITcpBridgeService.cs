using System.Net.Sockets;

namespace TgWsProxy.Application.Abstractions;

public interface ITcpBridgeService
{
    Task BridgeWsAsync(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init = null);

    Task TcpFallbackAsync(NetworkStream client, string dst, int port, byte[] init, string scope);

    Task TcpPassthroughAsync(NetworkStream client, string dst, int port, string scope);
}
