using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyInfrastructure(this IServiceCollection services)
        => services.AddSingleton<IRawWebSocketFactory, RawWebSocketFactory>()
            .AddSingleton<ITcpBridgeService, TcpBridgeService>()
            .AddSingleton<IProxyServer, ProxyServer>()
            .AddSingleton<IWsPool, WsPool>()
            .AddSingleton<ICfWorkerPool, CfWorkerPool>()
            .AddSingleton<ICfDomainBalancer, CfDomainBalancer>()
            .AddSingleton<ICfDomainRefresher, CfDomainRefresher>()
            .AddSingleton<ICfH2Pool, CfH2Pool>()
            .AddSingleton<H2Bridge>();
}
