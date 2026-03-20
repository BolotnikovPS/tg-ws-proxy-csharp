using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyCore(this IServiceCollection services, Config cfg, Dictionary<int, string> dcOpt)
    {
        services.AddSingleton(cfg);
        services.AddSingleton(dcOpt);
        return services;
    }

    public static IServiceCollection AddProxyApplication(this IServiceCollection services)
    {
        services.AddSingleton<IProxyStats, ProxyStats>();
        services.AddSingleton<IMtProtoInspector, MtProtoInspector>();
        services.AddSingleton<IClientSessionHandler, ClientSessionHandler>();
        return services;
    }
}
