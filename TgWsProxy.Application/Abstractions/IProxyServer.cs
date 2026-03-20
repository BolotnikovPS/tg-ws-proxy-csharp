namespace TgWsProxy.Application.Abstractions;

public interface IProxyServer
{
    Task RunAsync(CancellationToken cancellationToken);
}
