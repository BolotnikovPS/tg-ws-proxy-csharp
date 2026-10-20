# Конфигурация

[← Оглавление](README.md)

## Файл `config.json`

`config.json` — единственный источник настроек, аргументы CLI ключи не перекрывают. Путь ищется так: `--config <PATH>` → переменная окружения `TGWS_CONFIG` → `./config.json`.

- Отсутствие файла — не ошибка, работают значения по умолчанию.
- Синтаксическая ошибка, неизвестный ключ или значение вне диапазона — завершение с кодом `1` и указанием строки.
- Допустимы комментарии (`//`, `/* */`) и висячие запятые; ключи в camelCase, регистр не важен.
- Полный список с комментариями — [`config.example.json`](../config.example.json), для контейнера — [`DockerCompose/config.docker.json`](../DockerCompose/config.docker.json).

> Приложение **перезаписывает файл** при сохранении из панели/API, при миграции старого `secret` в `tokens[]` и при автогенерации токена `default` для пустого списка. Запись атомарная, но комментарии и форматирование при этом теряются (пишется компактный JSON). Держите копию образца отдельно.

## Ключи, применяемые на лету

Меняются в панели или через `PUT /api/config` и вступают в силу сразу — ответ содержит `hotReloaded`.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `publicHost` | пусто | Внешний адрес для ссылок `t.me/proxy`: `host`, `host:port`, `[IPv6]:port`. Пусто — берутся `host` и `port`. Нужен, когда `host` служебный (`0.0.0.0` в контейнере) или прокси за NAT. |
| `webProxyPublicHost` | пусто | Внешний домен для ссылок и bridge-страницы WEB proxy (если bridge за reverse-proxy). Пусто — используется `webProxyHost`. Только домен: IP-адреса и хосты без точки Telegram не принимает, и ссылка не строится. |
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

## Ключи, требующие перезапуска слушателя

Применяются перезапуском листенера внутри процесса: останавливается приём, закрываются активные сессии, открывается новый сокет, затем применяется остальная конфигурация. Ответ содержит `listenerRestarted` и `droppedSessions`. Если новый сокет не удалось привязать — конфигурация и файл откатываются, ответ `409` с `rolledBack: true`, старый сокет продолжает работать.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `dcIp` | `1:…`…`5:…` | Сопоставление DC → IP. Пустой список отключает WS-пул и прямые WS. |
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

## Ключи, читаемые только при старте

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

## WEB proxy (browser-relay bridge) — alfa

> **Статус: alfa.** Возможность работает и покрыта тестами, но контракт ещё сверяется с Telegram Desktop, поведение и формат ссылок могут меняться между версиями. В панели кнопка помечена как «Ссылка Web (alfa)».

Опциональный bridge для WEB proxy из Telegram Desktop: браузер подключается к серверу по HTTPS и ретранслирует MTProto в Telegram DC. Включается `webProxyEnabled`; по умолчанию выключен и порт не занимает.

| Ключ | По умолчанию | Категория | Описание |
|---|---|---|---|
| `webProxyEnabled` | `false` | только старт | Включить bridge-листенер. |
| `webProxyHost` | `0.0.0.0` | только старт | Адрес привязки bridge. |
| `webProxyPort` | `443` | только старт | Порт bridge (должен отличаться от `port` и `metricsPort`). |
| `webProxyBasePath` | пусто | только старт | Путь сайта — голый слаг (`""` → корень, иначе `slug`). |
| `webProxyPublicHost` | пусто | горячо | Внешний домен для ссылок и bridge-страницы (только домен, не IP). |
| `webProxyCertPath` | пусто | только старт | PFX/сертификат для TLS (обязателен при `webProxyEnabled`). |
| `webProxyCertPassword` | пусто | только старт | Пароль PFX (в GET API маскируется). |
| `webProxyCarrierMode` | `https` | горячо | Носитель bridge-сессии: `https` (long-poll `POST /api/v1/{session,up,down}`) или `websocket` (одно соединение `GET /api/v1/ws` на сессию). Режимы `https-lanes`/`websocket-lanes` не поддержаны и отклоняются валидацией. |
| `webProxyMaxSessions` | `128` | горячо | Предел одновременных bridge-сессий; сверх лимита создание отвечает `503` + `Retry-After`. |
| `webProxyMaxSessionsPerIp` | `0` | горячо | Предел сессий с одного IP (`0` — без ограничения). |
| `webProxyMaxStreamsPerSession` | `128` | горячо | Потоков relay в одной сессии; `OPEN` сверх лимита получает `CLOSE`. |
| `webProxyMaxStreams` | `4096` | горячо | Глобальный предел relay-потоков на процесс. |
| `webProxyMaxPendingBytesPerSession` | `33554432` | горячо | Объём исходящей очереди одной сессии (32 МиБ; допустимо 1 … 64 МиБ). Переполнение закрывает сессию — клиент переподключается. |
| `webProxyMaxPendingItemsPerSession` | `16384` | горячо | Кадров в исходящей очереди одной сессии. |

Bridge слушает `https://webProxyHost:webProxyPort` с путём `webProxyBasePath`: корень `GET /?bridge=<capability>` отдаёт динамическую bridge-страницу со встроенным JS-носителем и одноразовым bootstrap-токеном (авторизация по HMAC-SHA256 секрета токена), а `…/api/v1/{session,up,down}` — HTTPS long-poll carrier с заголовком `Authorization: Bearer <token>`.

Носитель выбирает сервер: режим из `webProxyCarrierMode` подставляется в bridge-страницу и повторяется в ответе `POST /api/v1/session` заголовком `X-Carrier-Mode`; клиент обязан использовать именно его. В режиме `websocket` тот же поток кадров идёт через `GET …/api/v1/ws` с подпротоколом `tproxy-v1.<token>` (одно соединение на сессию). Сессия привязана к своему носителю: запросы «чужого» carrier'а (например `POST /up` при `carrier=websocket`) получают `404`. Ключи `webProxyEnabled`/`Host`/`Port`/`BasePath`/`CertPath`/`CertPassword` применяются только перезапуском; `webProxyPublicHost`, `webProxyCarrierMode` и все лимиты — горячие (новый носитель действует на новые сессии, уже открытые доживают на прежнем).

Проверить bridge без клиента можно смоук-скриптом [`tools/web-proxy-smoke.mjs`](../tools/web-proxy-smoke.mjs) (Node.js ≥ 18, без зависимостей) — он эмулирует bridge-клиента: `GET /?bridge=…` (bootstrap) → `POST /api/v1/session` (`HELLO→WELCOME`) → носитель (`POST /api/v1/up` + `/down` либо `GET /api/v1/ws`) → `DELETE /api/v1/session`. Транспортный режим детерминирован и не требует доступа к Telegram DC:

```bash
node tools/web-proxy-smoke.mjs --host bridge.example.com --secret <32hex> [--port 443] [--base-path slug] [--carrier https|websocket|auto] [--ca certs/bridge.pem] [--insecure]
```

`--full` отправляет настоящий obfuscated2 init и ждёт `relay_init` (нужна сеть с доступом к Telegram DC). `--insecure` отключает проверку TLS для самоподписанного тестового сертификата, а `--ca <pem>` наоборот — оставляет проверку включённой и доверяет только указанному корню (см. [Диагностика](diagnostics.md#проверка-web-proxy-локально-без-домена)).

## Как применяются изменения

1. Значения с маской (`abcd…ef01`) означают «не менять».
2. Слияние с текущей конфигурацией → валидация → классификация ключей.
3. Ключи «только старт» отклоняются до записи файла (`409 startupOnly`).
4. Запись во временный файл и атомарное переименование.
5. Если затронуты ключи «слушатель» — перезапуск листенера; при неудаче файл откатывается.
6. Применение «горячих» ключей и предупреждения в ответе.
