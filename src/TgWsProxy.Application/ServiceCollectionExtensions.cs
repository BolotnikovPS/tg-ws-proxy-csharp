using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using Wolverine;

namespace TgWsProxy.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProxyApplication(this IServiceCollection services)
        => services
            // Версия и время старта нужны и /healthz, и обработчикам /api/info, /api/metrics.
            .AddSingleton(_ => AppRuntimeInfo.FromEntryAssembly())
            .AddSingleton<IProxyStats, ProxyStats>()
            .AddSingleton<PeerAccessControl>()
            .AddSingleton<ConnectionLimiter>()
            .AddSingleton<PeerRateLimiter>()
            .AddSingleton<IPeerStatsRegistry, PeerStatsRegistry>()
            .AddSingleton<PeerStatsSnapshotCache>()
            // Реестр токенов строится по живой конфигурации (F-8.4): её единственный экземпляр
            // регистрируется хостом как singleton, поэтому реестр видит и горячие правки.
            .AddSingleton<IProxyTokenRegistry>(sp => new ProxyTokenRegistry(sp.GetRequiredService<Config>()))
            .AddWolverine(options =>
            {
                // Соглашения Wolverine: публичные методы Handle/HandleAsync, первый параметр —
                // тип сообщения. Отдельной регистрации каждого обработчика не требуется.
                options.Discovery.IncludeAssembly(typeof(ServiceCollectionExtensions).Assembly);
            });
}
