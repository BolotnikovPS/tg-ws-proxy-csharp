using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyApplication(this IServiceCollection services)
        => services.AddSingleton<IProxyStats, ProxyStats>()
            .AddSingleton<PeerAccessControl>()
            .AddSingleton<ConnectionLimiter>()
            .AddSingleton<PeerRateLimiter>()
            .AddSingleton<IPeerStatsRegistry, PeerStatsRegistry>()
            .AddSingleton<PeerStatsSnapshotCache>()
            // Реестр токенов строится по живой конфигурации (F-8.4): её единственный экземпляр
            // регистрируется хостом как singleton, поэтому реестр видит и горячие правки.
            .AddSingleton<IProxyTokenRegistry>(sp => new ProxyTokenRegistry(sp.GetRequiredService<Config>()));
}
