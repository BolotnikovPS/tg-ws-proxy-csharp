# Логирование и корреляция

Цель — сделать логи прокси «связными»: по одному идентификатору находить всю цепочку событий одного HTTP-запроса (панель/Web API) и одной прокси-сессии (TCP-клиент, WEB proxy), не теряя при этом структурированность событий и маскирование секретов.

## Текущее состояние

Логирование собрано в единственном месте — `src/TgWsProxy/Program.cs` (`BuildSerilogLogger`):

- уровень задаётся `LoggingLevelSwitch` (`verbose` → Debug, иначе Information);
- консоль (стандартный шаблон) + опциональный асинхронный файл (`CompactJsonFormatter`, ограничение размера или часовой ролл по `logs.path`/`logs.maxMegabytes`/`logs.retainedFileCount`);
- регистрация через legacy-путь `builder.Logging.ClearProviders().AddDebug().AddSerilog(...)`.

Чего нет:

- `.Enrich.*` отсутствуют вовсе (нет `FromLogContext`, нет machine/env/app-свойств);
- нет `UseSerilogRequestLogging` и correlation-middleware — HTTP-запросы панели некоррелируемы;
- `Activity`/`TraceId` нигде не используются;
- у TCP-соединений корреляция есть только как **строка** `scope = "peer|connectionId"` (`ProxyServer`), которая пишется в текст сообщений `[{Scope}]`, а не как отдельные структурированные поля.

## Целевая модель корреляции

| Поле | Источник | Домен |
|---|---|---|
| `TraceId`, `SpanId` | `Activity` (W3C) + `Serilog.Enrichers.Span` | HTTP + (опц.) прокси |
| `CorrelationId` | `X-Correlation-Id` (middleware, `LogContext`) | HTTP |
| `RequestHost`, `UserAgent` | `UseSerilogRequestLogging` | HTTP |
| `ConnectionId`, `Peer` | `ClientContext` + `ILogger.BeginScope` | TCP-прокси |
| `TokenId`, `Dc`, `Media` | handshake в `ClientSessionHandler` | TCP-прокси |
| `SessionId`, `StreamId`, `CarrierMode` | WEB-proxy relay | WEB-proxy |
| `Application`, `Version`, `EnvironmentName`, `MachineName` | глобальные enricher'ы | все |

Сквозная корреляция для не-HTTP пути прокси реализуется через `ILogger.BeginScope` (фреймворк-нейтральный механизм scoped-логов), который Serilog автоматически раскладывает в структурированные свойства события — это сохраняет чистоту слоёв (Infrastructure не зависит от Serilog).

## Ключевые решения

1. **`AddSerilog((services, lc) => …)` вместо `AddSerilog(ILogger)`** — интеграция с DI (`ReadFrom.Services`), провайдер логов заменяется корректно, `AddDebug()` больше не нужен.
2. **Корреляция прокси — через `ILogger.BeginScope`, а не `Serilog.LogContext`** — не тянем Serilog в слой Infrastructure.
3. **`Serilog.Enrichers.Span`** — глобально добавляет `TraceId`/`SpanId` на каждое событие, где есть активная `Activity`. Чтобы `Activity` гарантированно создавался на каждый HTTP-запрос, в стартапе установлен встроенный `ActivityListener` на `ActivitySource` «Microsoft.AspNetCore» (без OpenTelemetry); для прокси-сессии `ActivitySource` остаётся stretch.
4. **Фильтрация шума без отдельного пакета** — `UseSerilogRequestLogging` отдаёт уровень `Verbose` для `/healthz`, `/metrics`, `/swagger` и высокочастотных `/{base}api/v1/*` (ниже минимального уровня, т.е. фактически не пишется).
5. **Маскирование секретов — единой `IDestructuringPolicy`** в дополнение к точечным `SecretMask.Mask(...)`.

## План внедрения

- **M1. Пакеты** — `Serilog.Enrichers.Environment` + `Serilog.Enrichers.Span` (CPM + csproj).
- **M2. Конвейер Serilog** — enricher'ы, `outputTemplate` консоли с `TraceId`/`CorrelationId`, `AddSerilog`.
- **M3. Correlation middleware** — `CorrelationIdMiddleware`.
- **M4. Request logging** — `UseSerilogRequestLogging` + подавление шума.
- **M5. Корреляция TCP-сессий** — `ILogger.BeginScope` (`ConnectionId`/`Peer`/`TokenId`/`Dc`/`Media`).
- **M6. Корреляция WEB-proxy** — `SessionId`/`StreamId`/`CarrierMode`.
- **M7. Маскирование** — `SensitiveDataDestructuringPolicy` для `Config`/`ConfigDto`/`ProxyToken`/`ProxyTokenEntry`.
- **M8. Тесты и верификация** — юнит-тесты middleware/scope/маскирования, сборка 0 warning, полный прогон тестов, смоук.

## Безопасность

Секреты не логируются: `WebApiToken`, `WebProxyCertPassword`, `Secret` токенов и `capability` WEB-proxy — маскируются (`SecretMask`, destructuring-политика). Идентификаторы (`token.Name`, `ConnectionId`, `TraceId`) — можно.

## Производительность

Pushed-свойства — один раз на соединение/запрос, не на сообщение; горячий data-plane прокси затрагивается минимально. Файловый sink уже асинхронный.

## Проверка

Запустить сервер, дёрнуть панель и смоук: в консоли должны появиться `TraceId`/`CorrelationId` (HTTP) и `ConnectionId`/`TokenId` (прокси). Для end-to-end проверки `relay_init` нужен доступ к Telegram DC (см. [Диагностика](diagnostics.md)).

## Дальше (stretch)

- `ActivitySource` на прокси-соединение → заполнит `TraceId`/`SpanId` и в не-HTTP пути;
- OTLP-экспорт трасс/логов (`Serilog.Sinks.OpenTelemetry`);
- `Serilog.Expressions` для более тонкой маршрутизации/фильтрации.