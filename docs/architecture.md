# Архитектура

[← Оглавление](README.md)

| Проект | Назначение |
|---|---|
| `TgWsProxy.Domain` | Чистая модель без внешних зависимостей: конфигурация (`Config`, `PublicEndpoint`), токены (`ProxyToken`, `SecretMask`), модель handshake (`MtProtoHandshake`), снимки статистики (`StatsSnapshot`, `PeerStatsSnapshot`), исключения. |
| `TgWsProxy.Application` | Сценарии и абстракции: bootstrap-опции и валидация конфига, загрузка/сохранение `config.json` (`ConfigFileLoader`, `ConfigDto`, `ConfigDtoMapper`), применение изменений (`ConfigUpdateService`), реестры (`ProxyTokenRegistry`, `PeerStatsRegistry`), статистика (`ProxyStats`), лимиты (`ConnectionLimiter`, `PeerRateLimiter`), ACL (`PeerAccessControl`), порты (`IClientSessionHandler`, `IMtProtoInspector`, `IActiveSessionRegistry`, `IListenerController`, `IProxyServer`). |
| `TgWsProxy.Infrastructure` | Сетевые реализации: `ProxyServer` (watchdog, `RebindAsync`), `RawWebSocket(Factory)`, `WsPool`, `CfWorkerPool`, `CfDomainBalancer`/`CfDomainRefresher`, `CfH2Pool`/`CfH2Lane`/`CfH2Channel`/`H2Bridge`, `TcpBridgeService`, I/O и протокол (`FakeTls/`, `IO/`, `MtProto/`, `Session/`, `WebProxy/`, `Wire/`), хостинг (`ProxyHostedService` как `IListenerController`), кольцевой буфер метрик (`MetricsHistory`). |
| `TgWsProxy` | Хост ASP.NET Core: DI, логирование с ротацией, Web API (`Web/`), Prometheus-метрики (`ProxyMetrics`), health checks (`ProxyHealthChecks`), статика и панель (`wwwroot/`), graceful shutdown. |
| `TgWsProxy.Test` | xUnit-тесты (327 шт.): bootstrap и конфиг, протокол, Fake TLS, H2-пакеты, WS-кадрирование, регрессия порядка перешифрования, liveness пула, токены и ссылки, Web API, hosted-сервис, DI, WEB proxy (codec, capability, multiplexer, HTTP long-poll сессии). |

Раскладка каталогов: `src/` — продакшн-проекты, `tests/` — тесты, `benchmarks/` — замеры производительности.
