# TG WS Proxy (C#)

Локальный MTProto-прокси для Telegram, который маршрутизирует трафик через WebSocket/TLS к дата-центрам Telegram и автоматически переключается на TCP fallback при проблемах с WS.

## Особенности проекта

- Поднимает локальный MTProto-сервер (по умолчанию `127.0.0.1:1080`).
- Определяет Telegram-трафик и извлекает DC из init-пакета MTProto.
- Пытается подключить трафик через WSS-домены Telegram (`/apiws`).
- При сбоях WS автоматически уходит в TCP fallback и продолжает работу.
- Поддерживает пул WebSocket-соединений, cooldown после ошибок и blacklist при постоянных redirect.
- **Поддерживает несколько MTProto-секретов** (`--secret SECRET`, можно указывать несколько раз).
- Поддерживает Cloudflare proxy fallback (`--no-cfproxy`, `--cfproxy-domain`, `--cfproxy-priority`).
- Раз в минуту пишет в лог сводку `stats` и `ws_bl` (чёрный список DC для WS после серии редиректов).

## Как это работает

```text
Telegram Desktop -> MTProto proxy -> TG WS Proxy -> WSS/TCP -> Telegram DC
```

## Аргументы запуска

| Аргумент | По умолчанию | Описание |
|---|---|---|
| `--port <PORT>` | `1080` | Порт локального MTProto-сервера. |
| `--host <HOST>` | `127.0.0.1` | Адрес локального MTProto-сервера. |
| `--dc-ip <DC:IP>` | нет (если не задан, в `Program` добавляются `2:149.154.167.220`, `4:149.154.167.220` и `203:149.154.167.220`) | Явное сопоставление дата-центра Telegram с IP. **Номер DC и IP должны соответствовать друг другу**, иначе TLS к `kws{N}.web.telegram.org` может обрываться. Можно указывать несколько раз. |
| `--secret <SECRET>` | нет (автогенерация) | MTProto-секрет (32 hex-символа). Можно указывать **несколько раз** для поддержки разных клиентов. Допустим формат с префиксом `dd`/`ee` (например `ddf43b...`), префикс автоматически strip-ится. |
| `--no-cfproxy` | выключен | Отключает fallback через Cloudflare-proxied WebSocket домены. |
| `--cfproxy-domain <DOMAIN>` | `pclead.co.uk` | Базовый домен для CF Proxy fallback. |
| `--cfproxy-priority <true\|false>` | `true` | Приоритет CF Proxy над TCP fallback (`true` = CF first, `false` = TCP first). |
| `-v`, `--verbose` | выключен | Включает подробное логирование (`Debug`). |
| `--allow-invalid-certs` | выключен | Разрешает TLS-подключение при невалидном сертификате (только для диагностики; по умолчанию выключено). |
| `--log-path <PATH>` | не задан | Путь к лог-файлу. Без `--log-max-mb` используется **почасовая** ротация (JSON). |
| `--log-max-mb <MB>` | `0` | Если `> 0`, ротация **по размеру** файла, минимальный размер одного файла 32 КБ. |
| `--log-backups <N>` | `0` | Сколько архивных файлов хранить при ротации по размеру (текущий + N). |
| `--ws-timeout <SEC>` | `10` | Таймаут (сек.) на **чтение** HTTP-ответа WS-рукопожатия. Этапы **TCP и TLS** ограничены `default(значение, 10)` секунд. Диапазон `1`…`300`. После ошибки WS используется быстрый повтор **2 с**. При проблемах в Docker см. раздел ниже. |
| `--buf-kb <KB>` | `256` | Размер `SO_RCVBUF`/`SO_SNDBUF` для клиентского сокета и исходящего WSS. |
| `--pool-size <N>` | `4` | Размер пула заранее открытых WS на пару (DC, media). `0` отключает пул. |
| `--ws-max-frame-bytes <BYTES>` | `1048576` | Максимальный размер payload одного WS-фрейма (в байтах). При превышении соединение закрывается и происходит TCP fallback. |

## Сборка

```bash
dotnet build TgWsProxy.slnx
```

### Docker: образ под Linux AArch64 (arm64)

`Dockerfile` рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), а `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64). Финальный слой берётся под целевую платформу (`TARGETPLATFORM`).

**Один образ только под AArch64** (на x86-ПК нужен [buildx](https://docs.docker.com/build/buildx/) и эмуляция или кросс-сборка .NET — образ SDK сам соберёт `linux-arm64`):

```bash
docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .
```

**Один тег для amd64 + arm64** (в registry):

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:2.2 --push .
```

Сборка **на самом ARM-сервере** (без эмуляции):

```bash
docker build -t tg-ws-proxy:local .
```

## Примеры запуска

Проект изначально рассчитан на **установку на сервер** (выделенная машина, контейнер на хосте, VPS): прокси слушает сеть, а **клиент Telegram подключается к этому серверу** по SOCKS5 (адрес/порт сервера или проброшенного порта), а не обязательно к `127.0.0.1` на той же машине, где открыт Telegram. Локальный запуск с `127.0.0.1` возможен, но типовой сценарий — сервер + удалённый клиент.

### Из исходников

```bash
# Запуск с настройками по умолчанию (секрет автогенерируется)
dotnet TgWsProxy.dll

# Другой порт и подробный лог
dotnet TgWsProxy.dll --port 9050 --verbose

# Привязка к другому хосту и явные DC
dotnet TgWsProxy.dll --host 0.0.0.0 --dc-ip 1:149.154.175.50 --dc-ip 2:149.154.167.220

# Запуск с несколькими MTProto-секретами (для разных клиентов)
dotnet TgWsProxy.dll --secret ddf43b08aef31a50b7b92c17f07bb66b11 --secret ddaaa111bbb222ccc333ddd444eee555

# С Cloudflare fallback через другой домен
dotnet TgWsProxy.dll --cfproxy-domain example.com --cfproxy-priority true
```

### Запуск опубликованного бинарника

```bash
# Пример для Windows PowerShell
.\TgWsProxy.exe --port 1080 --dc-ip 2:149.154.167.220 --dc-ip 4:149.154.167.220 --secret ddf43b08aef31a50b7b92c17f07bb66b11
```

### Docker Compose (`docker-compose.yml`)

Запуск из готового образа на [Docker Hub](https://hub.docker.com/r/pboldev/tg-ws-proxy) (тег образа задаётся в `docker-compose.yml`, по умолчанию в репозитории указан актуальный тег). При каждом `up` образ подтягивается заново (`pull_policy: always`).

**Требования:** установленные Docker и Docker Compose v2.

**Запуск в фоне:**

```bash
docker compose up -d
```

**С явным `.env`** (файл положите рядом с `docker-compose.yml`):

```env
TG_HOST=0.0.0.0
TG_PORT=1080
TG_WS_TIMEOUT=10
TG_DC_IP_1=2:149.154.167.220
TG_DC_IP_2=4:149.154.167.220
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

**Дополнительные аргументы** (`--verbose`, `--log-path`, несколько `--secret`, CF Proxy) добавьте в список `command` в `docker-compose.yml` (по одному элементу на строку), по аналогии с таблицей аргументов выше.

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

При запуске прокси выводит в лог готовую ссылку формата `tg://proxy?server=...&port=...&secret=dd...`. Нажмите на неё в логе — Telegram автоматически добавит прокси.

### Telegram Desktop

1. Откройте: **Настройки → Продвинутые настройки → Тип подключения → Прокси**.
2. Добавьте прокси типа **MTProto**:
   - Сервер: `127.0.0.1` (или адрес сервера)
   - Порт: `1080` (или ваш `--port`)
   - Секрет: `ddf43b08aef31a50b7b92c17f07bb66b11` (из лога при запуске)

### Telegram Mobile

Нажмите на ссылку `tg://proxy?...` из лога — Telegram автоматически предложит добавить прокси.

