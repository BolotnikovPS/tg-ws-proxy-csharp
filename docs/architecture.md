# Архитектура

[← Оглавление](README.md)

TG WS Proxy — локальный MTProto-прокси для Telegram, переписанный на C# (.NET 11). Он принимает
зашифрованный трафик от клиента, маскирует его под обычный HTTPS и доставляет в дата-центры
Telegram через WebSocket, Cloudflare или прямой TCP. Проект работает только как веб-сервис:
управление и настройка — через веб-панель, конфигурация хранится в `config.json`.

## Слои и направление зависимостей

Код разбит на четыре проекта. Зависимости идут строго в одну сторону, циклов нет:

```
TgWsProxy                 хост ASP.NET Core: DI, Web API, метрики, панель
      │  ссылается на
      ▼
TgWsProxy.Infrastructure  сетевые реализации и адаптеры
      │  ссылается на
      ▼
TgWsProxy.Application     сценарии, порты (интерфейсы), CQRS-обработчики
      │  ссылается на
      ▼
TgWsProxy.Domain          чистая модель без внешних зависимостей
```

Правило простое: **код слоя сверху может обращаться к слою снизу, но не наоборот.** `Application`
не знает про `Infrastructure` — он объявляет интерфейсы-порты, а `Infrastructure` их реализует.
`Domain` вообще ни на кого не ссылается.

## Назначение проектов

| Проект | Что в нём лежит |
|---|---|
| **Domain** | Чистые данные и правила без I/O: конфигурация (`Config`, секционные блоки `MtProtoConfig`/`WebProxyConfig`/`CfProxyConfig` и др.), токены (`ProxyToken`, `SecretMask`), модель handshake (`MtProtoHandshake`), снимки статистики (`StatsSnapshot`, `PeerStatsSnapshot`), исключения домена. Здесь же объявлены порты домена (`Domain/Abstractions`): `IProxyStats`, `IProxyTokenRegistry`, `IPeerStatsRegistry`. |
| **Application** | Сценарии и порты. Порты (`Abstractions/`) — 18 интерфейсов (`IProxyServer`, `IListenerController`, `IActiveSessionRegistry`, `IWebProxyLinks`, `ITimeseriesReader` и др.). Логика (`Logic/`) — статистика, реестры токенов/peers, лимиты соединений и rate-limit, ACL. Загрузка и применение конфига (`StartConfig/`). CQRS (`Cqrs/` — запросы, команды, обработчики). Read-модели API (`ReadModels/`). Реализует порты домена. |
| **Infrastructure** | Всё, что общается с сетью и ОС: `ProxyServer` (listener + watchdog), пулы `WsPool`/`CfWorkerPool`/`CfH2Pool`, `TcpBridgeService`, Fake TLS (`FakeTls/`), MTProto (`MtProto/`), клиентские сессии (`Session/`), WEB proxy (`WebProxy/`), кадрирование (`Wire/`), хостинг (`ProxyHostedService`), кольцевой буфер метрик (`MetricsHistory`). Реализует порты Application и регистрирует их в DI (`ServiceCollectionExtensions.cs`). |
| **TgWsProxy (хост)** | Точка входа: сборка DI-контейнера, логгирование, Web API (`Web/`), Prometheus-метрики, health checks, статическая панель (`wwwroot/`), graceful shutdown. HTTP-слой не содержит бизнес-логики (см. ниже про CQRS). |
| **Test / Benchmarks** | xUnit-тесты (398 шт.) и замеры производительности. Видят `internal` через `InternalsVisibleTo`. |

Раскладка каталогов: `src/` — продакшн, `tests/` — тесты, `benchmarks/` — замеры.

## Как HTTP-запрос превращается в действие (CQRS)

Веб-эндпоинты (`WebApiEndpoints`) — очень тонкие. Они лишь разбирают запрос и отправляют
сообщение в шину **Wolverine** (`IMessageBus.InvokeAsync<T>` — медиатор в пределах процесса,
брокер не нужен). Решение принимает обработчик в `Application`:

```
HTTP-запрос ──► WebApiEndpoints ──► IMessageBus ──► *Handler (Application) ──► порт ──► Infrastructure
```

| Домен сообщений | Файл | Обработчик |
|---|---|---|
| Метрики и панель (`GetMetricsQuery`, `GetTimeseriesQuery`, `GetPerIpQuery`, `RunDcCheckQuery`…) | `Application/Cqrs/MetricsMessages.cs` | `MetricsHandler` |
| Конфигурация (`GetConfigQuery`, `UpdateConfigCommand`…) | `Application/Cqrs/ConfigMessages.cs` | `ConfigHandler` |
| Токены (`GetTokensQuery`, `CreateTokenCommand`, `RotateTokenCommand`…) | `Application/Cqrs/TokenMessages.cs` | `TokenHandler` |

Read-модели (`InfoDto`, `MetricsDto`, `PeerStatsDto`, `TimeseriesDto` и др.) лежат в
`Application/ReadModels/` — их используют и хост, и тесты.

Правила, которые нельзя нарушать:

- **Имя класса-обработчика обязано оканчиваться на `Handler`.** Wolverine ищет обработчики по
  этому соглашению. Во множественном числе (`ConfigHandlers`) класс он просто не видит, и запрос
  падает с `IndeterminateRoutesException` → 500.
- **Штатный отказ — это исключение**, а не `Result<T>`. Ожидаемые ошибки
  (`ArgumentException`, `TokenNotFoundException`, `TokenConflictException`, `StartupOnlyKeysException`,
  `ListenerRebindException`) обработчик выбрасывает, а в коды 400/404/409 их переводит только
  HTTP-слой. Чтобы такие отказы не попадали в Error-лог, обработчики помечены
  `[WolverineLogging(FailureLogLevel = LogLevel.Information)]`.
- **`Application` не видит `Infrastructure`.** Обработчик зависит только от портов и домена.
  Реализации портов регистрируются в `Infrastructure/ServiceCollectionExtensions.cs`.
- **Wolverine генерирует код обработчиков в рантайме** — отсюда зависимость
  `WolverineFx.RuntimeCompilation` (Roslyn, ~26 МБ в образе). Хотите меньше образ — переходите
  на предгенерацию: `dotnet run --project src/TgWsProxy -- codegen write` +
  `opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static`.

## Уровни доступа (public vs internal)

По умолчанию тип `internal` — виден только своей сборке. `public` делается только то, что реально
пересекает границу сборки:

- **Интерфейсы-порты** — всегда `public` (это контракты между проектами: `IProxyServer`,
  `ITimeseriesReader`, `IWebProxyLinks` и т. д.).
- **Типы в сигнатурах публичных членов** — `public` (иначе компилятор выдаёт
  `CS0050`/`CS0051`/`CS0053`): секционные блоки конфигурации, `PeerStatsDto`, `RelayFrame`,
  `WebProxyRelayLease`, `MetricsHistory`, `RelayStream` и другие.
- **CQRS-обработчики (`*Handler`) и `*ServiceCollectionExtensions`** — `public`: обработчики
  вызывает сгенерированный код Wolverine, методы расширения DI — другие проекты.
- **Всё остальное — `internal`**: реализации портов (`PeerStatsRegistry`, `ProxyTokenRegistry`,
  `ActiveSessionRegistry`, `WebProxyLinks`), агрегаторы, health checks, метрики, middleware,
  кодексы relay, утилиты и константы.
- Тесты и бенчмарки получают доступ к `internal` через `InternalsVisibleTo` в трёх csproj.

## Известные отклонения от идеальной раскладки

Ниже перечислены места, где текущий код пока не соответствует заявленному разбиению
«бизнес-логика ↔ транспорт». Это предмет отдельного рефакторинга, а не ошибка компиляции:

- `Protocol` (MTProto/WS-константы: адреса DC, пути `/apiws`, длины handshake) и
  `CfDomains` (обфусцированный список CF-доменов) лежат в `Application/Logic`, хотя по смыслу
  это транспортные данные и потребляются они только из `Infrastructure`.
- `IMetricsServer` в `Application/Abstractions` — устаревший интерфейс без реализаций и
  потребителей (наследие эпохи до веб-режима).
- `ListenerState`, `ListenerRestartResult`, `ListenerRestartInProgressException` — конкретные
  типы, лежащие в папке `Abstractions` вместе с интерфейсами.