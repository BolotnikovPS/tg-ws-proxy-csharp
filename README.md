# TG WS Proxy (C#)

Локальный MTProto-прокси для Telegram на C#/.NET 8: маршрутизирует трафик через WebSocket/TLS к дата-центрам Telegram, умеет маскироваться под обычный HTTPS (Fake TLS) и автоматически переключается на резервные транспорты (Cloudflare Worker → Cloudflare → TCP) при проблемах с WS.

Проект — порт Python-реализации [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy). Исходники **актуализированы до состояния upstream v1.11.2** (модульный пакет `proxy/`, 336 коммитов) и оптимизированы по горячим путям.

## Возможности

- Локальный MTProto-сервер (по умолчанию `127.0.0.1:1443`).
- Определение Telegram-трафика и извлечение DC из init-пакета MTProto (abridged / intermediate / padded intermediate — «secure»).
- Основной транспорт — WSS к `/apiws` (или `/apiws_test` для тестовых DC).
- **Пул WS-соединений** per (DC, media): предварительный прогрев, TTL 120 с, периодическая чистка, refill с экспоненциальным backoff (1 с → 3600 с).
- **TCP fallback** с экспоненциальным backoff (30 с → 3600 с) вместо прежнего фиксированного cooldown.
- **Cloudflare fallback**: балансировщик CF-доменов (20 встроенных «обфусцированных» доменов, автообновление раз в час с GitHub, минимум 3 валидных), пул Cloudflare Worker-доменов и прямые CF-proxy подключения.
- **HTTP/2 мультиплексирование media-трафика** через Cloudflare (`/api`): переиспользование одного H2-соединения каналами (lane), учёт бюджета байт/запросов, восстановление потерянных пакетов (replay/recovery).
- **Fake TLS (ee-secret) маскировка**: приём TLS ClientHello, ответ ServerHello + ChangeCipherSpec, упаковка MTProto в TLS application data; при невалидном секрете — проксирование на домен-маскировку; при обычном HTTP — редирект 301.
- **PROXY protocol v1** (работа за nginx/haproxy).
- Поддержка тестовых DC Telegram (`dc >= 10000` или `--force-test-dc`).
- Watchdog слушающего сокета: раз в 5 с проверяет живость сокета и перезапускает сервер.
- **Управление доступом по IP** (`--allow-ip`/`--deny-ip` с CIDR): блокировка до рукопожатия по реальному IP пира, отдельный счётчик `blocked`.
- Журнал подключений: connect и итог каждой сессии (peer, DC, media, маршрут, байты up/down, длительность).
- Один MTProto-секрет (32 hex); при отсутствии — автогенерация. В лог пишутся готовые ссылки `dd…` и (при `--fake-tls-domain`) `ee…`.
- Раз в минуту — сводка `stats` с счётчиками (WS/H2/TCP/CF/fronting/masking/bad/blocked, байты, попадания в пул).

## Архитектура

| Проект | Назначение |
|---|---|
| `TgWsProxy.Domain` | Конфигурация (`Config`), модель handshake (`MtProtoHandshake`), исключения. Без внешних зависимостей. |
| `TgWsProxy.Application` | Разбор CLI и валидация конфига, протокольные хелперы (`Protocol`, `MsgSplitter`, `IncrementalCipher`, `CryptoCtx`, `H2PacketCodec`), Fake TLS, `MtProtoInspector`, `ClientInitReader`, `ClientSessionHandler`, статистика. |
| `TgWsProxy.Infrastructure` | Сетевые реализации: `ProxyServer` (+ watchdog), `RawWebSocket(Factory)`, `WsPool`, `CfWorkerPool`, `CfDomainBalancer`/`CfDomainRefresher`, `CfH2Pool`/`CfH2Lane`/`CfH2Channel`/`H2Bridge`, `TcpBridgeService`. |
| `TgWsProxy` | Точка входа: DI, логирование с ротацией, запуск сервера, graceful shutdown. |
| `TgWsProxy.Test` | xUnit-тесты (114 шт.): CLI/Config, протокол, Fake TLS, H2-пакеты, WS-кадрирование, регрессия порядка перешифрования, liveness пула. |

## Как это работает

```text
Telegram Client -> MTProto proxy (этот сервис) -> WSS/CF-H2/TCP -> Telegram DC
```

Порядок выбора транспорта для каждого нового подключения:

1. **Пул прямых WS** (если пул не отключён и DC не в «cooldown» после ошибки).
2. **Cloudflare Worker** (`--cfproxy-worker-domain`) — если заданы.
3. **Cloudflare**: для media-трафика — HTTP/2 мультиплексирование на `/api` (если H2 доступен), иначе WS через CF-proxy домен.
4. **TCP fallback** на IP дата-центра (с экспоненциальным backoff при повторных неудачах).

Для каждого соединения MTProto-трафик перешифровывается: расшифровка клиентским ключом и шифрование ключом Telegram (и наоборот в обратную сторону).

## Аргументы запуска

| Аргумент | По умолчанию | Описание |
|---|---|---|
| `--port <PORT>` | `1443` | Порт локального MTProto-сервера. |
| `--host <HOST>` | `127.0.0.1` | Адрес прослушивания. |
| `--secret <HEX>` | автогенерация | MTProto-секрет: ровно 32 hex-символа. |
| `--dc-ip <DC:IP>` | `2:149.154.167.220`, `4:149.154.167.220` | Явное сопоставление DC → IP. Можно указывать несколько раз. **Голый `--dc-ip` без значения очищает список** (WS-пул и прямые WS отключаются, весь трафик идёт в fallback). |
| `-v`, `--verbose` | выключено | Подробное логирование (`Debug`). |
| `--log-file <PATH>` | нет (только консоль) | Лог в файл с ротацией. Алиас `--log-path` сохранён для совместимости. |
| `--log-max-mb <MB>` | `5` | Лимит размера файла лога перед ротацией. |
| `--log-backups <N>` | `1` | Сколько архивных лог-файлов хранить (минимум 1). |
| `--buf-kb <KB>` | `256` | Размер `SO_RCVBUF`/`SO_SNDBUF` клиентского сокета и исходящего WSS. |
| `--pool-size <N>` | `4` | Размер пула WS на пару (DC, media); `0` отключает прямые WS. |
| `--cfproxy-domain <DOMAIN>` | встроенный список | Пользовательские CF-proxy домены для H2 media и WS fallback. Можно указывать несколько раз; значения делятся по `,`, `;`, пробелам. |
| `--cfproxy-worker-domain <DOMAIN>` | нет | Домены Cloudflare Worker (пробуются первыми в fallback). Можно несколько раз. |
| `--no-h2` | H2 включён | Отключает HTTP/2 мультиплексирование media через CF. |
| `--no-cfproxy` | CF включён | Полностью отключает Cloudflare fallback. |
| `--no-secure` | выключено | Использовать порт 80 вместо 443 для CF-proxy и CF-worker. |
| `--fake-tls-domain <DOMAIN>` | пусто | Включает Fake TLS (ee-secret) маскировку с указанным доменом. |
| `--force-test-dc` | выключено | Весь трафик направлять в тестовые DC Telegram. |
| `--proxy-protocol` | выключено | Принимать заголовок PROXY protocol v1 (nginx/haproxy). |
| `--allow-ip <IP|CIDR>` | пусто | Разрешить подключение только с перечисленных IP/подсетей. Можно несколько раз; значения делятся по `,`, `;`, пробелам. Пустой список — пускать всех. **Расширение порта**. |
| `--deny-ip <IP|CIDR>` | пусто | Заблокировать перечисленные IP/подсети. Можно несколько раз; `deny` приоритетнее `allow`. Пустой список — никого не блокировать. **Расширение порта**. |
| `--allow-invalid-certs` | выключено | Разрешать TLS с невалидным сертификатом. **Расширение порта**, в upstream отсутствует; только для диагностики. |
| `--ws-timeout <SEC>` | `10` | Таймаут чтения ответа WS-рукопожатия; этапы TCP и TLS ограничены `min(значение, 10)` с. **Расширение порта**. |
| `--ws-max-frame-bytes <BYTES>` | `16777216` (16 MiB) | Максимальный payload одного WS-фрейма (защита от DoS). **Расширение порта**. |
| `-h`, `--help` | — | Печатает справку по всем параметрам и завершает работу с кодом 0. |
| `--version` | — | Печатает версию (`tg-ws-proxy <версия>`) и завершает работу с кодом 0. |

## Сборка и тесты

```bash
dotnet build TgWsProxy.slnx
dotnet test TgWsProxy.slnx
```

## Docker

`Dockerfile` рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), а `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64). Финальный слой берётся под целевую платформу (`TARGETPLATFORM`).

**Один образ только под AArch64:**

```bash
docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .
```

**Один тег для amd64 + arm64** (в registry):

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:2.4 --push .
```

Сборка **на самом ARM-сервере** (без эмуляции):

```bash
docker build -t tg-ws-proxy:local .
```

### CI/CD (GitHub Actions)

Workflow [`.github/workflows/docker-build.yml`](.github/workflows/docker-build.yml) при пуше в `main` и на тег `v*` / `[0-9]*`:

1. гоняет `dotnet test` (сборка + тесты);
2. собирает мультиархитектурный образ `linux/amd64,linux/arm64` через BuildKit;
3. пушит в Docker Hub `pboldev/tg-ws-proxy`: пуш в `main` — теги `latest` и `main`; тег вида `v2.4` или `2.4` — семантический тег образа `2.4`.


### Docker Compose (`DockerCompose/docker-compose.yml`)

Запуск из готового образа на [Docker Hub](https://hub.docker.com/r/pboldev/tg-ws-proxy). При каждом `up` образ подтягивается заново (`pull_policy: always`).

```bash
cd DockerCompose
docker compose up -d
```

**С явным `.env`** (файл положите рядом с `docker-compose.yml`):

```env
TG_HOST=0.0.0.0
TG_PORT=1443
TG_DC_IP_1=2:149.154.167.220
TG_DC_IP_2=4:149.154.167.220
TG_ALLOW_IP=
TG_DENY_IP=
```

```bash
docker compose --env-file .env up -d
```

Переменные подставляются в `command` и в проброс порта `ports` (хост и контейнер используют один и тот же `TG_PORT`).

**Логи и остановка:**

```bash
docker compose logs -f tg-ws-proxy
docker compose down
```

**Дополнительные аргументы** (`--verbose`, `--log-file`, `--secret`, CF Proxy, `--fake-tls-domain`, `--no-h2`) добавьте в список `command` в `docker-compose.yml` (по одному элементу на строку). `--allow-ip`/`--deny-ip` уже подключены к переменным `TG_ALLOW_IP`/`TG_DENY_IP` — перечисляйте IP/подсети через запятую (пустое значение отключает фильтр).

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

При запуске прокси выводит в лог готовые ссылки:

- `tg://proxy?server=...&port=...&secret=dd<secret>` — обычный режим;
- `tg://proxy?server=...&port=...&secret=ee<secret><hex(домен)>` — если задан `--fake-tls-domain` (маскировка под HTTPS к указанному домену).

Нажмите на ссылку в логе — Telegram автоматически добавит прокси.

### Telegram Desktop

1. Откройте: **Настройки → Продвинутые настройки → Тип подключения → Прокси**.
2. Добавьте прокси типа **MTProto**:
   - Сервер: `127.0.0.1` (или адрес сервера)
   - Порт: `1443` (или ваш `--port`)
   - Секрет: `dd…` или `ee…` из лога при запуске.

### Telegram Mobile

Нажмите на ссылку `tg://proxy?...` из лога — Telegram автоматически предложит добавить прокси.

## Отличия от upstream

Порт следует upstream v1.11.2 по поведению и CLI. Осознанные отличия:

- **`--auth` и `--cfproxy-priority` удалены** (в upstream их больше нет; несколько секретов заменены одним).
- **`blacklist` WS-доменов удалён** — вместо постоянного исключения DC используется пул + экспоненциальный backoff.
- Добавлены расширения, которых нет в upstream: `--ws-timeout`, `--ws-max-frame-bytes`, `--allow-invalid-certs`, `--allow-ip`, `--deny-ip`, алиас `--log-path`.
- Добавлен watchdog слушающего сокета (в upstream — `install_self_pipe_guard`/перезапуск сервера).
