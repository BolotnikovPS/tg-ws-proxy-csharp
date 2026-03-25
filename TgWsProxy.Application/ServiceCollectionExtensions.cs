using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyApplication(this IServiceCollection services)
        => services.AddSingleton<IProxyStats, ProxyStats>()
            .AddSingleton<IWsRoutingState, WsRoutingState>()
            .AddSingleton<IMtProtoInspector, MtProtoInspector>()
            .AddSingleton<IClientSessionHandler, ClientSessionHandler>();
}
