using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyInfrastructure(this IServiceCollection services)
        => services.AddSingleton<ITcpBridgeService, TcpBridgeService>()
            .AddSingleton<IRawWebSocketFactory, RawWebSocketFactory>()
            .AddSingleton<IProxyServer, ProxyServer>();
}
