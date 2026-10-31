using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Hosting;

namespace TgWsProxy.Test;

/// <summary>
/// Регистрация DI: набор сервисов приложения должен собираться без исключений (F-1.1).
/// </summary>
/// <remarks>
/// <para>
/// <c>ProxyRegistrations_Build_WithSingleHostedService</c> появилась после регрессии:
/// <c>AddHostedService&lt;IHostedService&gt;(...)</c> падала прямо в момент регистрации
/// («implementation type is indistinguishable»), хотя сборка и юнит-тесты были зелёными.
/// Сейчас прокси-хост регистрируется фабрикой (<c>AddSingleton&lt;IHostedService&gt;(sp =&gt;
/// sp.GetRequiredService&lt;ProxyHostedService&gt;())</c>), а не <c>AddHostedService</c>,
/// поэтому сама регистрация — уже доказательство, что регрессия не вернулась.
/// </para>
/// <para>
/// Assert.Single ровно на один <c>IHostedService</c> больше некорректен: с появлением
/// Wolverine (шина CQRS в слое Application) в контейнере оказывается несколько hosted-сервисов.
/// Инвариант — приложению принадлежит ровно один hosted-сервис, <c>ProxyHostedService</c>,
/// и он зарегистрирован как singleton (F-1.1).
/// </para>
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

        // Приложению принадлежит ровно один экземпляр прокси-хоста (F-1.1),
        // и он — singleton.
        var proxyHost = Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(ProxyHostedService));
        Assert.Equal(ServiceLifetime.Singleton, proxyHost.Lifetime);

        // IHostedService-регистрации присутствуют (одна для прокси-хоста, плюс
        // собственные hosted-сервисы Wolverine — шина CQRS в слое Application).
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
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