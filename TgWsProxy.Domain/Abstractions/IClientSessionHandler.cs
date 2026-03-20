using System.Net.Sockets;

namespace TgWsProxy.Domain.Abstractions;

public interface IClientSessionHandler
{
    Task HandleAsync(TcpClient client, ClientContext context, CancellationToken cancellationToken);

    Task WarmupAsync(CancellationToken cancellationToken);
}
