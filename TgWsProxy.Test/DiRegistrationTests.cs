using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Hosting;

namespace TgWsProxy.Test;

/// <summary>
/// Регистрация DI: набор сервисов приложения должен собираться без исключений и
/// содержать ровно один <see cref="IHostedService"/> — прокси-хост (F-1.1).
/// </summary>
/// <remarks>
/// Проверка появилась после регрессии: <c>AddHostedService&lt;IHostedService&gt;(...)</c>
/// падает прямо в момент регистрации («implementation type is indistinguishable»),
/// то есть приложение не стартовало вообще, хотя сборка и юнит-тесты были зелёными.
/// </remarks>
public sealed class DiRegistrationTests
{
    [Fact]
    public void ProxyRegistrations_Build_WithSingleHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TestConfig.Create());

        services.AddProxyApplication().AddProxyInfrastructure();

        var hosted = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Equal(ServiceLifetime.Singleton, hosted.Lifetime);
    }

    [Fact]
    public void ProxyHostedService_IsRegistered_AsConcreteTypeAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TestConfig.Create());

        services.AddProxyApplication().AddProxyInfrastructure();

        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(ProxyHostedService));
        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(IListenerController));
    }
}
