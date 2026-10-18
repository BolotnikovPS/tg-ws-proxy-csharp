# Требования: Web API + HTML-панель как единственный режим работы

**Проект:** `tg-ws-proxy-csharp` (порт Flowseal/tg-ws-proxy на C#/.NET 11)
**Статус документа:** черновик требований, редакция 3 — web-only + мультитокены
**Область:** `TgWsProxy` (host), `TgWsProxy.Application`, `TgWsProxy.Domain`, `Dockerfile`, `DockerCompose`, `README.md`

---

## 1. Цель и контекст

### 1.1. Состояние после предыдущих редакций

Этапы 1–7 выполнены: приложение поднимается как ASP.NET Core host, прокси работает как `ProxyHostedService`, есть `/metrics`, `/healthz`, `/api/*`, per-IP учёт и HTML-панель. Остались ограничения, которые эта редакция устраняет:

- **Конфигурация всё ещё двухканальная.** `config.json` читается, но аргументы командной строки продолжают перекрывать файл (`CliParser.Parse(args, cfg)`), то есть консоль остаётся вторым интерфейсом настройки. `TgWsProxy\Properties\launchSettings.json` при этом подсовывает `--host`, `--secret`, `-v`.
- **Консольный режим формально жив.** `metricsPort = 0` выключает веб-слой и возвращает приложение к поведению «чистой консоли».
- **Часть настроек требует перезапуска процесса.** `PUT /api/config` отклоняет ключи категории «только старт» (`port`, `host`, `verbose`, `metricsPort`, `metricsHost`, `enableSwagger`, логирование) с `409` и списком `startupOnly`; остальные применяются горячо или перезапуском слушателя внутри процесса.
- **Секрет ровно один.** `Config.Secret` — единственная строка 32 hex; все устройства подключаются по одной ссылке, различить их трафик невозможно. `LogLinks` печатает одну ссылку `tg://`.
- **Метрики Prometheus формируются вручную.** `PrometheusFormatter` собирает текст из `StringBuilder`; интеграции с `prometheus-net` и с подсистемой health checks нет. `GET /healthz` — рукописная проверка флага `IProxyServer.IsListening`.
- **Контракта API нет.** Swagger/OpenAPI отсутствует, документация — только этот файл и `README.md`.

### 1.2. Целевое состояние

Приложение — **веб-приложение и ничего больше**:

1. **Единственный режим запуска** — ASP.NET Core host с постоянно работающим веб-сервером. Консольного режима нет, `metricsPort = 0` недопустим.
2. **Единственный интерфейс настройки** — страница `/` (и её API). Командная строка сокращается до bootstrap-опций.
3. **Любое изменение настроек применяется в работающем процессе**: «горячо» либо перезапуском слушателя (остановка accept-loop, перепривязка сокета, подъём пулов).
4. **Ключи, которые нельзя менять на лету** (`metricsPort`, `metricsHost`, логирование, `enableSwagger`), помечаются «только старт» и отклоняются API с `409`.
5. **Множество токенов доступа.** Прокси обслуживает не один секрет, а список именованных токенов (каждый — свой секрет 32 hex и своя `tg://`-ссылка). Токены создаются, переименовываются, отключаются и удаляются **со страницы**.
6. **Учёт трафика по паре (IP устройства, токен).** Метрики показывают, какое устройство и по какому токену сколько передало; разрез доступен в JSON, в Prometheus и на странице.
7. **Стандартная наблюдаемость.** Health checks — на `Microsoft.Extensions.Diagnostics.HealthChecks` + `AspNetCore.HealthChecks.UI.Client` + `prometheus-net.AspNetCore.HealthChecks`; `/metrics` — единый registry `prometheus-net`; контракт API — Swagger/OpenAPI.

### 1.3. Границы (что НЕ входит в объём)

- Многопользовательская аутентификация панели, роли, RBAC (токены — только доступ к прокси).
- Хранение истории метрик в БД (только in-memory ring buffer).
- Изменение протокольной логики MTProto / WebSocket / H2 / CF-fallback, кроме выбора секрета при handshake (§ 2.8).
- **Сохранение активных MTProto-сессий при перезапуске слушателя** — сессии разрываются немедленно (§ 2.7, F-7.4).
- Дашборд `AspNetCore.HealthChecks.UI` (веб-UI пакета) — не используется, только `UI.Client` для формата ответа.
- Поддержка запуска на Windows-службе / systemd — вне объёма (Docker).
- Горячая замена привязки веб-сервера (Kestrel) — запрещена (§ 8.1).

---

## 2. Функциональные требования

### 2.1. Хостинг: единственный режим — веб-приложение

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-1.1** | Точка входа — `WebApplication`/`IHost` (`Microsoft.Extensions.Hosting`). Прокси-сервер запускается как `IHostedService` (`ProxyHostedService`). | Must |
| **F-1.2** | Сохраняется вся текущая DI-регистрация: `AddSingleton(cfg)`, `AddProxyApplication()`, `AddProxyInfrastructure()`. | Must |
| **F-1.3** | Корректное завершение: `SIGTERM`/`Ctrl+C` → остановка hosted-сервисов в прежнем порядке (`cfDomainRefresher.Stop()`, `wsPool.Reset()`, `cfWorkerPool.Reset()`, `cfH2Pool.CloseAsync()`). | Must |
| **F-1.4** | Веб-сервер запускается **всегда**. Порт и хост — из `MetricsPort` / `MetricsHost`; `MetricsPort = 0` больше не допускается (`ConfigValidator`: `1…65535`). Ветки «веб отключён» в `Program.cs`, `ProxyHostedService` и HEALTHCHECK не остаётся. | Must |
| **F-1.5** | Отказ привязки веб-сервера при **старте** фатален: `Critical` в журнал и код возврата `1`. Отказ привязки **прокси-листенера** фатальным не является: попытки повторяются, readiness отдаёт `503`. | Must |
| **F-1.6** | Ошибки веб-слоя не роняют прокси; необработанные исключения — `500` с `ProblemDetails`, без утечки секретов. | Must |
| **F-1.7** | `--help` / `--version` работают и обрабатываются до загрузки конфигурации (`CliParser.TryHandleInfo`). `HelpText` сокращается до bootstrap-опций (F-1.10) и ссылки на страницу настроек. | Must |
| **F-1.8** | Логирование (Serilog) сохраняется; добавляется request-logging веб-слоя на уровне `Debug` при `Verbose = true`. | Should |
| **F-1.9** | Интерактивной консоли нет: `stdin` не читается, `stdout`/`stderr` — только журнал. | Must |
| **F-1.10** | CLI сокращается до bootstrap: `--config <path>` (и `--config=<path>`), `-h`/`--help`, `--version`. Любой другой аргумент — код `1` и сообщение, что параметры перенесены в `config.json`; для известных прежних флагов выводится подсказка с JSON-ключом (`--port` → `"port"`). | Must |
| **F-1.11** | **Swagger/OpenAPI.** Подключается `Swashbuckle.AspNetCore`: `GET /swagger` — Swagger UI, `GET /swagger/v1/swagger.json` — спецификация OpenAPI 3.0. Включается `GenerateDocumentationFile` и `IncludeXmlComments`, чтобы описания из XML-комментариев попадали в спецификацию. Схемы `ProblemDetails`, `ConfigDto`, `TokenDto`, `MetricsDto` и `PerIpPageDto` обязаны присутствовать. Для `webApiToken` объявляется security scheme `bearer`, чтобы кнопка «Authorize» в UI работала. Включается ключом `enableSwagger` (умолчание `true`). | Must |
| **F-1.12** | Swagger UI и спецификация не требуют bearer-токена (это статические артефакты без данных), но все `/api/*`, вызываемые из UI, подчиняются общей авторизации (F-3.8). Секреты в спецификацию не попадают. | Must |

### 2.2. Конфигурация: `config.json` как единственный источник

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-2.1** | Все параметры из § 4 доступны через `config.json` и через Web API (страница). | Must |
| **F-2.2** | Источники и приоритет: **встроенные умолчания** → **`config.json`**. Аргументы командной строки конфигурацию не задают — только путь к файлу (F-2.3). | Must |
| **F-2.3** | Путь к файлу: `--config <path>` → `TGWS_CONFIG` → `config.json` в рабочей директории (в контейнере — `/app/config/config.json`). | Must |
| **F-2.4** | Отсутствие файла — **не ошибка**: применяются умолчания, файл создаётся при первом сохранении. | Must |
| **F-2.5** | Некорректный JSON / неизвестный ключ / значение вне диапазона — ошибка старта: сообщение в журнал и код `1`. | Must |
| **F-2.6** | Файл читается как JSON с комментариями и висячими запятыми. | Should |
| **F-2.7** | Сохранение — атомарное: `<path>.tmp` → `fsync` → `File.Move(overwrite: true)`. | Must |
| **F-2.8** | Перед записью — тот же `ConfigValidator.Validate`; при провале `400` с перечнем нарушений, файл не изменяется. | Must |
| **F-2.9** | Коллекции (`Tokens`, `DcIp`, `AllowIp`, `DenyIp`, `CfProxyDomains`, `CfProxyWorkerDomains`) в JSON — массивы; (де)сериализация через DTO + маппинг, т. к. в `Config.cs` они `get`-only. | Must |
| **F-2.10** | Секреты токенов и `webApiToken` **никогда** не отдаются в открытом виде через GET API: только маска (`abcd…ef01`). Исключения — момент создания/ротации токена и запрос ссылки `GET /api/tokens/{id}/link` (§ 2.8, F-8.8); все они отвечают с `Cache-Control: no-store`. | Must |
| **F-2.11** | Каждый ключ относится к одной из трёх категорий (§ 4, колонка «Применение»): **горячо**; **слушатель** (перезапуск слушателя, § 2.7, сессии разрываются); **только старт** (читается при запуске, через API не меняется). Понятия `requiresRestart` нет. | Must |
| **F-2.12** | `PUT /api/config` отклоняет изменение ключей «только старт» с `409` и списком `startupOnly`. Неизменённые значения нарушением не считаются и игнорируются. | Must |
| **F-2.13** | Применение транзакционно: валидация → атомарная запись → применение → при неудаче откат в памяти и на диске. | Must |
| **F-2.14** | Одновременные `PUT` сериализуются; во время перезапуска слушателя — `409` с `Retry-After`. | Should |
| **F-2.15** | Пустой `secret` у нового токена → генерация 32 hex, сохранение, предупреждение в `warnings`. | Should |

### 2.3. Web API: метрики и health checks

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-3.1** | `GET /metrics` — Prometheus text exposition, **единый registry `prometheus-net`** (`Metrics.DefaultRegistry`). Рукописный `PrometheusFormatter` удаляется. Все прикладные счётчики регистрируются как `Counter`/`Gauge` в этом registry; сериализация — средствами `prometheus-net.AspNetCore`. Имена счётчиков получают суффикс `_total` (`tgwsproxy_bytes_up` → `tgwsproxy_bytes_up_total`). | Must |
| **F-3.2** | `GET /api/metrics` — те же данные в JSON (`StatsSnapshot` + метаданные). | Must |
| **F-3.3** | `GET /api/metrics/timeseries?window=…&step=…` — временной ряд: активные соединения, скорость up/down, ошибки. Ring buffer в памяти; умолчания — `timeseriesWindowSeconds`/`timeseriesStepSeconds`. | Should |
| **F-3.4** | **Health checks на ASP.NET Core.** `AddHealthChecks()` с зарегистрированными проверками: `self` (тег `live`), `proxy-listener` (тег `ready`, читает состояние `IProxyServer`), `config` (тег `ready`, файл читаем и каталог доступен на запись), `stats-registry` (тег `ready`, `Degraded` при переполнении per-IP реестра). Формат ответа readiness — `UIResponseWriter.WriteHealthCheckUIResponse` из `AspNetCore.HealthChecks.UI.Client`. | Must |
| **F-3.5** | `GET /api/per-ip` — статистика по клиентским IP и токенам (§ 2.4, § 2.9). | Must |
| **F-3.6** | `GET /api/info` — версия, uptime, состояние пулов, флаги возможностей, текущая привязка слушателя, счётчик перезапусков, число токенов (`tokensTotal`/`tokensEnabled`). | Must |
| **F-3.7** | Безопасность обеспечивается привязкой к loopback по умолчанию (`MetricsHost = "127.0.0.1"`); при не-loopback привязке — предупреждение в журнал при старте. | Must |
| **F-3.8** | Опциональный bearer-токен панели (`webApiToken`). Если задан — все `/api/*` требуют `Authorization: Bearer …`; `/healthz`, `/healthz/ready`, `/swagger`, `/metrics` остаются открытыми. Токенов панели ровно один; это **не** токены доступа прокси (§ 2.8). | Could |
| **F-3.9** | Два эндпоинта здоровья. `GET /healthz` — **liveness** (тег `live`): `200`, пока процесс и веб-слой живы; компактный JSON `{status, listening, uptimeSeconds, version}`, без авторизации. `GET /healthz/ready` — **readiness** (тег `ready`): `200 Healthy` / `503 Degraded` (идёт перезапуск слушателя) / `503 Unhealthy` (листенер мёртв), тело — формат `UI.Client`. `ResultStatusCodes`: Healthy→200, Degraded→503, Unhealthy→503. | Must |
| **F-3.10** | **Health-метрики в Prometheus.** Подключается `prometheus-net.AspNetCore.HealthChecks` (`AddPrometheusHealthCheckPublisher()`): публикует `healthcheck_status{name="…"}` (1/0) и `healthcheck_duration_seconds`, которые попадают в общий `/metrics`. | Must |
| **F-3.11** | Кратковременный `503 Degraded` при перезапуске слушателя не должен ронять контейнер: `HEALTHCHECK --retries` перекрывает окно перезапуска (F-6.5). | Must |

### 2.4. Учёт ресурсов по IP

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-4.1** | Компонент `IPeerStatsRegistry` (Application-слой): потокобезопасная структура (`lock`/`ConcurrentDictionary`) с периодическим sweep — паттерн из `PeerRateLimiter`. | Must |
| **F-4.2** | Адрес нормализуется: IPv4-mapped IPv6 → IPv4 (`MapToIPv4()`). Порт **отбрасывается**. | Must |
| **F-4.3** | Счётчики: активные соединения, всего соединений, отклонено (blocked/limited/rate-limited), байты вверх/вниз, ошибки WS, вклад в пулы (hits/misses), время последней активности, время первого появления. С этой редакции счётчики ведутся в разрезе **пары (IP, токен)** — см. § 2.9. | Must |
| **F-4.4** | Инкремент байтов — без блокировки на горячем пути (`Interlocked`). | Must |
| **F-4.5** | Записи с нулевой активностью старше TTL (`perIpStatsTtlSeconds`) удаляются sweep-проходом. Число отслеживаемых записей ограничено (`perIpStatsMaxPeers`, теперь — пары «IP+токен»); при превышении — принудительный sweep, затем отказ в регистрации с инкрементом `peerTrackingOverflow`. | Must |
| **F-4.6** | `ProxyServer` при accept передаёт `IPAddress` (не строку) в реестр; при закрытии сессии — декремент активных. Точки `AddBytesUp/AddBytesDown` (`CfH2Channel.cs:458`, `H2Bridge.cs:62,136`, `TcpBridgeService.cs:84,129,511,544`) получают парный вызов per-IP/per-token учёта через контекст сессии. | Must |
| **F-4.7** | `GET /api/per-ip` — сортировка по трафику убыв., пагинация (`offset`, `limit` по умолчанию 50, максимум 500), фильтр `?q=` по префиксу адреса, `?group=ip\|token\|pair` (по умолчанию `pair`). | Must |
| **F-4.8** | Per-IP данные в Prometheus — с метками `ip` и `token`: `tgws_peer_bytes_up_total{ip="…",token="…"}` и т. д. | Should |
| **F-4.9** | Per-IP учёт включается `enablePerIpStats` (умолчание `true`); при `false` реестр не выделяет память. Переключение горячее. | Should |

### 2.5. HTML-панель

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-5.1** | `GET /` отдаёт единственную HTML-страницу (`wwwroot/index.html` + статика) — единственный интерфейс оператора. | Must |
| **F-5.2** | Без сборщика и внешних CDN: vanilla JS + inline SVG/Canvas, стили локальные. | Must |
| **F-5.3** | Автообновление: polling `/api/metrics` + `/api/per-ip` каждые 2 с, индикатор «последнее обновление», пауза при скрытой вкладке. | Must |
| **F-5.4** | Блок «Обзор»: активные соединения, всего, скорость ↓/↑, ошибки WS, uptime, версия, привязка слушателя, число активных токенов. | Must |
| **F-5.5** | Блок «График трафика»: линейный график скорости ↓/↑ и активных соединений (SVG, без библиотек). | Must |
| **F-5.6** | Блок «Использование ресурсов по IP»: таблица с переключателем разреза «IP / токен / пара IP+токен»; колонки — адрес, токен, активные, всего, байты ↓/↑, ошибки, отклонено, последняя активность; inline-bar доли трафика. | Must |
| **F-5.7** | Блок «Соединения по типам»: WS / H2 / TCP-fallback / CF-proxy / fronting и причины отказа (bad / blocked / limited / rate-limited). | Must |
| **F-5.8** | Блок «Пулы»: попадания/промахи WS- и CF-пула, число живых CF-доменов, состояние H2. | Should |
| **F-5.9** | Блок «Конфигурация»: форма всех ключей § 4, сгруппированная по разделам. Клиентская валидация повторяет `ConfigValidator`; серверная — окончательная. | Must |
| **F-5.10** | Кнопки: «Сохранить» (`PUT /api/config`), «Перезагрузить с диска», «Сбросить к умолчаниям». После сохранения — отчёт: горячо / перезапуск слушателя / разорвано сессий / предупреждения. | Must |
| **F-5.11** | Поле `Secret` маскировано, с кнопками «показать» и «сгенерировать». Смена секрета помечается как перезапуск слушателя с предупреждением, что клиенты должны обновиться по новой ссылке. | Must |
| **F-5.12** | Деградация: при `503` readiness — баннер «прокси недоступен», при ошибке API — баннер с текстом, ранее отрисованные данные не ломаются. | Must |
| **F-5.13** | Тёмная и светлая темы; `prefers-color-scheme` по умолчанию + переключатель с сохранением в `localStorage`. | Should |
| **F-5.14** | Доступность: семантическая вёрстка, клавиатура, контраст ≥ 4.5:1, `aria-live` для баннера, `prefers-reduced-motion`. | Should |
| **F-5.15** | Адаптивность: одна колонка ≤ 640 px. | Should |
| **F-5.16** | Поля категории «только старт» (`port`, `host`, `verbose`, `metricsPort`, `metricsHost`, `enableSwagger`, `logPath`, `logMaxMegabytes`, `logRetainedFileCount`) — только для чтения, с пометкой «только при старте». | Must |
| **F-5.17** | Перед сохранением изменений категории «слушатель» — подтверждение с числом активных сессий, которые будут разорваны. | Must |
| **F-5.18** | Во время перезапуска слушателя — состояние «перезапуск…», кратковременный `503` не считается ошибкой. | Must |
| **F-5.19** | **Блок «Токены доступа».** Таблица: имя, маска секрета, состояние (вкл/выкл), создан, последняя активность, активные сессии, суммарный трафик ↓/↑. Действия в строке: переименовать, включить/выключить, удалить, сменить секрет, скопировать `tg://`-ссылку. | Must |
| **F-5.20** | **Добавление токена.** Диалог: имя (обязательно, уникальное) + секрет (32 hex, с кнопкой «сгенерировать» или пусто — сгенерирует сервер). После создания секрет показывается **один раз** с кнопками «скопировать секрет» и «скопировать ссылку»; после закрытия диалога значение недоступно. | Must |
| **F-5.21** | **Удаление и выключение токена** требуют подтверждения с указанием числа активных сессий, которые будут разорваны (F-8.6). | Must |
| **F-5.22** | Клиентская валидация токенов: секрет — ровно 32 hex, имя — 1…64 символа, уникальность имени и секрета; ошибки показываются у поля. | Must |
| **F-5.23** | Разделы «Сеть и Web API» и «Журналирование» — **справочные**: рендерятся как текст «метка — значение» без полей ввода и не участвуют в `PUT /api/config` (ключи из них относятся к «только старт» и меняются только в `config.json`). | Must |
| **F-5.24** | Раскладка формы конфигурации: каждая группа занимает всю ширину секции (одна колонка, порядок сверху вниз), исключение — справочные группы «Сеть и Web API» и «Журналирование», которые стоят рядом в первой строке. Поля компактные: `font-size` ввода 13 px, `textarea` ≈ 62 px. | Should |
| **F-5.25** | У `allowIp` / `denyIp` — подсказка с форматом списка: по одному адресу в строке (допустимы запятая и точка с запятой), примеры одиночного адреса, CIDR и IPv6, приоритет `denyIp` над `allowIp`. | Should |
| **F-5.26** | При редактировании существующего токена поле «Секрет» подставляется текущим значением (через `GET /api/tokens/{id}/link`); очистка поля означает «не менять», ввод нового значения — смену секрета. | Should |
| **F-5.27** | Отдельная группа «Ссылки Telegram» с полем `publicHost` (внешний адрес прокси, горячий ключ) и подсказкой о форматах и о том, что при служебном `host` без этого поля ссылка не строится. | Must |

### 2.6. Docker: конфигурация вне контейнера

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-6.1** | `Dockerfile`: `VOLUME ["/app/config"]`, `ENV TGWS_CONFIG=/app/config/config.json`. | Must |
| **F-6.2** | Рабочая директория — `/app`; `config.json` не запекается в образ (`.dockerignore`). | Must |
| **F-6.3** | Контейнер работает непривилегированным пользователем; `/app/config` доступен на запись. | Must |
| **F-6.4** | `EXPOSE 1443` и `EXPOSE 8081`; веб-порт активен всегда; `MetricsHost` в контейнере — `0.0.0.0`. | Must |
| **F-6.5** | `HEALTHCHECK` обращается к `/healthz/ready` (readiness, F-3.9), а не к TCP-порту. `--retries=3 --interval=30s --start-period=20s` перекрывают окно перезапуска слушателя (≤10 с). TCP-фолбэк из `docker/healthcheck.sh` удаляется. | Must |
| **F-6.6** | `DockerCompose/docker-compose.yml`: bind-mount `./config:/app/config` (именованный volume — закомментированная альтернатива). | Must |
| **F-6.7** | `config.example.json` документирует все ключи с умолчаниями и категорией применения. | Must |
| **F-6.8** | Изменения конфига через UI переживают `docker compose down && up`. | Must |
| **F-6.9** | `docker-compose.yml` не передаёт конфигурационные флаги в `command:` — только `environment: TGWS_CONFIG`. Переменные `TG_HOST`, `TG_DC_IP_*` удаляются или переносятся в `config.json`. | Must |

### 2.7. Перезапуск слушателя без рестарта процесса

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-7.1** | Операция `RestartAsync(candidate, ct)` (расширение `IProxyServer` либо `IListenerController`) без остановки процесса и без пересоздания DI-контейнера. | Must |
| **F-7.2** | Порядок шагов: (1) состояние `Restarting`; (2) остановить accept-loop и watchdog; (3) `cfDomainRefresher.Stop()`, `wsPool.Reset()`, `cfWorkerPool.Reset()`, `cfH2Pool.CloseAsync()`; (4) закрыть активные сессии и посчитать; (5) привязать новый `IPEndPoint(candidate.Host, candidate.Port)`; (6) поднять пулы и refresher; (7) перенести значения кандидата в живой `Config`; (8) состояние `Listening`. | Must |
| **F-7.3** | Шаг (7) — **после** успешной привязки: сессии, начатые до перезапуска, не должны увидеть частично применённую конфигурацию. | Must |
| **F-7.4** | Активные сессии разрываются немедленно (§ 8.1). Нужен реестр живых сессий (`IActiveSessionRegistry`): регистрация сокета при accept, снятие при завершении, `CloseAll()` с возвратом количества. Число разорванных возвращается в `PUT /api/config`. | Must |
| **F-7.5** | При неудаче привязки — откат: привязывается прежний `IPEndPoint`, подсистемы поднимаются на прежней конфигурации, файл перезаписывается прежними значениями, API возвращает `409` (`rolledBack: true`) либо `500` с причиной. | Must |
| **F-7.6** | Single-flight: одновременно не более одного перезапуска; таймаут 10 с, по истечении — откат и ошибка. | Must |
| **F-7.7** | Во время перезапуска `/healthz/ready` отдаёт `503 Degraded` (F-3.9). | Must |
| **F-7.8** | Перезапуск логируется на `Information` (старая и новая привязка, число разорванных сессий, длительность); число перезапусков и время последнего — в `GET /api/info`. | Should |

### 2.8. Токены доступа прокси (несколько секретов)

Ключевая доработка: единственный `Config.Secret` заменяется списком именованных токенов. Токен — это **секрет доступа прокси** (32 hex), из которого строится `tg://`-ссылка; он не имеет отношения к `webApiToken` панели.

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-8.1** | Модель `ProxyToken`: `id` (строка, генерируется сервером, стабильна), `name` (1…64 символа, уникально), `secret` (32 hex, уникален), `enabled` (bool), `createdAt`, `lastUsedAt` (только чтение). Коллекция `Config.Tokens` — `get`-only список; (де)сериализация через `ConfigDto`. | Must |
| **F-8.2** | Ограничения: от 1 до 64 токенов; **минимум один включённый** (иначе прокси некуда подключаться — `ConfigValidator` возвращает ошибку). Секреты и имена уникальны. | Must |
| **F-8.3** | Миграция: при первом старте 3.0 прежний ключ `secret` конвертируется в `tokens: [{ id: "default", name: "default", secret: <старый>, enabled: true }]`; ключ `secret` из конфигурации удаляется. Пустой прежний секрет → генерация 32 hex. | Must |
| **F-8.4** | **Handshake с выбором токена.** `ClientSessionHandler` перебирает включённые токены: для каждого выводит ключи (`BuildCryptoContext`) и вызывает `MtProtoInspector.TryHandshake`. Порядок — по `lastUsedAt` убыв. (последний успешный первым), затем по `createdAt`. При успехе сессия получает `tokenId`, обновляется `lastUsedAt`. | Must |
| **F-8.5** | Если ни один токен не подошёл — `stats.IncConnectionsBad()`, сессия закрывается (прежнее поведение при неверном секрете). Частичный успех не допускается: сессия привязывается ровно к одному токену. | Must |
| **F-8.6** | Выключение (`enabled = false`) или удаление токена **разрывает активные сессии этого токена** и возвращает их количество. Уже установленные сессии других токенов не затрагиваются. | Must |
| **F-8.7** | `LogLinks` печатает по одной `tg://`-ссылке на каждый включённый токен с указанием имени; секрет в журнал не пишется (N-8), только маска. | Must |
| **F-8.8** | **CRUD через API** (используется страницей): `GET /api/tokens`, `POST /api/tokens`, `PUT /api/tokens/{id}`, `DELETE /api/tokens/{id}`, `POST /api/tokens/{id}/rotate`, `GET /api/tokens/{id}/link`. В ответах `GET` секрет всегда маскирован. Исключения из F-2.10: `POST /api/tokens` (при пустом `secret` — сгенерированный) и `POST /api/tokens/{id}/rotate` возвращают новый секрет в открытом виде **однократно**, с `Cache-Control: no-store`; повторный `GET` его уже не покажет. Отдельное исключение — `GET /api/tokens/{id}/link`: отдаёт полный секрет и готовую ссылку `https://t.me/proxy?server=…&port=…&secret=dd…` для кнопки «Ссылка», всегда с `Cache-Control: no-store`, чтобы панель могла скопировать конфигурацию для Telegram. Адрес в ссылке берётся из `publicHost` (§ 4.1), а при пустом значении — из `host` и `port`; если итоговый адрес служебный (`0.0.0.0`, `::`) или `publicHost` некорректен, поле `link` равно `null`. | Must |
| **F-8.9** | Применение изменений токенов — категория **горячо**: новый набор валидируется и применяется к новым handshake без перезапуска слушателя; активные сессии сохраняются (кроме случая F-8.6). | Must |
| **F-8.10** | Стоимость перебора ограничена: не более 64 попыток вывода ключей на handshake; попытки выполняются последовательно в памяти без сетевых операций. | Must |
| **F-8.11** | `GET /api/info` отдаёт `tokensTotal` и `tokensEnabled`; `GET /api/tokens` дополнительно отдаёт по каждому токену агрегаты использования (активные сессии, всего соединений, байты ↓/↑). | Should |

### 2.9. Учёт трафика по паре (IP устройства, токен)

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-9.1** | Ключ учёта в `IPeerStatsRegistry` — пара `(IPAddress, tokenId)`. Снимок `PeerStatsSnapshot` дополняется `tokenId` и `tokenName`. | Must |
| **F-9.2** | Все точки учёта байтов (`CfH2Channel.cs:458`, `H2Bridge.cs:62,136`, `TcpBridgeService.cs:84,129,511,544`) получают `tokenId` вместе с адресом: `AddBytesUp(IPAddress?, string? tokenId, long)`. `tokenId` проносится в контексте сессии (`ClientContext`) и в `FallbackRequest` рядом с адресом. | Must |
| **F-9.3** | Отклонённые соединения (blocked/limited/rate-limited) учитываются по IP без токена (`tokenId = null`) — токен на этапе accept ещё неизвестен; такие записи показываются в разрезе `group=ip`. | Must |
| **F-9.4** | `GET /api/per-ip` поддерживает `?group=ip\|token\|pair` (умолчание `pair`) и `?token=<id>` для фильтра. Элемент ответа содержит `ip`, `tokenId`, `tokenName`, `activeConnections`, `totalConnections`, `bytesUp`, `bytesDown`, `wsErrors`, `rejected`, `poolHits`, `poolMisses`, `firstSeen`, `lastSeen`. | Must |
| **F-9.5** | `GET /api/metrics/by-token` — агрегаты по токену целиком (без разреза по IP): суммарные байты, соединения, активные сессии. Используется блоком «Токены доступа». | Must |
| **F-9.6** | В Prometheus per-IP серии получают метку `token` (значение — **имя токена**, никогда не секрет). Число экспортируемых пар ограничено `prometheusTopPeers` (умолчание 200) — отбираются по суммарному трафику; остальные не выгружаются, чтобы не раздувать кардинальность. | Must |
| **F-9.7** | Панель показывает трафик по токену и по паре (IP, токен) — F-5.6 и F-5.19. | Must |

---

## 3. Нефункциональные требования

| ID | Требование |
|----|-----------|
| **N-1** | Накладные расходы веб-слоя не деградируют прокси: per-IP/per-token учёт — только `Interlocked`/словарные операции, без аллокаций на горячем пути передачи данных. |
| **N-2** | Память реестра ограничена `perIpStatsMaxPeers`; при выключенном `enablePerIpStats` — ноль. |
| **N-3** | HTTP-запрос обслуживается без удержания блокировок горячего пути дольше копирования снимка. |
| **N-4** | Разрешённые новые зависимости: `Swashbuckle.AspNetCore`, `AspNetCore.HealthChecks.UI.Client`, `prometheus-net.AspNetCore.HealthChecks` (транзитивно — `prometheus-net`), `Microsoft.Extensions.Diagnostics.HealthChecks`. Регистрируются в `Directory.Packages.props` (центральное управление версиями). Сборщики JS/CSS не вводятся. |
| **N-5** | Целевая платформа — .NET 11, RID `linux-x64`/`linux-arm64`; `TgWsProxy.csproj` — `Microsoft.NET.Sdk.Web`. |
| **N-6** | Ломающее изменение — мажорная версия **3.0.0**: удаление конфигурационных CLI-флагов, консольного режима и ключа `secret`; смена имён метрик Prometheus (`_total`). В `README.md` — раздел миграции 2.x → 3.0. |
| **N-7** | Тестируемость: реестр статистики, реестр сессий, CRUD токенов, выбор токена при handshake, загрузчик/сохранятель конфига, валидация, эндпоинты, перезапуск слушателя и откат, health checks — покрываются xUnit-тестами. |
| **N-8** | Секреты токенов и `webApiToken` не попадают в журнал, в метки Prometheus, в спецификацию OpenAPI и в ответы GET API. |
| **N-9** | Страница не требует интернета: ресурсы локальные, шрифты системные. Swagger UI также отдаётся с локальными статическими ресурсами (без CDN). |
| **N-10** | `TgWsProxy\Properties\launchSettings.json` не содержит конфигурационных флагов. |
| **N-11** | Перезапуск слушателя — не более 10 с, типично сотни миллисекунд. |
| **N-12** | Кардинальность Prometheus ограничена: метки `ip` и `token` экспортируются не более чем для `prometheusTopPeers` пар, отсортированных по трафику. |
| **N-13** | Перебор токенов на handshake не должен заметно влиять на latency установления соединения: не более 64 попыток вывода ключей, без сетевых вызовов. |

---

## 4. Приложение А: перечень переменных конфигурации

Источник модели — `TgWsProxy.Domain\Config.cs`, ограничения — `ConfigValidator`, маппинг — `ConfigDto`.

Категории (§ 2.2, F-2.11): **горячо** — применяется к работающим подсистемам; **слушатель** — перезапуск слушателя (§ 2.7), активные сессии разрываются; **только старт** — читается при запуске, через API `409`.

Колонка «Прежний флаг» — для миграции; перечисленные флаги удалены (F-1.10).

### 4.1. Сеть и прослушивание

| Ключ JSON | Тип | Умолчание | Допустимо | Применение | Прежний флаг |
|---|---|---|---|---|---|
| `port` | int | `1443` | 1…65535 | **только старт** | `--port` |
| `host` | string | `"127.0.0.1"` | непустая строка | **только старт** | `--host` |
| `publicHost` | string | `""` | пусто, `host`, `host:port`, `[IPv6]:port`; не служебный адрес | горячо | — |
| `maxConnections` | int | `0` | 0…1000000 (0 = без лимита) | горячо | `--max-connections` |
| `proxyProtocol` | bool | `false` | — | слушатель | `--proxy-protocol` |
| `allowIp` | string[] | `[]` | IP или CIDR; пусто = пускаем всех | горячо | `--allow-ip` |
| `denyIp` | string[] | `[]` | IP или CIDR; deny приоритетнее allow | горячо | `--deny-ip` |
| `rateLimitPerIp` | int | `0` | 0…1000000 (0 = выкл.) | горячо | `--rate-limit` |
| `rateLimitWindowSeconds` | int | `60` | 1…86400 | горячо | `--rate-limit-window` |

> `publicHost` — внешний адрес прокси для ссылок `https://t.me/proxy` (F-8.8), а не адрес привязки. Нужен, когда `host` служебный (`0.0.0.0` в контейнере) или прокси за NAT: приложение не может вывести внешний адрес из привязки. Пустое значение — берётся `host` и `port`. Ключ горячий: ссылка собирается при каждом запросе, слушатель не трогается. Валидация (`ConfigValidator.ValidatePublicHost`): длина ≤ 255, без пробелов, служебные адреса (`0.0.0.0`, `::`, `*`, `+`) запрещены, порт 1…65535.

### 4.2. DC и протокол

| Ключ JSON | Тип | Умолчание | Допустимо | Применение | Прежний флаг |
|---|---|---|---|---|---|
| `tokens` | array | `[]` → один токен `default` | 1…64, минимум один `enabled`; схема — § 4.6 | горячо | `--secret` (единственный секрет) |
| `dcIp` | string[] | `[]` → `["2:149.154.167.220","4:149.154.167.220"]` | формат `DC:IP`; `[]` = WS-пул отключён | слушатель | `--dc-ip` |
| `allowInvalidCertificates` | bool | `false` | — | слушатель | `--allow-invalid-certs` |
| `wsMaxFrameBytes` | int | `16777216` (16 MiB) | 1024…67108864 | слушатель | `--ws-max-frame-bytes` |
| `wsConnectTimeoutSeconds` | int | `10` | 1…300 | слушатель | `--ws-timeout` |
| `socketBufferKb` | int | `256` | 4…8192 | слушатель | `--buf-kb` |
| `wsPoolSize` | int | `4` | 0…64 (0 = пул отключён) | слушатель | `--pool-size` |
| `forceTestDc` | bool | `false` | — | слушатель | `--force-test-dc` |
| `fakeTlsDomain` | string | `""` (маскировка выкл.) | домен | слушатель | `--fake-tls-domain` |

> Ключ `secret` удалён: единственный секрет заменён коллекцией `tokens` (§ 2.8). Токены применяются горячо — перезапуск слушателя для них не нужен.

### 4.3. Cloudflare fallback

| Ключ JSON | Тип | Умолчание | Допустимо | Применение | Прежний флаг |
|---|---|---|---|---|---|
| `cfProxyEnabled` | bool | `true` | — | слушатель | `--no-cfproxy` (инверсия) |
| `cfProxyDomains` | string[] | `[]` → встроенный пул | домены; пусто = встроенный пул | слушатель | `--cfproxy-domain` |
| `cfProxyWorkerDomains` | string[] | `[]` | домены | слушатель | `--cfproxy-worker-domain` |
| `cfProxyH2Media` | bool | `true` | — | слушатель | `--no-h2` / `--cfproxy-h2-media` |
| `disableSecure` | bool | `false` | — | слушатель | `--no-secure` |

### 4.4. Логи, веб-привязка, документация

| Ключ JSON | Тип | Умолчание | Допустимо | Применение | Прежний флаг |
|---|---|---|---|---|---|
| `verbose` | bool | `false` | — | **только старт** | `-v` / `--verbose` |
| `logPath` | string | `""` (только консоль) | путь | **только старт** | `--log-file` / `--log-path` |
| `logMaxMegabytes` | double | `5` | 0…4096 | **только старт** | `--log-max-mb` |
| `logRetainedFileCount` | int | `1` | 1…1000 | **только старт** | `--log-backups` |
| `metricsPort` | int | `8081` | 1…65535 (`0` не допускается) | **только старт** | `--metrics-port` |
| `metricsHost` | string | `"127.0.0.1"` | непустая строка | **только старт** | `--metrics-host` |
| `enableSwagger` | bool | `true` | — | **только старт** | — (новый) |

> `metricsPort`/`metricsHost` сохраняют исторические имена, но означают привязку всего веб-приложения (§ 8.2, вопрос о переименовании).

### 4.5. Статистика и Web API

| Ключ JSON | Тип | Умолчание | Допустимо | Применение | Прежний флаг |
|---|---|---|---|---|---|
| `enablePerIpStats` | bool | `true` | — | горячо | `--enable-per-ip-stats` / `--no-per-ip-stats` |
| `perIpStatsTtlSeconds` | int | `1800` | 0…604800 (0 = не удалять) | горячо | `--per-ip-stats-ttl` |
| `perIpStatsMaxPeers` | int | `100000` | 1…10000000 | горячо | `--per-ip-stats-max-peers` |
| `prometheusTopPeers` | int | `200` | 0…10000 (0 = не выгружать per-IP серии) | горячо | — (новый) |
| `webApiToken` | string | `""` | непустая строка включает Bearer | горячо | `--web-api-token` |
| `timeseriesWindowSeconds` | int | `900` | 60…86400 | горячо (буфер пересоздаётся) | `--timeseries-window` |
| `timeseriesStepSeconds` | int | `5` | 1…3600, не больше окна | горячо (буфер пересоздаётся) | `--timeseries-step` |

> Путь к файлу конфигурации **не является** ключом JSON: только `--config` / `TGWS_CONFIG` (F-2.3).
> Производные (только чтение): `h2Enabled = cfProxyH2Media && cfProxyEnabled && !disableSecure && !forceTestDc`.

### 4.6. Схема элемента `tokens`

| Поле | Тип | Умолчание | Допустимо | Изменяемо через API |
|---|---|---|---|---|
| `id` | string | генерируется сервером | стабильный идентификатор | нет |
| `name` | string | `"default"` для первого | 1…64 символа, уникально | да |
| `secret` | string | генерируется при пустом значении | ровно 32 hex, уникально | да (через rotate) |
| `enabled` | bool | `true` | — | да |
| `createdAt` | string (ISO-8601) | момент создания | — | нет |
| `lastUsedAt` | string (ISO-8601) | `null` | — | нет (обновляется при handshake) |

Пример:

```json
{
  "tokens": [
    {
      "id": "default",
      "name": "iPhone Pavel",
      "secret": "0123456789abcdef0123456789abcdef",
      "enabled": true,
      "createdAt": "2026-01-01T10:00:00Z",
      "lastUsedAt": "2026-01-05T12:00:00Z"
    }
  ]
}
```

---

## 5. Приложение Б: контракты API

```
GET  /                          → 200 text/html   (панель)
GET  /swagger                   → 200 text/html   (Swagger UI, без авторизации)
GET  /swagger/v1/swagger.json   → 200 application/json (OpenAPI 3.0)
GET  /healthz                   → 200             (liveness, компактный JSON)
GET  /healthz/ready             → 200 | 503       (readiness, формат UI.Client)
GET  /metrics                   → 200 text/plain  (Prometheus, registry prometheus-net)
GET  /api/info                  → 200 InfoDto
GET  /api/metrics               → 200 MetricsDto
GET  /api/metrics/timeseries    → 200 TimeseriesDto
GET  /api/metrics/by-token      → 200 TokenUsageDto[]
GET  /api/per-ip?offset&limit&q&group&token → 200 PerIpPageDto
GET  /api/tokens                → 200 TokenDto[]   (секреты маскированы)
POST /api/tokens                → 201 TokenDto     (секрет отдаётся однократно)
PUT  /api/tokens/{id}           → 200 TokenDto
POST /api/tokens/{id}/rotate    → 200 TokenDto     (новый секрет однократно)
GET  /api/tokens/{id}/link      → 200 TokenLinkDto (секрет + ссылка t.me, no-store)
DELETE /api/tokens/{id}         → 200 {closedSessions}
GET  /api/config                → 200 ConfigDto    (секреты маскированы)
PUT  /api/config                → 200 | 400 | 409 ConfigUpdateResultDto
POST /api/config/reset          → 200 ConfigDto
```

`MetricsDto` — без изменений относительно предыдущей редакции (включает `trackedPeers`, `peerTrackingOverflow`, `h2Enabled`).

`InfoDto` дополняется:

```json
{
  "version": "3.0",
  "uptimeSeconds": 8231,
  "listenerHost": "0.0.0.0",
  "listenerPort": 1443,
  "listenerState": "listening",
  "listenerRestarts": 2,
  "lastListenerRestart": "2026-01-01T10:40:02Z",
  "tokensTotal": 4,
  "tokensEnabled": 3
}
```

`listenerHost` и `listenerPort` — действующая привязка (значения живого `Config`, которые
меняются только после успешного перезапуска слушателя, F-7.3). `listenerState` — производное
состояние привязки: `listening` / `restarting` / `unavailable` (F-7.7, F-7.8) — по нему панель
отличает перезапуск привязки от падения процесса.

Элемент `GET /api/tokens` (секрет маскирован):

```json
{
  "id": "default",
  "name": "iPhone Pavel",
  "secret": "0123…cdef",
  "enabled": true,
  "createdAt": "2026-01-01T10:00:00Z",
  "lastUsedAt": "2026-01-05T12:00:00Z",
  "activeSessions": 2,
  "totalConnections": 128,
  "bytesUp": 10485760,
  "bytesDown": 104857600
}
```

Элемент `GET /api/per-ip?group=pair`:

```json
{
  "ip": "203.0.113.7",
  "tokenId": "default",
  "tokenName": "iPhone Pavel",
  "activeConnections": 3,
  "totalConnections": 128,
  "bytesUp": 10485760,
  "bytesDown": 104857600,
  "wsErrors": 0,
  "rejected": 2,
  "poolHits": 110,
  "poolMisses": 18,
  "firstSeen": "2026-01-01T10:00:00Z",
  "lastSeen": "2026-01-01T10:42:11Z"
}
```

`GET /healthz/ready` при перезапуске слушателя (503, формат `UI.Client`):

```json
{
  "status": "Degraded",
  "totalDuration": "00:00:00.0123456",
  "entries": {
    "self": { "status": "Healthy", "duration": "00:00:00.0001200" },
    "proxy-listener": {
      "status": "Degraded",
      "duration": "00:00:00.0002000",
      "data": { "state": "restarting" }
    },
    "config": { "status": "Healthy", "duration": "00:00:00.0003100" }
  }
}
```

`GET /healthz` (liveness):

```json
{ "status": "ok", "listening": true, "uptimeSeconds": 8231, "version": "3.0" }
```

Фрагмент `/metrics` после миграции на `prometheus-net`:

```
# HELP tgwsproxy_connections_total Total accepted proxy connections.
# TYPE tgwsproxy_connections_total counter
tgwsproxy_connections_total 10432
# HELP healthcheck_status Health check status (1 = healthy, 0 = unhealthy).
# TYPE healthcheck_status gauge
healthcheck_status{name="proxy-listener"} 1
# HELP tgws_peer_bytes_up_total Bytes sent per client IP and token.
# TYPE tgws_peer_bytes_up_total counter
tgws_peer_bytes_up_total{ip="203.0.113.7",token="iPhone Pavel"} 10485760
```

Пример успешного `PUT /api/config` (200):

```json
{
  "saved": true,
  "path": "/app/config/config.json",
  "hotReloaded": ["verbose", "maxConnections", "rateLimitPerIp"],
  "listenerRestarted": true,
  "droppedSessions": 3,
  "warnings": []
}
```

Отказ по ключам «только старт» (409):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Configuration keys require a process restart",
  "status": 409,
  "detail": "These keys are read once at startup and cannot be changed at runtime.",
  "startupOnly": ["metricsPort", "metricsHost"]
}
```

Отказ перепривязки слушателя (409, с откатом):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Listener rebind failed",
  "status": 409,
  "detail": "Address already in use (0.0.0.0:1443). Previous binding restored.",
  "rolledBack": true
}
```

---

## 6. Приложение В: план реализации

### Этап 1. Удаление CLI-конфигурации

1. `CliParser` → `BootstrapOptions` (`--config`, `-h`/`--help`, `--version`); разбор конфигурационных флагов удаляется.
2. Неизвестный аргумент → подсказка «прежний флаг → ключ JSON» и код `1`.
3. `ConfigValidator`: `metricsPort` — `1…65535`; удаление ветки «0 = веб выключен».
4. Очистка `launchSettings.json`.
5. Тесты: неизвестный флаг, bootstrap-опции, `metricsPort = 0` невалиден.

**Проверка:** старт только из `config.json`, прежние флаги дают понятную ошибку.

### Этап 2. Категоризация ключей и `409`

1. Перечисление категорий применения — единый источник истины, согласованный с § 4.
2. `PUT /api/config`: изменённые ключи «только старт» → `409` со списком `startupOnly`; неизменённые игнорируются.
3. Удаление `requiresRestart` из `ConfigUpdateResult` и API.
4. Тесты: `409` при изменении `metricsPort`, отсутствие `409` при полной форме без изменений.

**Проверка:** форма сохраняется целиком без ложных отказов.

### Этап 3. Реестр активных сессий

1. `IActiveSessionRegistry`: регистрация сокета при accept, снятие при завершении, `Count`, `CloseAll()`, `CloseByToken(tokenId)`.
2. Подключение в `ProxyServer` рядом с per-IP учётом.
3. Тесты: конкурентная регистрация/снятие, `CloseAll()`, `CloseByToken()`.

**Проверка:** число живых сессий из API совпадает с фактическим.

### Этап 4. Перезапуск слушателя

1. `RestartAsync(candidate, ct)` по шагам F-7.2, `SemaphoreSlim` (single-flight), таймаут 10 с.
2. Состояние `Restarting`/`Listening` в `ProxyServer`, отражение в readiness и `/api/info`.
3. Откат при неудаче привязки.
4. Логирование события (F-7.8).
5. Тесты: смена ключа «слушатель» (`wsPoolSize`) без рестарта процесса, откат при занятом порте, single-flight, таймаут.

**Проверка:** `wsPoolSize` меняется из API, `uptimeSeconds` не сбрасывается, при занятом порте прокси слушает старый.

### Этап 5. Транзакционное применение

1. Порядок: валидация → запись файла → применение; при ошибке применения — откат файла.
2. Генерация секрета при пустом значении (F-2.15).
3. Горячее применение `enablePerIpStats`/`perIpStatsTtlSeconds`/`perIpStatsMaxPeers`/`prometheusTopPeers`; пересоздание буфера временного ряда при смене `timeseries*`.
4. Тесты: откат файла при неудачном перезапуске, предупреждения, горячие переключатели.

**Проверка:** после неудачного применения файл и память согласованы.

### Этап 6. Панель (базовая)

1. Поля «только старт» — read-only (F-5.16).
2. Подтверждение перед сохранением «слушатель»-ключей (F-5.17).
3. Состояние «перезапуск…», устойчивость к кратковременному `503` (F-5.18).
4. Отчёт после сохранения (F-5.10).

**Проверка:** смена порта из панели проходит без перезапуска контейнера.

### Этап 7. Токены доступа

1. Модель `ProxyToken`, `Config.Tokens`, DTO, миграция `secret` → `tokens` (F-8.3).
2. `BuildCryptoContext`/`TryHandshake` в цикле по включённым токенам, порядок по `lastUsedAt` (F-8.4); `tokenId` в `ClientContext`.
3. CRUD API `/api/tokens` + ротация с однократной выдачей секрета (F-8.8).
4. `CloseByToken` при выключении/удалении (F-8.6); `LogLinks` по ссылке на токен (F-8.7).
5. Тесты: выбор правильного токена среди нескольких, отказ при неизвестном секрете, уникальность, миграция, ротация, закрытие сессий.

**Проверка:** два устройства с разными токенами работают одновременно, каждый виден отдельно.

### Этап 8. Учёт трафика по (IP, токен)

1. Ключ реестра `(IPAddress, tokenId)`; `PeerStatsSnapshot` + `tokenId`/`tokenName`.
2. Протяжка `tokenId` в точки учёта байтов (F-9.2).
3. `?group=` и `?token=` в `/api/per-ip`; `GET /api/metrics/by-token`.
4. Per-token метки в Prometheus с ограничением `prometheusTopPeers`.
5. Тесты: суммы по паре сходятся с глобальными, агрегация `group=ip|token|pair`, ограничение кардинальности.

**Проверка:** трафик корректно разделяется между устройствами и токенами.

### Этап 9. Наблюдаемость: health checks, prometheus-net, Swagger

1. `AddHealthChecks()` + проверки `self`, `proxy-listener`, `config`, `stats-registry`; `/healthz` (liveness) и `/healthz/ready` (readiness, `UI.Client`).
2. `AddPrometheusHealthCheckPublisher()`; публикация `healthcheck_status`/`healthcheck_duration_seconds`.
3. Миграция `/metrics` на `Metrics.DefaultRegistry`: прикладные счётчики как `Counter`/`Gauge`, суффикс `_total`, удаление `PrometheusFormatter` и переработка `PrometheusFormatterTests`.
4. Swashbuckle: `/swagger`, XML-комментарии, security scheme `bearer`, ключ `enableSwagger`.
5. Тесты: коды и тела health-эндпоинтов, наличие `healthcheck_status`, имена метрик, доступность спецификации.

**Проверка:** `/swagger` открывается, `/healthz/ready` различает состояния, `/metrics` содержит health-метрики.

### Этап 10. Панель (токены и трафик), Docker, документация

1. Блок «Токены доступа»: таблица, добавление/переименование/выключение/удаление/ротация, однократный показ секрета и ссылки (F-5.19…F-5.22).
2. Переключатель разреза в блоке per-IP (IP / токен / пара).
3. `docker/healthcheck.sh` → только `/healthz/ready`, `--retries` под окно перезапуска.
4. `docker-compose.yml`: убрать конфигурационные флаги из `command:`, оставить `TGWS_CONFIG`.
5. `config.example.json`: `tokens`, `enableSwagger`, `prometheusTopPeers`; `secret` удалён.
6. `README.md`: миграция 2.x → 3.0 (флаги → ключи, `secret` → `tokens`, имена метрик), удаление описания консольного режима.
7. Версия — `3.0.2-beta2`.

**Проверка:** `docker compose up` → панель доступна, токены управляются из UI, конфиг переживает пересоздание.

---

## 7. Критерии приёмки

1. Приложение запускается **только** как веб-приложение: консольного режима нет, `metricsPort = 0` отвергается, `GET /` открывается.
2. Конфигурация читается только из `config.json`; любой конфигурационный CLI-флаг завершает процесс с кодом `1` и подсказкой.
3. Все ключи § 4 доступны на странице и через API, кроме «только старт» — они read-only.
4. Изменение «горячих» ключей применяется без разрыва соединений.
5. Изменение «слушатель»-ключей перезапускает слушатель **в процессе**: порт меняется, `/api/info` показывает новую привязку, `uptimeSeconds` не сбрасывается, сессии разорваны, их число возвращено.
6. Неудачный перезапуск откатывается: прокси слушает прежний порт, файл содержит прежние значения, API вернул `409` с `rolledBack: true`.
7. Изменение `metricsPort`/`metricsHost`/`enableSwagger`/`logPath`/`logMaxMegabytes`/`logRetainedFileCount` через API отклоняется с `409`.
8. **Swagger**: `GET /swagger` открывается, `GET /swagger/v1/swagger.json` валиден, содержит схемы `ConfigDto`, `TokenDto`, `MetricsDto`, `PerIpPageDto`, `ProblemDetails` и security scheme `bearer`.
9. **Health checks**: `/healthz` — `200` liveness; `/healthz/ready` — `200` в норме, `503 Degraded` при перезапуске слушателя, `503 Unhealthy` при мёртвом листенере; тело в формате `UI.Client`.
10. **Prometheus**: `/metrics` формируется `prometheus-net`, содержит `healthcheck_status{name=...}`, прикладные счётчики с суффиксом `_total` и per-IP/per-token серии; число серий ограничено `prometheusTopPeers`.
11. **Несколько токенов**: два и более устройств с разными токенами подключаются одновременно; неверный секрет даёт `connectionsBad`; `tokensTotal`/`tokensEnabled` видны в `/api/info`.
12. **Управление токенами со страницы**: создать, переименовать, выключить, удалить, ротировать; кнопка «Ссылка» копирует готовую `t.me/proxy`-ссылку (полный секрет берётся из `GET /api/tokens/{id}/link` с `no-store`); при редактировании поле секрета подставлено текущим значением; `GET /api/tokens` возвращает маску. Внешний адрес ссылки задаётся полем `publicHost` и применяется без перезапуска: при `host = 0.0.0.0` без `publicHost` ссылка не строится, с `publicHost = proxy.example.com` — строится.
13. **Трафик по (IP, токен)**: `/api/per-ip?group=pair` и `/api/metrics/by-token` показывают разделение по токенам; суммы по парам сходятся с глобальными счётчиками.
14. Выключение или удаление токена разрывает его активные сессии и возвращает их число; сессии других токенов не затрагиваются.
15. `docker compose up` с bind-mount: конфиг вне контейнера, переживает пересоздание, конфигурация не передаётся флагами, `HEALTHCHECK` использует `/healthz/ready`.
16. Секреты токенов и `webApiToken` не утекают через GET API, журналы, метки Prometheus и спецификацию OpenAPI.
17. Отказ привязки Web API при старте завершает процесс с кодом `1`.
18. Все существующие тесты зелёные; новый функционал покрыт тестами.

---

## 8. Решения и открытые вопросы

### 8.1. Решения, принятые в этой редакции

1. **Консольный режим удалён полностью.** `metricsPort = 0` не выключает веб-слой.
2. **CLI сокращён до bootstrap:** `--config`, `--help`, `--version`; конфигурационные флаги удалены.
3. **Привязка Web API (`metricsPort`/`metricsHost`) меняется только при старте** — из UI запрещена (`409`): страница не должна иметь возможности заблокировать сама себя.
4. **Активные сессии при перезапуске слушателя разрываются немедленно.** Дренирование не реализуется.
5. **`requiresRestart` удалён из контракта** — три категории применения (F-2.11) и факт `listenerRestarted`.
6. **Отказ привязки Web API при старте фатален** (код `1`): без веб-слоя процесс неуправляем.
7. **Логирование и `enableSwagger` — «только старт»:** пересоздание Serilog-синков разделило бы журнал между файлами, а регистрация Swagger-промежуточного ПО происходит при сборке приложения.
8. **Токены — это секреты доступа прокси (MTProto), а не токены панели.** `webApiToken` остаётся единственным и отвечает только за доступ к `/api/*`. Один и тот же список для панели и прокси не используется: у панели и у устройств разные модели угроз и разный жизненный цикл.
9. **Несколько токенов реализуются перебором при handshake** (F-8.4): секрет известен только клиенту, поэтому прокси выводит ключи для каждого включённого токена и проверяет handshake. Порядок — по `lastUsedAt` убыв., предел — 64 попытки.
10. **Токены применяются горячо**, без перезапуска слушателя: они влияют только на валидацию новых handshake.
11. **Выключение/удаление токена разрывает его сессии** (F-8.6) — иначе «отозванный» токен продолжал бы работать до естественного закрытия.
12. **Секрет отдаётся в открытом виде ровно один раз** — при создании и ротации (F-8.8), с `Cache-Control: no-store`. Это узкое исключение из F-2.10, без него оператор не сможет скопировать ссылку.
13. **`/metrics` переведён на `prometheus-net` целиком** (единый registry). Следствие — суффикс `_total` у счётчиков и удаление `PrometheusFormatter`; имена метрик меняются, это часть мажорного релиза 3.0.0.
14. **Health-эндпоинты разделены**: `/healthz` — liveness (процесс жив), `/healthz/ready` — readiness (листенер и подсистемы). Docker `HEALTHCHECK` использует readiness, чтобы мёртвый листенер считался нездоровым, а окно перезапуска перекрывалось `--retries`.
15. **`AspNetCore.HealthChecks.UI` (дашборд) не подключается** — только `UI.Client` для формата ответа; отдельный UI-дашборд дублировал бы нашу панель.
16. **В метках Prometheus используется имя токена, а не секрет** (F-9.6, N-8).
17. **Кардинальность ограничена `prometheusTopPeers`** (F-9.6): пара (IP, токен) — потенциально большое произведение, экспортируются только топ-пары по трафику.
18. **`port`, `host` и `verbose` — «только старт»** (F-5.16): смена порта/адреса через панель означала бы, что оператор может одним запросом отрезать себя от прокси, а уровень логирования задаётся при сборке логгера. Разделы «Сеть и Web API» и «Журналирование» поэтому справочные (F-5.23); остальные ключи «слушатель» по-прежнему перезапускают листенер внутри процесса (F-7.1).
19. **Внешний адрес ссылок — отдельный ключ `publicHost`, а не `host`** (F-5.27, § 4.1). Адрес привязки и адрес, по которому клиенты заходят снаружи, — разные вещи: в контейнере `host` обязан быть `0.0.0.0`, и вывести из него домен или публичный IP невозможно. Поэтому ссылка `t.me/proxy` строится из `publicHost`, а `host` остаётся только адресом прослушивания. Ключ горячий: он влияет лишь на формируемый текст ссылки, слушатель не перезапускается.

### 8.2. Открытые вопросы

1. **Имена `metricsPort`/`metricsHost`** — оставлены ради совместимости, хотя означают привязку всего приложения. Переименовать в `webPort`/`webHost` в 3.1 с приёмом старых имён как алиасов?
2. **История метрик** — in-memory ring buffer достаточно или нужна опциональная запись в файл для графиков за сутки?
3. **`dcIp` без значения** семантически очищает список (отключает WS-пул). В JSON это `"dcIp": []`, что конфликтует с «пусто = умолчания upstream». Предложение: `[]` = «как в CLI без значения», отсутствие ключа = умолчания.
4. **Нужен ли `--config`**, если есть `TGWS_CONFIG`? Оставить оба (текущее решение) или только переменную окружения?
5. **Опциональный режим «не рвать сессии»** при выключении токена (дренирование с таймаутом) — нужен ли как отдельная настройка?
6. **Лимиты на токен** — нужны ли per-token ограничения (`maxConnections`, `rateLimitPerIp` для отдельного устройства), или лимиты остаются глобальными по IP?
7. **QR-код** для `tg://`-ссылки на странице — генерировать локально (vanilla JS, без CDN) или ограничиться копированием текста?
8. **Группы токенов** — потребуется ли объединение токенов в группы с общими лимитами и агрегированной статистикой?
