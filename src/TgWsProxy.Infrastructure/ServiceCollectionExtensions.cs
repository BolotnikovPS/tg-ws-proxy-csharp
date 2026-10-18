using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.FakeTls;
using TgWsProxy.Infrastructure.Hosting;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.Metrics;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.Session;
using TgWsProxy.Infrastructure.WebProxy;

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
            .AddSingleton<H2Bridge>()
            // Клиентские сессии, чтение init и Fake TLS-перенаправление — сетевой ввод-вывод
            // (Infrastructure), а не бизнес-логика Application.
            .AddSingleton<ClientInitReader>()
            .AddSingleton<MaskingProxy>()
            .AddSingleton<IMtProtoInspector, MtProtoInspector>()
            // Ядро «после handshake» — общее для TCP-режима и web-proxy (DESIGN-WEB-PROXY §4.3).
            .AddSingleton<MtProtoBridgeEngine>()
            // Relay-сессия WEB proxy: подбор токена по HMAC bridge-capability и жизненный цикл
            // сессии (статистика/реестр); менеджер сессий — bootstrap/session-токены и слоты.
            .AddSingleton<IWebProxyRelaySession, WebProxyRelaySession>()
            .AddSingleton<WebProxySessionManager>()
            .AddSingleton<IClientSessionHandler, ClientSessionHandler>()
            .AddSingleton<IActiveSessionRegistry, ActiveSessionRegistry>()
            .AddSingleton(sp => new MetricsHistory(sp.GetRequiredService<Config>()))
            // Ключи timeseries* применяются горячо: ConfigUpdateService пересоздаёт буфер
            // через абстракцию, чтобы слой Application не зависел от Infrastructure (F-2.11).
            .AddSingleton<ITimeseriesBufferConfigurator>(sp => sp.GetRequiredService<MetricsHistory>())
            // AddHostedService<T> регистрирует только IHostedService, а точка входа резолвит
            // конкретный тип (Program.cs), поэтому тип регистрируется явно и hosted-сервис
            // берётся из того же синглтона — иначе старт падает с InvalidOperationException.
            // Явный AddSingleton<IHostedService> (а не AddHostedService<IHostedService>(...)):
            // последний отвергается TryAddEnumerable как «неотличимый» от других IHostedService.
            .AddSingleton<ProxyHostedService>()
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProxyHostedService>())
            // Перезапуск листенера (§ 2.7) выполняет тот же hosted-сервис: он уже владеет
            // пулами, refresher'ом и реестром сессий, которые нужно остановить и поднять заново.
            .AddSingleton<IListenerController>(sp => sp.GetRequiredService<ProxyHostedService>());
}
