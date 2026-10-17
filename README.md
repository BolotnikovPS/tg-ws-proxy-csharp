# TG WS Proxy (C#)

Локальный MTProto-прокси для Telegram на C#/.NET 8. Маршрутизирует трафик через WebSocket/TLS к дата-центрам Telegram, умеет маскироваться под обычный HTTPS (Fake TLS) и автоматически уходит на резервные транспорты (Cloudflare Worker → Cloudflare → TCP), когда WS недоступен.

Проект — порт Python-реализации [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), доведённый до upstream v1.11.2 и переработанный в **web-only сервис**: единственный способ запуска — ASP.NET Core хост, единственный интерфейс настройки — веб-панель.

---

## Быстрый старт

**Локально, из исходников** (нужен .NET 8 SDK):

```bash
dotnet run --project TgWsProxy -- --config config.example.json
```

Без `--config` берётся `./config.json` из текущего каталога; если файла нет — стартуют значения по умолчанию (`127.0.0.1:1443`, панель на `127.0.0.1:8081`).

**В Docker** (готовый образ на [Docker Hub](https://hub.docker.com/r/pboldev/tg-ws-proxy)):

```bash
cd DockerCompose
mkdir -p config && cp config.docker.json config/config.json
sudo chown -R 1654:1654 config
docker compose up -d
```

Дальше:

| Что | Куда |
|---|---|
| Панель (мониторинг + настройка) | <http://127.0.0.1:8081/> |
| Метрики Prometheus | <http://127.0.0.1:8081/metrics> |
| Проверка живости | <http://127.0.0.1:8081/healthz> |
| Сам прокси для Telegram | `127.0.0.1:1443` + секрет из панели |

Токен для Telegram берётся в панели: раздел **«Токены доступа»** → кнопка **«Ссылка»** копирует готовую конфигурацию `t.me/proxy`. Подробнее — [Настройка Telegram](#настройка-telegram).

> Если открываете панель не с loopback (`metricsHost: "0.0.0.0"`), **обязательно задайте `webApiToken`** — иначе конфигурацию сможет менять любой, кто дотянется до порта.

---

## Содержание

- [Возможности](#возможности)
- [Как это работает](#как-это-работает)
- [Веб-панель](#веб-панель)
- [Страница метрик `/metrics`](#страница-метрик-metrics)
- [Запуск и аргументы командной строки](#запуск-и-аргументы-командной-строки)
- [Конфигурация](#конфигурация)
- [Web API](#web-api)
- [Настройка Telegram](#настройка-telegram)
- [Docker](#docker)
- [Диагностика](#диагностика)
- [Архитектура](#архитектура)
- [Сборка и тесты](#сборка-и-тесты)
- [Отличия от upstream](#отличия-от-upstream)

---

## Возможности

### Транспорты

- **Пул WS-соединений** на пару (DC, media): прогрев, TTL 120 с, периодическая чистка, refill с экспоненциальным backoff (1 с → 3600 с).
- **TCP fallback** на IP дата-центра с backoff 30 с → 3600 с.
- **Cloudflare fallback**: балансировщик из 20 встроенных «обфусцированных» доменов с обновлением раз в час, пул Cloudflare Worker-доменов, прямые CF-proxy подключения.
- **HTTP/2 мультиплексирование** media-трафика через Cloudflare (`/api`): несколько каналов (lane) на одном H2-соединении, бюджет байт/запросов, восстановление потерянных пакетов.
- **Fake TLS (ee-secret)**: приём ClientHello, ответ ServerHello + ChangeCipherSpec, MTProto внутри TLS application data. Невалидный секрет — проксирование на домен-маскировку, обычный HTTP — редирект 301.
- **Watchdog** слушающего сокета: раз в 5 с проверяет живость и поднимает сервер заново.

### Доступ и безопасность

- **Несколько токенов доступа** (MTProto secret, 32 hex): своё устройство или группа — свой секрет и своя `tg://`-ссылка. CRUD и ротация из панели, без правки файла руками.
- **Bearer-аутентификация** `/api/*` (`webApiToken`), сравнение постоянного времени.
- **ACL по IP/CIDR**, лимит одновременных сессий, per-IP rate limit.
- **PROXY protocol v1** для работы за nginx/haproxy.

### Управление и наблюдаемость

- **Перезапуск слушателя внутри процесса**: смена ключей категории «слушатель» не требует рестарта контейнера и не роняет Web API. При неудачной привязке конфигурация откатывается. Список ключей — в разделе [Конфигурация](#конфигурация).
- **Веб-панель** без сборщика и CDN: обзор, график скорости, типы соединений, пулы, токены, трафик по источникам, форма конфигурации с классификацией ключей.
- **Статистика по источникам**: пары «IP устройства + токен» — соединения, байты в обе стороны, ошибки WS, попадания в пул, отказы.
- **`/metrics` на `prometheus-net`** (единственный registry) + health checks (`AspNetCore.HealthChecks.UI.Client`, `prometheus-net.AspNetCore.HealthChecks`) + **Swagger/OpenAPI**.

---

## Как это работает

```text
Telegram Client -> MTProto proxy (этот сервис) -> WSS / CF-H2 / TCP -> Telegram DC
```

Порядок выбора транспорта для каждого нового подключения:

1. **Пул прямых WS** — если пул не отключён и DC не в «cooldown» после ошибки.
2. **Cloudflare Worker** (`cfProxyWorkerDomains`) — если заданы.
3. **Cloudflare** — для media-трафика HTTP/2 мультиплексирование на `/api` (если H2 доступен), иначе WS через CF-proxy домен.
4. **TCP fallback** на IP дата-центра.

MTProto-трафик каждого соединения перешифровывается: расшифровка ключом выбранного токена, шифрование ключом Telegram — и наоборот в обратную сторону.

---

## Веб-панель

Одна страница по адресу `http://<metricsHost>:<metricsPort>/` (по умолчанию `http://127.0.0.1:8081/`). Статика лежит в [`TgWsProxy/wwwroot/`](TgWsProxy/wwwroot/): [`index.html`](TgWsProxy/wwwroot/index.html), [`app.js`](TgWsProxy/wwwroot/app.js), [`site.css`](TgWsProxy/wwwroot/site.css) — без CDN, npm и сборщика, отдаётся тем же ASP.NET Core хостом, что и API.

### Что на странице

| Блок | Содержимое |
|---|---|
| **Обзор** | Плитки: время работы, активные соединения (из суммарных), отправлено и получено, отклонено (ACL / лимит), ошибки (WS / bad), распределение по маршрутам (WS / H2 / TCP), состояние слушателя, число отслеженных IP. |
| **Скорость трафика** | График скорости из кольцевого буфера (`timeseriesWindowSeconds` / `timeseriesStepSeconds`), две линии: к Telegram и от Telegram. |
| **Типы соединений** | Доли WS / H2 / TCP fallback / CF proxy / fronting / masked. |
| **Пул соединений** | Живые и прогреваемые WS-соединения, попадания и промахи пула, состояние CF-пула и H2. |
| **Токены доступа** | Таблица токенов: имя, маска секрета, состояние, отправлено/получено, число сессий, последнее использование. Действия: «Ссылка», «Изменить», «Ротация», «Включить/Отключить», «Удалить». Ниже — форма создания и блок одноразового показа секрета. |
| **Трафик по источникам** | Таблица с группировкой «IP + токен» / «только IP» / «только токен» и фильтром по префиксу адреса. |
| **Конфигурация** | Форма по группам с бейджами категорий («горячо» / «слушатель» / «только старт») и кнопками «Сохранить», «Перезагрузить», «Сбросить». |

Порядок групп в форме: **«Сеть и Web API»** и **«Журналирование»** — первые, рядом друг с другом и только для чтения (это справочные блоки: метка — значение, без полей ввода, потому что все их ключи относятся к «только старт»); далее на всю ширину — «Ссылки Telegram», «Безопасность», «DC и пул WebSocket», «Ограничения», «Cloudflare», «Мониторинг».

### Поведение и детали

- **Опрос раз в 2 с**; в скрытой вкладке опрос приостанавливается (событие `visibilitychange`).
- **Шапка**: индикатор состояния (`ok` / `restarting` / `unavailable`), версия сборки, переключатель темы (авто → светлая → тёмная, выбор в `localStorage`).
- **Аутентификация**: если задан `webApiToken`, страница один раз спрашивает его и держит в `sessionStorage` вкладки. Токен не попадает в исходники страницы, в URL и в состояние после закрытия вкладки.
- **Секреты**: в таблице только маска `abcd…ef01`. Полный секрет показывается однократно при создании и ротации, а кнопка «Ссылка» забирает его через `GET /api/tokens/{id}/link` и сразу кладёт в буфер обмена, не сохраняя в состоянии панели. В режиме «Изменить» поле секрета подставляется текущим значением: пусто — не менять, новое — смена секрета.
- **Подтверждение перезапуска**: если в форме изменены ключи категории «слушатель», перед сохранением панель спрашивает подтверждение и показывает число сессий, которые будут закрыты (`droppedSessions`).
- **Ошибки** приходят в формате ProblemDetails и разбираются по-разному: `409 startupOnly` → «ключ меняется только при перезапуске процесса», `409 rolledBack` → «новый адрес не занят, изменения откачены».
- **Доступность**: skip-link на контент, `aria-live` для статусов, подписи у всех полей, уважение `prefers-reduced-motion`, одна колонка на узких экранах (≤ 640 px).

---

## Страница метрик `/metrics`

Метрики отдаёт тот же веб-сервер, что и панель — отдельного процесса и порта нет. Адрес складывается из `metricsHost` и `metricsPort` (по умолчанию `127.0.0.1:8081`).

| Как открыть | Команда / адрес |
|---|---|
| Браузер, локально | <http://127.0.0.1:8081/metrics> |
| Консоль | `curl -s http://127.0.0.1:8081/metrics \| head -n 40` |
| Docker Compose | `curl -s http://127.0.0.1:8081/metrics` (порт `8081` опубликован) |
| Снаружи | `http://<host>:8081/metrics` — только если `metricsHost` переключён на `0.0.0.0` |
| Другой порт | `metricsPort` в `config.json` + **перезапуск процесса** (ключ «только старт») |

Формат — обычный текст `text/plain; version=0.0.4`: открывается в браузере без JavaScript, `webApiToken` для него не нужен (аутентификация закрывает только `/api/*`).

### Что внутри

| Группа | Метрики |
|---|---|
| Соединения | `tgwsproxy_connections_total`, `tgwsproxy_connections_active`, `tgwsproxy_connections_ws_total`, `tgwsproxy_connections_h2_total`, `tgwsproxy_connections_tcp_fallback_total`, `tgwsproxy_connections_cf_proxy_total`, `tgwsproxy_connections_fronting_total`, `tgwsproxy_connections_masked_total`, `tgwsproxy_connections_bad_total` |
| Отказы | `tgwsproxy_connections_blocked_total` (ACL), `tgwsproxy_connections_limited_total` (лимит сессий), `tgwsproxy_connections_rate_limited_total` (rate limit), `tgwsproxy_peer_tracking_overflow_total` |
| Трафик | `tgwsproxy_bytes_up_total`, `tgwsproxy_bytes_down_total`; по источникам — `tgws_peer_bytes_up_total{ip,token}`, `tgws_peer_bytes_down_total{ip,token}`, `tgws_peer_connections_total{ip,token}`, `tgws_peer_connections_active{ip,token}`, `tgws_peer_ws_errors_total{ip,token}`, `tgws_peer_tracked` |
| Пулы и HTTP/2 | `tgwsproxy_pool_hits_total`, `tgwsproxy_pool_misses_total`, `tgwsproxy_cf_pool_hits_total`, `tgwsproxy_cf_pool_misses_total`, `tgwsproxy_h2_requests_total`, `tgwsproxy_h2_errors_total`, `tgwsproxy_h2_tcp_connections_total`, `tgwsproxy_ws_errors_total` |
| Служебные | `tgwsproxy_uptime_seconds`, `healthcheck_status`, `healthcheck_duration_seconds` |

Серии `tgws_peer_*` содержат метку `token` с **именем** токена (не секретом) и ограничены ключом `prometheusTopPeers` (по умолчанию 200 пар, `0` — серий по источникам нет).

### Подключение к Prometheus

```yaml
scrape_configs:
  - job_name: tg-ws-proxy
    scrape_interval: 15s
    static_configs:
      - targets: ["127.0.0.1:8081"]
```

Если прокси в контейнере, а Prometheus снаружи: либо поднимите `metricsHost` до `0.0.0.0` и ограничьте доступ на уровне сети, либо скрейпьте контейнер из той же docker-сети по имени сервиса. Health-метрики публикует `AddPrometheusHealthCheckPublisher`, поэтому статус проверок виден в тех же `/metrics` без отдельного экспортёра.

---

## Запуск и аргументы командной строки

Единственный режим — web-only: HTTP-сервер поднимается всегда, панель и API живут в том же процессе, что и прокси. Консольного запуска без Web-части больше нет.

```bash
dotnet TgWsProxy.dll --config /etc/tg-ws-proxy/config.json
```

| Аргумент | Описание |
|---|---|
| `--config <PATH>` | Путь к файлу конфигурации (алиас `--config=PATH`). Также читается из `TGWS_CONFIG`. По умолчанию `./config.json`. |
| `-h`, `--help` | Справка по bootstrap-аргументам и ключам `config.json`; выход с кодом `0`. |
| `--version` | Версия (`tg-ws-proxy <версия>`); выход с кодом `0`. |

Любой другой флаг (`--port`, `--secret`, `--metrics-port`, `--verbose`…) завершает работу с кодом `1` и подсказкой, какой ключ `config.json` соответствует старому флагу (например `--port` → `"port"`).

---

## Конфигурация

### Файл `config.json`

`config.json` — единственный источник настроек, аргументы CLI ключи не перекрывают. Путь ищется так: `--config <PATH>` → переменная окружения `TGWS_CONFIG` → `./config.json`.

- Отсутствие файла — не ошибка, работают значения по умолчанию.
- Синтаксическая ошибка, неизвестный ключ или значение вне диапазона — завершение с кодом `1` и указанием строки.
- Допустимы комментарии (`//`, `/* */`) и висячие запятые; ключи в camelCase, регистр не важен.
- Полный список с комментариями — [`config.example.json`](config.example.json), для контейнера — [`DockerCompose/config.docker.json`](DockerCompose/config.docker.json).

> Приложение **перезаписывает файл** при сохранении из панели/API, при миграции старого `secret` в `tokens[]` и при автогенерации токена `default` для пустого списка. Запись атомарная, но комментарии и форматирование при этом теряются (пишется компактный JSON). Держите копию образца отдельно.

### Ключи, применяемые на лету

Меняются в панели или через `PUT /api/config` и вступают в силу сразу — ответ содержит `hotReloaded`.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `publicHost` | пусто | Внешний адрес для ссылок `t.me/proxy`: `host`, `host:port`, `[IPv6]:port`. Пусто — берутся `host` и `port`. Нужен, когда `host` служебный (`0.0.0.0` в контейнере) или прокси за NAT. |
| `webApiToken` | пусто | Bearer-токен для `/api/*`. Пусто — аутентификации нет (допустимо только на loopback). |
| `tokens` | `[]` → создаётся `default` | Токены доступа прокси, 1…64: `{id, name, secret (32 hex), enabled, createdAt, lastUsedAt}`. |
| `maxConnections` | `0` | Максимум одновременных сессий; `0` — без лимита. |
| `rateLimitPerIp` | `0` | Лимит новых подключений с одного IP за окно; `0` — выключено. |
| `rateLimitWindowSeconds` | `60` | Длина окна rate limit. |
| `allowIp` / `denyIp` | `[]` | Списки IP/CIDR; `deny` приоритетнее `allow`. |
| `enablePerIpStats` | `true` | Учёт статистики по парам «IP + токен». |
| `perIpStatsTtlSeconds` | `1800` | Время жизни простаивающей записи; `0` — не удалять. |
| `perIpStatsMaxPeers` | `100000` | Максимум отслеживаемых источников; сверх лимита растёт `peerTrackingOverflow`. |
| `prometheusTopPeers` | `200` | Сколько источников отдавать в `/metrics` отдельными сериями (`0` — ни одной). |
| `timeseriesWindowSeconds` | `900` | Окно графика по умолчанию для `/api/metrics/timeseries`. |
| `timeseriesStepSeconds` | `5` | Шаг записи в кольцевой буфер и выборки истории. |

### Ключи, требующие перезапуска слушателя

Применяются перезапуском листенера внутри процесса: останавливается приём, закрываются активные сессии, открывается новый сокет, затем применяется остальная конфигурация. Ответ содержит `listenerRestarted` и `droppedSessions`. Если новый сокет не удалось привязать — конфигурация и файл откатываются, ответ `409` с `rolledBack: true`, старый сокет продолжает работать.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `dcIp` | `2:…`, `4:…` | Сопоставление DC → IP. Пустой список отключает WS-пул и прямые WS. |
| `wsPoolSize` | `4` | Размер пула WS на пару (DC, media); `0` отключает прямые WS. |
| `wsConnectTimeoutSeconds` | `10` | Таймаут WS-рукопожатия; этапы TCP/TLS — не больше 10 с. |
| `wsMaxFrameBytes` | `16777216` | Максимальный payload WS-фрейма (защита от DoS). |
| `socketBufferKb` | `256` | `SO_RCVBUF`/`SO_SNDBUF` клиентского и исходящего сокета. |
| `cfProxyEnabled` / `cfProxyH2Media` | `true` / `true` | Cloudflare fallback и H2-мультиплексирование media. |
| `cfProxyDomains` / `cfProxyWorkerDomains` | `[]` | Свои CF-proxy и CF Worker домены (пусто — встроенный список). |
| `disableSecure` | `false` | Использовать порт 80 вместо 443 для Cloudflare. |
| `fakeTlsDomain` | пусто | Включает Fake TLS (`ee…`) маскировку под указанный домен. |
| `proxyProtocol` | `false` | Принимать PROXY protocol v1. |
| `forceTestDc` | `false` | Весь трафик в тестовые DC. |
| `allowInvalidCertificates` | `false` | Разрешать TLS с невалидным сертификатом (только для диагностики). |

### Ключи, читаемые только при старте

Меняются только правкой файла и перезапуском процесса. `PUT /api/config` отклоняет их с `409` и полем `startupOnly`, файл на диске при этом не меняется. В панели это справочные блоки без полей ввода.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `port` | `1443` | Порт локального MTProto-сервера. |
| `host` | `127.0.0.1` | Адрес прослушивания прокси. |
| `metricsHost` | `127.0.0.1` | Адрес привязки Web API и панели. |
| `metricsPort` | `8081` | Порт Web API и панели, диапазон `1…65535`. Выключить Web-часть нельзя. |
| `enableSwagger` | `true` | Swagger UI (`/swagger`) и `/swagger/v1/swagger.json`. |
| `verbose` | `false` | Подробное логирование (`Debug`). Уровень фиксируется при сборке логгера. |
| `logPath` | пусто | Файл лога с ротацией; пусто — только консоль. |
| `logMaxMegabytes` / `logRetainedFileCount` | `5` / `1` | Лимит размера файла и число архивов. |

### Как применяются изменения

1. Значения с маской (`abcd…ef01`) означают «не менять».
2. Слияние с текущей конфигурацией → валидация → классификация ключей.
3. Ключи «только старт» отклоняются до записи файла (`409 startupOnly`).
4. Запись во временный файл и атомарное переименование.
5. Если затронуты ключи «слушатель» — перезапуск листенера; при неудаче файл откатывается.
6. Применение «горячих» ключей и предупреждения в ответе.

---

## Web API

Все `/api/*` требуют `Authorization: Bearer <webApiToken>`, если токен задан. `/healthz`, `/healthz/ready`, `/metrics`, `/swagger` и сама страница открыты.

### Служебные и диагностика

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/` | Веб-панель. |
| `GET` | `/healthz` | Liveness: `200` и `{"status":"ok","listening":…}`, `503` при `restarting` / `unavailable`. |
| `GET` | `/healthz/ready` | Readiness: проверки `proxy-listener`, `config`, `stats-registry`; формат `AspNetCore.HealthChecks.UI.Client`, `200` (Healthy) или `503` (Degraded/Unhealthy). |
| `GET` | `/metrics` | Prometheus-метрики (`prometheus-net`). |
| `GET` | `/swagger`, `/swagger/v1/swagger.json` | Swagger UI и OpenAPI-схема (`enableSwagger`). |
| `GET` | `/api/info` | Версия, uptime, состояние листенера, счётчики токенов. |

### Конфигурация

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/api/config` | Текущая конфигурация; секреты замаскированы. |
| `PUT` | `/api/config` | Частичное обновление — только изменяемые ключи. |
| `POST` | `/api/config/reset` | Значения по умолчанию (без записи на диск). |

### Токены

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/api/tokens` | Список токенов; секреты замаскированы. |
| `POST` | `/api/tokens` | Создать токен; единственный раз возвращает полный секрет (`Cache-Control: no-store`). |
| `PUT` | `/api/tokens/{id}` | Переименовать / включить / выключить / сменить секрет. Выключение закрывает сессии этого токена. |
| `DELETE` | `/api/tokens/{id}` | Удалить токен (нельзя последний включённый) и закрыть его сессии. |
| `POST` | `/api/tokens/{id}/rotate` | Новый секрет; возвращается один раз, старый перестаёт работать. |
| `GET` | `/api/tokens/{id}/link` | Полный секрет и готовая ссылка `t.me/proxy` (`Cache-Control: no-store`). |

### Метрики и трафик

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/api/metrics` | Снимок счётчиков (JSON). |
| `GET` | `/api/metrics/timeseries?window=&step=` | Временной ряд из кольцевого буфера. |
| `GET` | `/api/metrics/by-token` | Агрегаты трафика по токенам. |
| `GET` | `/api/per-ip?group=ip\|token\|pair&token=&q=&offset=&limit=` | Трафик по источникам: группировка, фильтр по токену, `q` — префикс адреса, `limit` до 500. |

### Безопасность и секреты

По умолчанию сервер слушает только `127.0.0.1` — этого достаточно для локального доступа и SSH-туннеля. Если открываете панель наружу, задайте `webApiToken`: иначе при старте в лог уйдёт предупреждение, а читать метрики и менять конфигурацию сможет любой, кто дотянется до порта.

Секреты (`webApiToken`, `tokens[].secret`) никогда не отдаются открытым текстом — только маска `abcd…ef01`. Исключения: создание токена, ротация и `GET /api/tokens/{id}/link` — все с `Cache-Control: no-store`. Маска, отправленная обратно, трактуется как «не менять».

### Токены и внешний адрес

При старте без токенов создаётся `default`; старый ключ `secret` в файле автоматически мигрирует в `tokens[]`. В лог пишется по одной `tg://`-ссылке на включённый токен (секрет маскируется).

Адрес в ссылке берётся из `publicHost`, а если он пуст — из `host` и `port`. Адрес прослушивания и внешний адрес — разные вещи: в контейнере `host` обязан быть `0.0.0.0`, и вывести из него домен или публичный IP невозможно, поэтому без `publicHost` кнопка «Ссылка» ответит «внешний адрес прокси не задан». Служебные адреса (`0.0.0.0`, `::`, `*`, `+`) в `publicHost` запрещены валидатором.

### Учёт по источникам

Ключ записи — пара (IP устройства, токен): IPv4-mapped IPv6 нормализуется, порт отбрасывается. Считаются активные и суммарные соединения, байты в обе стороны, ошибки WS, попадания в пул и отказы (ACL / лимит / rate limit). Записи живут `perIpStatsTtlSeconds`, количество ограничено `perIpStatsMaxPeers`, при переполнении растёт `peerTrackingOverflow`. Счётчики обновляются без блокировок (`Interlocked`).

---

## Настройка Telegram

Самый простой путь — панель, раздел **«Токены доступа»**: у каждого токена своя ссылка, кнопка «Ссылка» копирует готовую конфигурацию для Telegram в буфер обмена. Секрет показывается при создании, при ротации и подставляется в поле при редактировании. Внешний адрес задаётся ключом `publicHost` (раздел панели «Ссылки Telegram»): в Docker без него `host` равен `0.0.0.0` и ссылка не строится — укажите домен или публичный IP вместе с опубликованным портом, например `proxy.example.com:1443`.

Формат ссылки зависит от режима:

- `tg://proxy?server=...&port=...&secret=dd<secret>` — обычный режим;
- `tg://proxy?server=...&port=...&secret=ee<secret><hex(домен)>` — если задан `fakeTlsDomain` (маскировка под HTTPS к указанному домену).

### Telegram Desktop

1. **Настройки → Продвинутые настройки → Тип подключения → Прокси**.
2. Добавьте прокси типа **MTProto**:
   - Сервер: `127.0.0.1` (или адрес сервера);
   - Порт: `1443` (или ваш `port`);
   - Секрет: `dd…` или `ee…` нужного токена.

### Telegram Mobile

Нажмите ссылку `tg://proxy?...` из панели или лога — Telegram сам предложит добавить прокси.

---

## Docker

`Dockerfile` рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64), финальный слой берётся под целевую платформу. Контекст сборки — корень репозитория (скрипт healthcheck копируется из `DockerCompose/docker/healthcheck.sh`).

```bash
# только AArch64
docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .

# один тег для amd64 + arm64 в registry
docker buildx build --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:3.0.1 --push .

# на самом ARM-сервере, без эмуляции
docker build -t tg-ws-proxy:local .
```

### CI/CD (GitHub Actions)

Workflow [`.github/workflows/docker-build.yml`](.github/workflows/docker-build.yml) срабатывает на пуш в `main`, на тег `v*` / `[0-9]*` и вручную:

1. гоняет `dotnet test` (сборка + тесты);
2. собирает мультиархитектурный образ `linux/amd64,linux/arm64` через BuildKit;
3. пушит в Docker Hub `pboldev/tg-ws-proxy`: пуш в `main` — тег `latest`; тег вида `v3.0.1` или `3.0.1` — семантические теги `3.0.1` и `3.0`. Теги веток не публикуются.

### Docker Compose

```bash
cd DockerCompose
mkdir -p config
cp config.docker.json config/config.json     # образец: host/metricsHost = 0.0.0.0
sudo chown -R 1654:1654 config               # UID пользователя app в образе
docker compose up -d

docker compose logs -f tg-ws-proxy
docker compose down
```

`command:` не передаёт флагов конфигурации — все настройки в `config.json`, включая `host`/`metricsHost: "0.0.0.0"`, иначе проброшенные порты не дойдут до приложения. Переменные `TG_PORT` / `TG_METRICS_PORT` управляют только **публикацией** портов (`${TG_PORT:-1443}:1443`); порты внутри контейнера задают ключи `port` и `metricsPort`.

Ссылки `t.me/proxy` в контейнере строятся из `publicHost`: `host` здесь служебный, поэтому задайте домен или публичный IP с опубликованным портом (`"publicHost": "proxy.example.com:1443"`) — в файле или прямо в панели, ключ применяется без перезапуска.

### Конфигурация и состояние

В образе объявлен том **`/app/config`**, переменная `TGWS_CONFIG` указывает на `/app/config/config.json`:

- положите свой `config.json` в примонтированный каталог — прокси подхватит его при старте;
- изменения из панели пишутся **в этот же файл** и переживают `docker compose down` / `up`;
- если файла нет — прокси стартует на значениях по умолчанию.

`docker-compose.yml` монтирует `./config:/app/config` (bind-mount); в комментариях есть вариант с именованным томом.

Образ работает **не от root**, слушает `1443` (прокси) и `8081` (Web API), а `HEALTHCHECK` опрашивает `/healthz/ready`. Интервал 30 с, стартовое окно 20 с, 3 повтора — этого достаточно, чтобы пережить короткое окно `Degraded` при перезапуске листенера.

**Дополнительные настройки** (`verbose`, `logPath`, `port`, `host`) задавайте в `config/config.json` — это ключи «только старт». Остальное (`tokens`, CF Proxy, `fakeTlsDomain`, `webApiToken`, `allowIp`/`denyIp`, `maxConnections`, `rateLimitPerIp`) меняется и в панели.

> **Панель в контейнере.** Образец `config.docker.json` слушает `0.0.0.0`, поэтому **обязательно** задайте `webApiToken`: без него в лог уйдёт предупреждение, а метрики и правка конфигурации будут доступны любому, кто дотянется до порта. Порт `8081` публикуйте только туда, где это допустимо, или не публикуйте вовсе и ходите через SSH-туннель.

---

## Диагностика

### Сеть в Docker: TLS, warmup, `EOF`, `RST`

На **хосте** `dotnet`/бинарник ходит в интернет напрямую. В **контейнере** трафик идёт через bridge/NAT (на Windows/macOS ещё и через виртуализацию), поэтому проблемы всплывают чаще:

| Причина | Что попробовать |
|---|---|
| **MTU** (VPN, PPPoE, некоторые провайдеры) | Пакеты к `443` «зависают» или обрываются. Уменьшить MTU у Docker (`daemon.json` → `"mtu": 1400`) или подключить оверлей **`docker-compose.mtu.yml`** (сеть с MTU 1400). |
| **Маршрут VPN только для хоста** | Исходящие из bridge не попадают в туннель. **Linux:** оверлей **`docker-compose.host.yml`** (`network_mode: host`). |
| **DNS в контейнере** | Оверлей **`docker-compose.dns.yml`** (например Cloudflare/Google DNS). |
| **Проверка в той же сети, что у прокси** | `docker run --rm --network container:tg-ws-proxy nicolaka/netshoot openssl s_client -connect 149.154.167.220:443 -servername kws2.web.telegram.org -brief` — если здесь обрыв или таймаут, проблема в сети Docker/хоста, а не в коде прокси. |

Запуск с оверлеями:

```bash
docker compose -f docker-compose.yml -f docker-compose.mtu.yml up -d
docker compose -f docker-compose.yml -f docker-compose.dns.yml up -d
# только Linux:
docker compose -f docker-compose.yml -f docker-compose.host.yml up -d
```

`network_mode: host` на **Docker Desktop** для Windows/macOS обычно **не** даёт того же эффекта, что на Linux; там смотрите MTU, WSL2/VPN и настройки Docker.

### Ссылка `t.me/proxy` не строится

Значит внешний адрес не определён: `host` служебный (`0.0.0.0`, `::`) и `publicHost` пуст. Задайте `publicHost` в панели (раздел «Ссылки Telegram») — применится сразу, перезапуск не нужен.

### Панель отвечает `401`

Задан `webApiToken`, а в браузере он не введён (или введён неверно). Токен хранится в `sessionStorage` вкладки: после её закрытия спрашивается заново. Сброс — очистка данных сайта.

### Панель отвечает `409`

- `startupOnly` — ключ из категории «только старт», меняйте файл и перезапускайте процесс;
- `rolledBack` — новый адрес прослушивания не удалось занять, изменения отменены, старый сокет работает;
- без поля — уже идёт перезапуск слушателя, повторите через секунду (`Retry-After: 1`).

---

## Архитектура

| Проект | Назначение |
|---|---|
| `TgWsProxy.Domain` | Чистая модель без внешних зависимостей: конфигурация (`Config`, `PublicEndpoint`), токены (`ProxyToken`, `SecretMask`), модель handshake (`MtProtoHandshake`), снимки статистики (`StatsSnapshot`, `PeerStatsSnapshot`), исключения. |
| `TgWsProxy.Application` | Сценарии и абстракции: bootstrap-опции и валидация конфига, загрузка/сохранение `config.json` (`ConfigFileLoader`, `ConfigDto`, `ConfigDtoMapper`), применение изменений (`ConfigUpdateService`), реестры (`ProxyTokenRegistry`, `PeerStatsRegistry`), статистика (`ProxyStats`), лимиты (`ConnectionLimiter`, `PeerRateLimiter`), ACL (`PeerAccessControl`), порты (`IClientSessionHandler`, `IMtProtoInspector`, `IActiveSessionRegistry`, `IListenerController`, `IProxyServer`). |
| `TgWsProxy.Infrastructure` | Сетевые реализации: `ProxyServer` (watchdog, `RebindAsync`), `RawWebSocket(Factory)`, `WsPool`, `CfWorkerPool`, `CfDomainBalancer`/`CfDomainRefresher`, `CfH2Pool`/`CfH2Lane`/`CfH2Channel`/`H2Bridge`, `TcpBridgeService`, I/O и протокол (`FakeTls/`, `IO/`, `MtProto/`, `Session/`, `Wire/`), хостинг (`ProxyHostedService` как `IListenerController`), кольцевой буфер метрик (`MetricsHistory`). |
| `TgWsProxy` | Хост ASP.NET Core: DI, логирование с ротацией, Web API (`Web/`), Prometheus-метрики (`ProxyMetrics`), health checks (`ProxyHealthChecks`), статика и панель (`wwwroot/`), graceful shutdown. |
| `TgWsProxy.Test` | xUnit-тесты (231 шт.): bootstrap и конфиг, протокол, Fake TLS, H2-пакеты, WS-кадрирование, регрессия порядка перешифрования, liveness пула, токены и ссылки, Web API, hosted-сервис, DI. |

---

## Сборка и тесты

```bash
dotnet build TgWsProxy.slnx
dotnet test TgWsProxy.slnx
```

---

## Отличия от upstream

По поведению транспортов и протокола порт следует upstream v1.11.2. Осознанные отличия:

**Режим работы и конфигурация**

- **Только web-only режим.** Консольного запуска нет: процесс — ASP.NET Core хост, прокси работает как `IHostedService`, рядом всегда поднимается Web API с панелью. Флаги конфигурации удалены, остались `--config`, `--help`, `--version`.
- **Конфигурация только в `config.json`**: комментарии, атомарная запись, валидация перед сохранением, классификация ключей «горячо / слушатель / только старт», правка через API и панель.
- **Перезапуск листенера внутри приложения** (`IListenerController`): ключи категории «слушатель» (DC, пул WS, Cloudflare, `fakeTlsDomain` и т. п.) применяются без рестарта контейнера, при неудаче конфигурация откатывается. `port` и `host` — «только старт».

**Доступ**

- **Несколько токенов доступа** вместо одного секрета: `tokens[]`, CRUD и ротация из панели, миграция старого `secret`, рукопожатие перебирает токены по времени последнего использования.
- **Внешний адрес для ссылок** (`publicHost`): ссылки `t.me/proxy` строятся из него, а не из адреса привязки, поэтому работают и в контейнере с `host: 0.0.0.0`.
- **`--auth` и `--cfproxy-priority` отсутствуют** (в upstream их нет); `blacklist` WS-доменов заменён пулом с экспоненциальным backoff.
- Добавлены watchdog слушающего сокета, лимиты (`maxConnections`, `rateLimitPerIp`), ACL по IP, per-IP учёт и bearer-аутентификация для `/api/*`.

**Наблюдаемость**

- **Статистика по парам «IP + токен»**: `/api/per-ip?group=`, `/api/metrics/by-token`, серии `tgws_peer_*` с метками `ip` и `token`.
- **`/metrics` на `prometheus-net`** (единственный registry, имена счётчиков с суффиксом `_total`) вместо рукописного форматтера; health-метрики `healthcheck_status` / `healthcheck_duration_seconds` через `prometheus-net.AspNetCore.HealthChecks`.
- **Health checks через `AspNetCore.HealthChecks.UI.Client`**: `/healthz` (liveness) и `/healthz/ready` (readiness).
- **Swagger/OpenAPI** (`/swagger`) с XML-комментариями и bearer-схемой.

**Поставка**

- **Docker-образ production-ориентированный**: том `/app/config`, `TGWS_CONFIG`, работа не от root, `EXPOSE 1443 8081`, `HEALTHCHECK` на `/healthz/ready`.
