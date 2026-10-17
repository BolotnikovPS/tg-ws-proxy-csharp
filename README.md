# TG WS Proxy (C#)

Локальный MTProto-прокси для Telegram на C#/.NET 8: маршрутизирует трафик через WebSocket/TLS к дата-центрам Telegram, умеет маскироваться под обычный HTTPS (Fake TLS) и автоматически переключается на резервные транспорты (Cloudflare Worker → Cloudflare → TCP) при проблемах с WS.

Проект — порт Python-реализации [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), актуализированный до состояния upstream v1.11.2 и переработанный в **web-only сервис**: единственный способ запуска — ASP.NET Core хост, единственный интерфейс настройки — страница панели.

## Возможности

- Локальный MTProto-сервер (по умолчанию `127.0.0.1:1443`).
- Определение Telegram-трафика и извлечение DC из init-пакета MTProto (abridged / intermediate / padded intermediate — «secure»).
- Основной транспорт — WSS к `/apiws` (или `/apiws_test` для тестовых DC).
- **Пул WS-соединений** per (DC, media): предварительный прогрев, TTL 120 с, периодическая чистка, refill с экспоненциальным backoff (1 с → 3600 с).
- **TCP fallback** с экспоненциальным backoff (30 с → 3600 с).
- **Cloudflare fallback**: балансировщик CF-доменов (20 встроенных «обфусцированных» доменов, автообновление раз в час с GitHub), пул Cloudflare Worker-доменов и прямые CF-proxy подключения.
- **HTTP/2 мультиплексирование media-трафика** через Cloudflare (`/api`): переиспользование одного H2-соединения каналами (lane), учёт бюджета байт/запросов, восстановление потерянных пакетов (replay/recovery).
- **Fake TLS (ee-secret) маскировка**: приём TLS ClientHello, ответ ServerHello + ChangeCipherSpec, упаковка MTProto в TLS application data; при невалидном секрете — проксирование на домен-маскировку; при обычном HTTP — редирект 301.
- **Несколько токенов доступа** (MTProto secret, 32 hex): каждое устройство или группа получает свой секрет и свою `tg://`-ссылку; CRUD и ротация — из панели или API, без правки файла руками.
- **PROXY protocol v1** (работа за nginx/haproxy), управление доступом по IP (CIDR), лимит одновременных сессий и per-IP rate limit.
- Watchdog слушающего сокета: раз в 5 с проверяет живость сокета и перезапускает сервер.
- **Перезапуск слушателя на уровне приложения**: смена ключей категории «слушатель» (`dcIp`, `wsPoolSize`, `cfProxy*`, `fakeTlsDomain`, `proxyProtocol`, `disableSecure`, `socketBufferKb`, `wsMaxFrameBytes`, `wsConnectTimeoutSeconds`, `forceTestDc`, `allowInvalidCertificates`) перезапускает листенер внутри процесса, без рестарта контейнера и без потери Web API. `port`/`host` — «только старт»: меняются в `config.json`.
- **Веб-панель** (`/`): обзор, график скорости, распределение по типам соединений, состояние пулов, таблица трафика по источникам (IP / токен / пара), управление токенами, форма конфигурации с классификацией ключей.
- **Статистика по источникам трафика**: пары «IP устройства + токен» — активные и суммарные соединения, байты в обе стороны, ошибки WS, попадания в пул и отказы.
- **Страница метрик `/metrics`** на базе `prometheus-net` (единый registry): `http://127.0.0.1:8081/metrics` по умолчанию, текст формата Prometheus без авторизации; **health checks** через `AspNetCore.HealthChecks.UI.Client` + `prometheus-net.AspNetCore.HealthChecks`, **Swagger/OpenAPI** (`/swagger`).
- **Опциональная bearer-аутентификация** для `/api/*` (`webApiToken`); по умолчанию сервер слушает только loopback.

## Архитектура

| Проект | Назначение |
|---|---|
| `TgWsProxy.Domain` | Чистая модель без внешних зависимостей: конфигурация (`Config`), токены (`ProxyToken`, `SecretMask`), модель handshake (`MtProtoHandshake`), снимки статистики (`StatsSnapshot`, `PeerStatsSnapshot`), исключения. |
| `TgWsProxy.Application` | Сценарии и абстракции: bootstrap-опции и валидация конфига, загрузка/сохранение `config.json` (`ConfigFileLoader`, `ConfigDto`, `ConfigDtoMapper`), применение изменений (`ConfigUpdateService`), реестры (`ProxyTokenRegistry`, `PeerStatsRegistry`), статистика (`ProxyStats`), лимиты (`ConnectionLimiter`, `PeerRateLimiter`), ACL (`PeerAccessControl`), порты (`IClientSessionHandler`, `IMtProtoInspector`, `IActiveSessionRegistry`, `IListenerController`, `IProxyServer`). |
| `TgWsProxy.Infrastructure` | Сетевые реализации: `ProxyServer` (+ watchdog, `RebindAsync`), `RawWebSocket(Factory)`, `WsPool`, `CfWorkerPool`, `CfDomainBalancer`/`CfDomainRefresher`, `CfH2Pool`/`CfH2Lane`/`CfH2Channel`/`H2Bridge`, `TcpBridgeService`, I/O и протокол (`FakeTls/`, `IO/`, `MtProto/`, `Session/`, `Wire/`), хостинг (`ProxyHostedService` как `IListenerController`), кольцевой буфер метрик (`MetricsHistory`). |
| `TgWsProxy` | Хост ASP.NET Core: DI, логирование с ротацией, Web API (`Web/`), Prometheus-метрики (`ProxyMetrics`), health checks (`ProxyHealthChecks`), статика и панель (`wwwroot/`), graceful shutdown. |
| `TgWsProxy.Test` | xUnit-тесты (224 шт.): bootstrap/Config, протокол, Fake TLS, H2-пакеты, WS-кадрирование, регрессия порядка перешифрования, liveness пула, токены, Web API, hosted-сервис, DI. |

## Как это работает

```text
Telegram Client -> MTProto proxy (этот сервис) -> WSS/CF-H2/TCP -> Telegram DC
```

Порядок выбора транспорта для каждого нового подключения:

1. **Пул прямых WS** (если пул не отключён и DC не в «cooldown» после ошибки).
2. **Cloudflare Worker** (`cfProxyWorkerDomains`) — если заданы.
3. **Cloudflare**: для media-трафика — HTTP/2 мультиплексирование на `/api` (если H2 доступен), иначе WS через CF-proxy домен.
4. **TCP fallback** на IP дата-центра (с экспоненциальным backoff при повторных неудачах).

Для каждого соединения MTProto-трафик перешифровывается: расшифровка ключом выбранного токена и шифрование ключом Telegram (и наоборот в обратную сторону).

## Запуск

Единственный режим — web-only. Консольного прокси без Web-части больше нет: HTTP-сервер поднимается всегда, панель и API живут в том же процессе.

```bash
dotnet TgWsProxy.dll --config /etc/tg-ws-proxy/config.json
```

Аргументы командной строки — только bootstrap:

| Аргумент | Описание |
|---|---|
| `--config <PATH>` | Путь к файлу конфигурации (алиас `--config=PATH`). Также читается из `TGWS_CONFIG`. По умолчанию `./config.json`. |
| `-h`, `--help` | Справка по bootstrap-аргументам и ключам `config.json`; выход с кодом `0`. |
| `--version` | Версия (`tg-ws-proxy <версия>`); выход с кодом `0`. |

Любой другой флаг (`--port`, `--secret`, `--metrics-port`, `--verbose`…) завершает работу с кодом `1` и подсказкой, какой ключ `config.json` соответствует старому флагу (например `--port` → `"port"`). Настройки задаются **только** в файле или через панель.

## Файл конфигурации (`config.json`)

`config.json` — единственный источник настроек. Аргументы CLI ключи не перекрывают. Путь ищется так: `--config <PATH>` → переменная окружения `TGWS_CONFIG` → `./config.json`. Отсутствие файла — не ошибка (работают значения по умолчанию); синтаксическая ошибка, неизвестный ключ или значение вне диапазона приводят к завершению с кодом `1` с указанием строки.

В файле допустимы комментарии (`//`, `/* */`) и висячие запятые; ключи — camelCase, регистр не важен. Полный список с диапазонами — [`config.example.json`](config.example.json) и `--help`.

> Приложение перезаписывает файл при сохранении из панели/API, а также при миграции старого `secret` в `tokens[]` или автогенерации токена `default` для пустого списка — **комментарии и форматирование при этом теряются** (пишется компактный JSON). Держите копию образца отдельно.

### Ключи

| Ключ | По умолчанию | Категория | Описание |
|---|---|---|---|
| `port` | `1443` | только старт | Порт локального MTProto-сервера. |
| `host` | `127.0.0.1` | только старт | Адрес прослушивания. |
| `metricsHost` | `127.0.0.1` | только старт | Адрес привязки Web API и панели. |
| `metricsPort` | `8081` | только старт | Порт Web API и панели, диапазон `1…65535`. «Выключить Web-часть» нельзя (F-1.4). |
| `enableSwagger` | `true` | только старт | Swagger UI (`/swagger`) и `/swagger/v1/swagger.json`. |
| `webApiToken` | пусто | горячо | Bearer-токен для `/api/*`. Пусто — аутентификации нет (допустимо только на loopback). |
| `tokens` | `[]` → создаётся `default` | горячо | Список токенов доступа прокси, 1…64: `{id, name, secret (32 hex), enabled, createdAt, lastUsedAt}`. |
| `dcIp` | `2:…`, `4:…` | слушатель | Сопоставление DC → IP. Пустой список отключает WS-пул и прямые WS. |
| `wsConnectTimeoutSeconds` | `10` | слушатель | Таймаут WS-рукопожатия; этапы TCP/TLS — не больше 10 с. |
| `wsMaxFrameBytes` | `16777216` | слушатель | Максимальный payload WS-фрейма (защита от DoS). |
| `socketBufferKb` | `256` | слушатель | `SO_RCVBUF`/`SO_SNDBUF` клиентского и исходящего сокета. |
| `wsPoolSize` | `4` | слушатель | Размер пула WS на пару (DC, media); `0` отключает прямые WS. |
| `maxConnections` | `0` | горячо | Максимум одновременных сессий; `0` — без лимита. |
| `rateLimitPerIp` | `0` | горячо | Лимит новых подключений с одного IP за окно; `0` — выключено. |
| `rateLimitWindowSeconds` | `60` | горячо | Длина окна rate limit. |
| `allowIp` / `denyIp` | `[]` | горячо | Списки IP/CIDR; `deny` приоритетнее `allow`. |
| `enablePerIpStats` | `true` | горячо | Учёт статистики по парам «IP + токен». |
| `perIpStatsTtlSeconds` | `1800` | горячо | Время жизни простаивающей записи; `0` — не удалять. |
| `perIpStatsMaxPeers` | `100000` | горячо | Максимум отслеживаемых источников; сверх лимита растёт `peerTrackingOverflow`. |
| `prometheusTopPeers` | `200` | горячо | Сколько источников отдавать в `/metrics` отдельными сериями (`0` — ни одной). |
| `timeseriesWindowSeconds` | `900` | горячо | Окно графика по умолчанию для `/api/metrics/timeseries`. |
| `timeseriesStepSeconds` | `5` | горячо | Шаг записи в кольцевой буфер и выборки истории. |
| `verbose` | `false` | только старт | Подробное логирование (`Debug`). Уровень задаётся при сборке логгера. |
| `logPath` | пусто | только старт | Файл лога с ротацией; пусто — только консоль. |
| `logMaxMegabytes` / `logRetainedFileCount` | `5` / `1` | только старт | Лимит размера файла и число архивов. |
| `cfProxyEnabled` / `cfProxyH2Media` | `true` / `true` | слушатель | Cloudflare fallback и H2-мультиплексирование media. |
| `cfProxyDomains` / `cfProxyWorkerDomains` | `[]` | слушатель | Свои CF-proxy и CF Worker домены (пусто — встроенный список). |
| `disableSecure` | `false` | слушатель | Использовать порт 80 вместо 443 для CF. |
| `fakeTlsDomain` | пусто | слушатель | Включает Fake TLS (`ee…`) маскировку под указанный домен. |
| `proxyProtocol` | `false` | слушатель | Принимать PROXY protocol v1. |
| `forceTestDc` | `false` | слушатель | Весь трафик в тестовые DC. |
| `allowInvalidCertificates` | `false` | слушатель | Разрешать TLS с невалидным сертификатом (только для диагностики). |

### Как применяются изменения

Ключи делятся на три категории (видно в панели по бейджу рядом с полем и в ответе `PUT /api/config`):

* **горячо** — применяются в работающем процессе сразу (`hotReloaded`);
* **слушатель** — требуют перезапуска листенера внутри приложения (`listenerRestarted`): останавливается приём, закрываются активные сессии (`droppedSessions`), открывается новый сокет, затем применяется остальная конфигурация;
* **только старт** — меняются лишь при перезапуске процесса; такой `PUT` отклоняется с `409` и полем `startupOnly`, файл на диске не меняется. В панели это разделы «Сеть и Web API» и «Журналирование»: они справочные (метка — значение, без полей ввода), потому что все их ключи (`port`, `host`, `metricsPort`, `metricsHost`, `enableSwagger`, `verbose`, `logPath`, `logMaxMegabytes`, `logRetainedFileCount`) относятся к «только старт».

Порядок записи: значения с маской (`abcd…ef01`) означают «не менять» → слияние с текущей конфигурацией → валидация → запись во временный файл и атомарное переименование → применение. Если новый листенер не смог занять порт, конфигурация и файл откатываются, ответ — `409` с `rolledBack: true`, а старый сокет продолжает работать.

## Web API и веб-панель

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/` | Веб-панель (статика без CDN и сборщика). |
| `GET` | `/healthz` | Liveness: `200` и `{"status":"ok","listening":…}`, `503` при `restarting`/`unavailable`. Без аутентификации. |
| `GET` | `/healthz/ready` | Readiness: проверки `proxy-listener`, `config`, `stats-registry`; формат `AspNetCore.HealthChecks.UI.Client`, `200` (Healthy) или `503` (Degraded/Unhealthy). |
| `GET` | `/metrics` | Prometheus-метрики (`prometheus-net`): `tgwsproxy_*_total`, `tgws_peer_*` с метками `ip` и `token`, `healthcheck_status`, `healthcheck_duration_seconds`. |
| `GET` | `/swagger`, `/swagger/v1/swagger.json` | Swagger UI и OpenAPI-схема (`enableSwagger`). |
| `GET` | `/api/info` | Версия, uptime, состояние листенера, счётчики токенов. |
| `GET` | `/api/metrics` | Снимок счётчиков (JSON). |
| `GET` | `/api/metrics/timeseries?window=&step=` | Временной ряд из кольцевого буфера. |
| `GET` | `/api/metrics/by-token` | Агрегаты трафика по токенам. |
| `GET` | `/api/per-ip?group=ip\|token\|pair&token=&q=&offset=&limit=` | Трафик по источникам: группировка, фильтр по токену, `q` — префикс адреса, `limit` до 500. |
| `GET` | `/api/tokens` | Список токенов; секреты замаскированы. |
| `POST` | `/api/tokens` | Создать токен; единственный раз возвращает полный секрет (`Cache-Control: no-store`). |
| `PUT` | `/api/tokens/{id}` | Переименовать/включить/выключить/сменить секрет. Выключение закрывает сессии этого токена. |
| `DELETE` | `/api/tokens/{id}` | Удалить токен (нельзя удалить последний включённый) и закрыть его сессии. |
| `POST` | `/api/tokens/{id}/rotate` | Новый секрет; возвращается один раз, старый перестаёт работать. |
| `GET` | `/api/tokens/{id}/link` | Полный секрет и готовая ссылка `t.me/proxy` для копирования (`Cache-Control: no-store`). |
| `GET` | `/api/config` | Текущая конфигурация; секреты замаскированы. |
| `PUT` | `/api/config` | Частичное обновление (только изменяемые ключи). |
| `POST` | `/api/config/reset` | Значения по умолчанию (без записи на диск). |

**Безопасность.** По умолчанию сервер слушает только `127.0.0.1` — этого достаточно для локального доступа и SSH-туннеля. Если открыть панель наружу (`metricsHost: "0.0.0.0"`), **обязательно задайте `webApiToken`**: иначе при старте в лог уйдёт предупреждение, а читать метрики и менять конфигурацию сможет любой, кто дотянется до порта. При заданном токене все `/api/*` требуют `Authorization: Bearer <token>` (сравнение постоянного времени); `/healthz`, `/healthz/ready`, `/metrics`, `/swagger` и сама страница открыты. Секреты (`webApiToken`, `tokens[].secret`) никогда не отдаются в открытом виде — только маска `abcd…ef01`, кроме создания токена и ротации; маска, отправленная обратно, трактуется как «не менять».

**Токены и ссылки.** При старте без токенов создаётся `default`; старый ключ `secret` в файле автоматически мигрирует в `tokens[]`. В лог пишется по одной `tg://`-ссылке на включённый токен (секрет маскируется). В панели — таблица токенов с состоянием, трафиком, числом сессий и действиями «Ссылка», «Изменить», «Ротация», «Включить/Отключить», «Удалить». Кнопка «Ссылка» копирует готовую конфигурацию `https://t.me/proxy?server=…&port=…&secret=dd…` (полный секрет запрашивается через `GET /api/tokens/{id}/link` и в состояние панели не попадает); для служебного `host` (`0.0.0.0`, `::`) ссылка не строится — панель подсказывает указать внешний адрес. В режиме «Изменить» поле секрета подставлено текущим значением: пустое — не менять, новое — смена секрета.

**Учёт по источникам.** Ключ записи — пара (IP устройства, токен): IPv4-mapped IPv6 нормализуется, порт отбрасывается. Считаются активные и суммарные соединения, байты в обе стороны, ошибки WS, попадания в пул и отказы (ACL/лимит/rate-limit). Записи живут `perIpStatsTtlSeconds`, количество ограничено `perIpStatsMaxPeers`, при переполнении растёт `peerTrackingOverflow`. Счётчики обновляются без блокировок (`Interlocked`).

**Панель** опрашивает API раз в 2 секунды (и приостанавливает опрос в скрытой вкладке): обзор, график скорости, распределение по типам соединений, состояние пулов, трафик по источникам с переключателем группировки (пара / IP / токен), управление токенами, форма конфигурации с бейджами категорий и подтверждением при перезапуске слушателя. Тема — светлая/тёмная/системная, интерфейс следует `prefers-reduced-motion`.

## Страница метрик `/metrics`

Метрики отдаёт тот же веб-сервер, что и панель, — отдельного процесса и отдельного порта нет. Адрес складывается из ключей `metricsHost` и `metricsPort` (по умолчанию `127.0.0.1` и `8081`):

| Как открыть | Команда / адрес |
|---|---|
| Браузер (локально) | <http://127.0.0.1:8081/metrics> |
| Консоль | `curl -s http://127.0.0.1:8081/metrics \| head -n 40` |
| Docker Compose | `curl -s http://127.0.0.1:8081/metrics` (порт `8081` опубликован в `docker-compose.yml`) |
| Снаружи | `http://<host>:8081/metrics`, только если `metricsHost` переключён на `0.0.0.0` |
| Другой порт | задать `metricsPort` в `config.json` и **перезапустить процесс** (ключ «только старт») |

Страница отдаётся как обычный текст `text/plain; version=0.0.4` — открывается в браузере без JavaScript и без сборщика; `webApiToken` для неё не нужен (аутентификация закрывает только `/api/*`). Рядом живут человекочитаемые представления: панель `/` с графиками и `/healthz/ready` в формате `AspNetCore.HealthChecks.UI.Client`.

Что внутри:

| Группа | Метрики |
|---|---|
| Соединения | `tgwsproxy_connections_total`, `tgwsproxy_connections_active`, `tgwsproxy_connections_ws_total`, `tgwsproxy_connections_h2_total`, `tgwsproxy_connections_tcp_fallback_total`, `tgwsproxy_connections_cf_proxy_total`, `tgwsproxy_connections_fronting_total`, `tgwsproxy_connections_masked_total`, `tgwsproxy_connections_bad_total` |
| Отказы | `tgwsproxy_connections_blocked_total` (ACL), `tgwsproxy_connections_limited_total` (лимит), `tgwsproxy_connections_rate_limited_total` (rate-limit), `tgwsproxy_peer_tracking_overflow_total` |
| Трафик | `tgwsproxy_bytes_up_total`, `tgwsproxy_bytes_down_total`; по источникам — `tgws_peer_bytes_up_total{ip,token}`, `tgws_peer_bytes_down_total{ip,token}`, `tgws_peer_connections_total{ip,token}`, `tgws_peer_connections_active{ip,token}`, `tgws_peer_ws_errors_total{ip,token}`, `tgws_peer_tracked` |
| Пулы и HTTP/2 | `tgwsproxy_pool_hits_total`, `tgwsproxy_pool_misses_total`, `tgwsproxy_cf_pool_hits_total`, `tgwsproxy_cf_pool_misses_total`, `tgwsproxy_h2_requests_total`, `tgwsproxy_h2_errors_total`, `tgwsproxy_h2_tcp_connections_total`, `tgwsproxy_ws_errors_total` |
| Служебные | `tgwsproxy_uptime_seconds`, `healthcheck_status`, `healthcheck_duration_seconds` |

Серии `tgws_peer_*` содержат метку `token` с **именем** токена (не секретом) и ограничены ключом `prometheusTopPeers` (по умолчанию 200 пар, `0` — серий по источникам нет).

Подключение к Prometheus — стандартный scrape-конфиг, без авторизации:

```yaml
scrape_configs:
  - job_name: tg-ws-proxy
    scrape_interval: 15s
    static_configs:
      - targets: ["127.0.0.1:8081"]
```

Если прокси в контейнере, а Prometheus снаружи — либо поднимите `metricsHost` до `0.0.0.0` и ограничьте доступ на уровне сети, либо скрейпьте контейнер из той же docker-сети по имени сервиса. `prometheus-net` также публикует health-метрики через `AddPrometheusHealthCheckPublisher`, поэтому статус проверок виден в тех же `/metrics` без отдельного экспортёра.

## Сборка и тесты

```bash
dotnet build TgWsProxy.slnx
dotnet test TgWsProxy.slnx
```

## Docker

`Dockerfile` рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), а `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64). Финальный слой берётся под целевую платформу (`TARGETPLATFORM`). Контекст сборки — корень репозитория (скрипт healthcheck копируется из `DockerCompose/docker/healthcheck.sh`).

**Один образ только под AArch64:**

```bash
docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .
```

**Один тег для amd64 + arm64** (в registry):

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:3.0.0 --push .
```

Сборка **на самом ARM-сервере** (без эмуляции):

```bash
docker build -t tg-ws-proxy:local .
```

### CI/CD (GitHub Actions)

Workflow [`.github/workflows/docker-build.yml`](.github/workflows/docker-build.yml) при пуше в `main` и на тег `v*` / `[0-9]*`:

1. гоняет `dotnet test` (сборка + тесты);
2. собирает мультиархитектурный образ `linux/amd64,linux/arm64` через BuildKit;
3. пушит в Docker Hub `pboldev/tg-ws-proxy`: пуш в `main` — теги `latest` и `main`; тег вида `v3.0.0` или `3.0.0` — семантический тег образа `3.0.0`.

### Docker Compose (`DockerCompose/docker-compose.yml`)

Запуск из готового образа на [Docker Hub](https://hub.docker.com/r/pboldev/tg-ws-proxy).

```bash
cd DockerCompose
mkdir -p config
cp config.docker.json config/config.json     # образец: host/metricsHost = 0.0.0.0
sudo chown -R 1654:1654 config               # UID пользователя app в образе
docker compose up -d
```

`command:` больше не передаёт флагов конфигурации (F-6.9) — все настройки в `config.json`, в том числе `host`/`metricsHost: "0.0.0.0"`, иначе проброшенные порты не дойдут до приложения. Переменные окружения `TG_PORT`/`TG_METRICS_PORT` управляют только **публикацией** портов (`${TG_PORT:-1443}:1443`); порты внутри контейнера задают ключи `port` и `metricsPort`.

### Конфигурация и состояние в Docker

В образе объявлен том **`/app/config`**, переменная `TGWS_CONFIG` указывает на `/app/config/config.json`:

* положите свой `config.json` в примонтированный каталог — прокси подхватит его при старте;
* изменения из панели пишутся **в этот же файл** и переживают `docker compose down` / `up`;
* если файла нет — прокси стартует на значениях по умолчанию.

`docker-compose.yml` монтирует `./config:/app/config` (bind-mount); в комментариях есть вариант с именованным томом.

Образ работает **не от root**, слушает `1443` (прокси) и `8081` (Web API), а `HEALTHCHECK` опрашивает `/healthz/ready` (TCP-проверки порта прокси больше нет). Интервал — 30 с, стартовое окно — 20 с, 3 повтора: этого достаточно, чтобы пережить короткое окно `Degraded` при перезапуске листенера.

**Логи и остановка:**

```bash
docker compose logs -f tg-ws-proxy
docker compose down
```

**Дополнительные настройки** (`verbose`, `logPath`, `port`, `host`) задавайте в `config/config.json` — это ключи «только старт». Остальное (`tokens`, CF Proxy, `fakeTlsDomain`, `webApiToken`, `allowIp`/`denyIp`, `maxConnections`, `rateLimitPerIp`) можно менять и в панели. Флаги CLI удалены.

> **Важно про панель в контейнере.** Образец `config.docker.json` слушает `0.0.0.0`. Поэтому **обязательно** задайте `webApiToken`: без него в лог уйдёт предупреждение, а метрики и правка конфигурации будут доступны любому, кто дотянется до порта. Порт `8081` публикуйте только туда, где это допустимо (или не публикуйте вовсе и ходите через SSH-туннель).

#### Почему TLS / warmup / `EOF` / `RST` бывают именно в Docker

На **хосте** `dotnet`/бинарник ходит в интернет напрямую. В **контейнере** трафик идёт через **bridge/NAT** Docker (на Windows/macOS ещё и через **виртуализацию**). Из‑за этого чаще всплывают:

| Причина | Что попробовать |
|--------|------------------|
| **MTU** (VPN, PPPoE, некоторые провайдеры) | Пакеты к `443` «зависают» или обрываются. Варианты: уменьшить MTU у Docker (`daemon.json` → `"mtu": 1400`) или подключить оверлей **`docker-compose.mtu.yml`** (сеть с MTU 1400). |
| **Маршрут VPN только для хоста** | Исходящие из bridge не попадают в туннель. **Linux:** оверлей **`docker-compose.host.yml`** (`network_mode: host`) — исходящий трафик как у хоста. |
| **DNS в контейнере** | Оверлей **`docker-compose.dns.yml`** (например Cloudflare/Google DNS). |
| **Проверка той же сети, что у прокси** | С тем же сетевым namespace, что у запущенного контейнера: `docker run --rm --network container:tg-ws-proxy nicolaka/netshoot openssl s_client -connect 149.154.167.220:443 -servername kws2.web.telegram.org -brief` — если здесь обрыв/таймаут, проблема в **сети Docker/хоста**, не в коде прокси. |

Примеры запуска с оверлеями:

```bash
docker compose -f docker-compose.yml -f docker-compose.mtu.yml up -d
docker compose -f docker-compose.yml -f docker-compose.dns.yml up -d
# только Linux:
docker compose -f docker-compose.yml -f docker-compose.host.yml up -d
```

`network_mode: host` на **Docker Desktop** для Windows/macOS обычно **не** даёт того же эффекта, что на Linux; там смотрите MTU, WSL2/VPN и настройки Docker.

## Настройка Telegram

Токены и готовые ссылки удобнее всего брать в панели (раздел «Токены доступа»): у каждого токена своя ссылка — кнопка «Ссылка» копирует готовую конфигурацию для Telegram в буфер обмена, секрет показывается при создании, ротации и подставляется в поле при редактировании. В лог при старте пишется по одной ссылке на включённый токен (секрет маскируется).

- `tg://proxy?server=...&port=...&secret=dd<secret>` — обычный режим;
- `tg://proxy?server=...&port=...&secret=ee<secret><hex(домен)>` — если задан `fakeTlsDomain` (маскировка под HTTPS к указанному домену).

### Telegram Desktop

1. Откройте: **Настройки → Продвинутые настройки → Тип подключения → Прокси**.
2. Добавьте прокси типа **MTProto**:
   - Сервер: `127.0.0.1` (или адрес сервера)
   - Порт: `1443` (или ваш `port`)
   - Секрет: `dd…` или `ee…` нужного токена.

### Telegram Mobile

Нажмите на ссылку `tg://proxy?...` из панели или лога — Telegram автоматически предложит добавить прокси.

## Отличия от upstream

По поведению транспортов и протокола порт следует upstream v1.11.2. Осознанные отличия:

- **Только web-only режим.** Консольного запуска нет: процесс — ASP.NET Core хост, прокси работает как `IHostedService`, рядом всегда поднимается Web API с панелью. Флаги конфигурации удалены, остались `--config`, `--help`, `--version`.
- **Конфигурация только в `config.json`**: комментарии, атомарная запись, валидация перед сохранением, классификация ключей «горячо / слушатель / только старт», правка через API и панель.
- **Несколько токенов доступа** вместо одного секрета: `tokens[]`, CRUD и ротация из панели, миграция старого `secret`, рукопожатие перебирает токены по времени последнего использования.
- **Перезапуск листенера внутри приложения** (`IListenerController`): смена адреса/порта не требует рестарта контейнера, при неудаче конфигурация откатывается.
- **Статистика по парам «IP + токен»**: `/api/per-ip?group=`, `/api/metrics/by-token`, серии `tgws_peer_*` с метками `ip` и `token`.
- **`/metrics` на `prometheus-net`** (единственный registry, имена счётчиков с суффиксом `_total`) вместо рукописного форматтера; health-метрики `healthcheck_status`/`healthcheck_duration_seconds` через `prometheus-net.AspNetCore.HealthChecks`.
- **Health checks через `AspNetCore.HealthChecks.UI.Client`**: `/healthz` (liveness) и `/healthz/ready` (readiness, `proxy-listener` / `config` / `stats-registry`).
- **Swagger/OpenAPI** (`/swagger`) с XML-комментариями и bearer-схемой.
- **`--auth` и `--cfproxy-priority` отсутствуют** (в upstream их нет); `blacklist` WS-доменов заменён пулом с экспоненциальным backoff.
- Добавлен watchdog слушающего сокета, лимиты (`maxConnections`, `rateLimitPerIp`), ACL по IP, per-IP учёт и bearer-аутентификация для `/api/*`.
- **Docker-образ production-ориентированный**: том `/app/config`, `TGWS_CONFIG`, работа не от root, `EXPOSE 1443 8081`, `HEALTHCHECK` на `/healthz/ready`.
