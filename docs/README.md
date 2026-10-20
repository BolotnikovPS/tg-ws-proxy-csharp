# TG WS Proxy (C#)

> [!WARNING]
> Часть кода проекта, включая реализацию WEB proxy (browser-relay), тесты и документацию — написана с применением ИИ-ассистентов. Такой код проходит ревью и покрыт автотестами.

Локальный MTProto-прокси для Telegram на C#/.NET 11. Маршрутизирует трафик через WebSocket/TLS к дата-центрам Telegram, умеет маскироваться под обычный HTTPS (Fake TLS) и автоматически уходит на резервные транспорты (Cloudflare Worker → Cloudflare → TCP), когда WS недоступен.

> [!NOTE]  
> Проект — порт Python-реализации [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), доведённый до upstream v1.11.2 и переработанный в **web-only сервис**: единственный способ запуска — ASP.NET Core хост, единственный интерфейс настройки — веб-панель.

---

## Быстрый старт

**Локально, из исходников** (нужен .NET 11 SDK):

```bash
dotnet run --project src/TgWsProxy -- --config config.example.json
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

Токен для Telegram берётся в панели: раздел **«Токены доступа»** → кнопка **«Ссылка MTProto»** копирует готовую конфигурацию `t.me/proxy`. Подробнее — [Настройка Telegram](telegram.md).

> Если открываете панель не с loopback (`metricsHost: "0.0.0.0"`), **обязательно задайте `webApiToken`** — иначе конфигурацию сможет менять любой, кто дотянется до порта.

---

## Содержание

- [Возможности](#возможности)
- [Как это работает](#как-это-работает)
- [Веб-панель](#веб-панель)
- [Страница метрик `/metrics`](#страница-метрик-metrics)
- [Отличия от upstream](#отличия-от-upstream)

Отдельные документы:

- [Запуск и аргументы командной строки](run.md)
- [Конфигурация](configuration.md)
- [Web API](web-api.md)
- [Настройка Telegram](telegram.md)
- [Docker](docker.md)
- [Диагностика](diagnostics.md)
- [Архитектура](architecture.md)
- [Сборка и тесты](build.md)
- [Бенчмарки](benchmarks.md)

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

- **Перезапуск слушателя внутри процесса**: смена ключей категории «слушатель» не требует рестарта контейнера и не роняет Web API. При неудачной привязке конфигурация откатывается. Список ключей — в разделе [Конфигурация](configuration.md).
- **Веб-панель** без сборщика и CDN: обзор, график скорости, типы соединений, пулы, токены, трафик по источникам, форма конфигурации с классификацией ключей.
- **Статистика по источникам**: пары «IP устройства + токен» — соединения, байты в обе стороны, ошибки WS, попадания в пул, отказы.
- **`/metrics` на `prometheus-net`** (единственный registry) + health checks (`AspNetCore.HealthChecks.UI.Client`, `prometheus-net.AspNetCore.HealthChecks`) + **Swagger/OpenAPI**.

### WEB proxy (alfa)

Опциональный bridge для WEB proxy из Telegram Desktop: браузер клиента подключается к серверу по HTTPS и ретранслирует MTProto в Telegram DC. Носитель выбирается ключом `webProxyCarrierMode`: `https` — long-poll `POST /api/v1/{session,up,down}` (по умолчанию), `websocket` — одно соединение `GET /api/v1/ws` на сессию. Сверх лимитов сессий/потоков bridge отвечает `503`/`CLOSE`, поэтому один инстанс не выедается одним клиентом. Кнопка панели помечена как **«Ссылка Web (alfa)»**.

Статус — **alfa**: реализация покрыта тестами и проверена смоук-скриптом, но контракт ещё сверяется с Telegram Desktop, поведение и формат ссылок могут меняться между версиями. По умолчанию выключен (`webProxyEnabled: false`) и порт не занимает. Настройка — [Конфигурация → WEB proxy](configuration.md#web-proxy-browser-relay-bridge--alfa); локальная проверка без домена — [Диагностика](diagnostics.md#проверка-web-proxy-локально-без-домена).

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

Одна страница по адресу `http://<metricsHost>:<metricsPort>/` (по умолчанию `http://127.0.0.1:8081/`). Статика лежит в [`src/TgWsProxy/wwwroot/`](../src/TgWsProxy/wwwroot/): [`index.html`](../src/TgWsProxy/wwwroot/index.html), [`app.js`](../src/TgWsProxy/wwwroot/app.js), [`site.css`](../src/TgWsProxy/wwwroot/site.css) — без CDN, npm и сборщика, отдаётся тем же ASP.NET Core хостом, что и API.

### Что на странице

| Блок | Содержимое |
|---|---|
| **Обзор** | Плитки: время работы, активные соединения (из суммарных), отправлено и получено, отклонено (ACL / лимит), ошибки (WS / bad), распределение по маршрутам (WS / H2 / TCP), состояние слушателя, число отслеженных IP. |
| **Скорость трафика** | График скорости из кольцевого буфера (`timeseriesWindowSeconds` / `timeseriesStepSeconds`), две линии: к Telegram и от Telegram. |
| **Типы соединений** | Доли WS / H2 / TCP fallback / CF proxy / fronting / masked. |
| **Пул соединений** | Живые и прогреваемые WS-соединения, попадания и промахи пула, состояние CF-пула и H2. |
| **Токены доступа** | Таблица токенов: имя, маска секрета, состояние, отправлено/получено, число сессий, последнее использование. Действия: «Ссылка MTProto», «Ссылка Web (alfa)», «Изменить», «Ротация», «Включить/Отключить», «Удалить». Ниже — форма создания и блок одноразового показа секрета. |
| **Трафик по источникам** | Таблица с группировкой «IP + токен» / «только IP» / «только токен» и фильтром по префиксу адреса. |
| **Конфигурация** | Форма по группам с бейджами категорий («горячо» / «слушатель» / «только старт») и кнопками «Сохранить», «Перезагрузить», «Сбросить». |

Порядок групп в форме: первыми идут справочные блоки только для чтения (метка — значение, без полей ввода, потому что все их ключи относятся к «только старт») — **«Web API»**, **«Сеть»** (с подразделами **MTPROTO** и **WebProxy**) и **«Журналирование»**; далее на всю ширину — «Ссылки Telegram», «Безопасность», «DC и пул WebSocket», «Ограничения», «Cloudflare», «Мониторинг».

### Поведение и детали

- **Опрос раз в 2 с**; в скрытой вкладке опрос приостанавливается (событие `visibilitychange`).
- **Шапка**: индикатор состояния (`ok` / `restarting` / `unavailable`), версия сборки, переключатель темы (авто → светлая → тёмная, выбор в `localStorage`).
- **Аутентификация**: если задан `webApiToken`, страница один раз спрашивает его и держит в `sessionStorage` вкладки. Токен не попадает в исходники страницы, в URL и в состояние после закрытия вкладки.
- **Секреты**: в таблице только маска `abcd…ef01`. Полный секрет показывается однократно при создании и ротации, а кнопки ссылок забирают его через `GET /api/tokens/{id}/link` и сразу кладут в буфер обмена, не сохраняя в состоянии панели. В режиме «Изменить» поле секрета подставляется текущим значением: пусто — не менять, новое — смена секрета.
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

**WEB proxy**

- **Browser-relay bridge (alfa)** — то, чего в upstream нет: носитель сессии на выбор — HTTPS long-poll `POST /api/v1/{session,up,down}` или одно WebSocket-соединение `GET /api/v1/ws` (подпротокол `tproxy-v1.<token>`); bootstrap→session токены, HMAC-capability на bridge-странице, лимиты сессий/потоков/очередей, ссылки-приглашения по шаблону Telegram `https://t.me/webproxy?server=…&secret=…`. Контракт сверяется с `telegramdesktop/tproxy-server`.

**Поставка**

- **Docker-образ production-ориентированный**: том `/app/config`, `TGWS_CONFIG`, работа не от root, `EXPOSE 1443 8081`, `HEALTHCHECK` на `/healthz/ready`.
