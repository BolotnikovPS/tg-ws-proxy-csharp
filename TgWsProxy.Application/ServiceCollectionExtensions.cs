using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.Logic.FakeTls;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyApplication(this IServiceCollection services)
        => services.AddSingleton<IProxyStats, ProxyStats>()
            .AddSingleton<IMtProtoInspector, MtProtoInspector>()
            .AddSingleton<PeerAccessControl>()
            .AddSingleton<ClientInitReader>()
            .AddSingleton<MaskingProxy>()
            .AddSingleton<IClientSessionHandler, ClientSessionHandler>();
}
