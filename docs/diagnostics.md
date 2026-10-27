# Диагностика

[← Оглавление](README.md)

## Сеть в Docker: TLS, warmup, `EOF`, `RST`

На **хосте** `dotnet`/бинарник ходит в интернет напрямую. В **контейнере** трафик идёт через bridge/NAT (на Windows/macOS ещё и через виртуализацию), поэтому проблемы всплывают чаще:

| Причина | Что попробовать |
|---|---|
| **MTU** (VPN, PPPoE, некоторые провайдеры) | Пакеты к `443` «зависают» или обрываются. Уменьшить MTU у Docker (`daemon.json` → `"mtu": 1400`) или подключить оверлей **`docker-compose.mtu.yml`** (сеть с MTU 1400). |
| **Маршрут VPN только для хоста** | Исходящие из bridge не попадают в туннель. **Linux:** оверлей **`docker-compose.host.yml`** (`network_mode: host`). |
| **DNS в контейнере** | Оверлей **`docker-compose.dns.yml`** (например Cloudflare/Google DNS). |
| **Проверка в той же сети, что у прокси** | `docker run --rm --network container:tg-ws-proxy nicolaka/netshoot openssl s_client -connect 149.154.167.99:443 -servername kws2.web.telegram.org -brief` — если здесь обрыв или таймаут, проблема в сети Docker/хоста, а не в коде прокси. |

Запуск с оверлеями:

```bash
docker compose -f docker-compose.yml -f docker-compose.mtu.yml up -d
docker compose -f docker-compose.yml -f docker-compose.dns.yml up -d
# только Linux:
docker compose -f docker-compose.yml -f docker-compose.host.yml up -d
```

`network_mode: host` на **Docker Desktop** для Windows/macOS обычно **не** даёт того же эффекта, что на Linux; там смотрите MTU, WSL2/VPN и настройки Docker.

## Ссылка `t.me/proxy` не строится

Значит внешний адрес не определён: слушатель всегда привязан ко всем интерфейсам (`0.0.0.0`), а `publicHost` пуст. Задайте `publicHost` в панели (раздел «Ссылки Telegram») — применится сразу, перезапуск не нужен.

## Панель отвечает `401`

Задан `webApiToken`, а в браузере он не введён (или введён неверно). Токен хранится в `sessionStorage` вкладки: после её закрытия спрашивается заново. Сброс — очистка данных сайта.

## Панель отвечает `409`

- `startupOnly` — ключ из категории «только старт», меняйте файл и перезапускайте процесс;
- `rolledBack` — новый адрес прослушивания не удалось занять, изменения отменены, старый сокет работает;
- без поля — уже идёт перезапуск слушателя, повторите через секунду (`Retry-After: 1`).

## Нет кнопки «Ссылка Web» в панели

Статика (`app.js`, `site.css`) отдаётся с `Cache-Control: no-cache`, но если браузер всё равно держит старую копию — обновите страницу с очисткой кэша (`Ctrl+F5`). Кнопка рендерится всегда; ссылка возвращается только при `"webProxy": { "enabled": true }` и корректном внешнем адресе.

## WEB proxy: ссылка на токен не строится

Кнопка **«Ссылка Web»** копирует `https://t.me/webproxy?server=…&secret=…`. Ссылка не строится, если внешний адрес — IP (`127.0.0.1`, `127.1`, `0x7f.1`), хост без точки (`localhost`) или wildcard (`0.0.0.0`): Telegram принимает для WEB proxy только доменное имя. Задайте `webProxy.publicHost` (например `proxy.example.com`) — он же должен быть достижим с устройства клиента и покрыт сертификатом `webProxy.certPath`.

## WEB proxy: bridge-страница открывается, но `relay_init` не приходит

Смоук `tools/web-proxy-smoke.mjs` без `--full` проверяет только транспорт и авторизацию. Отсутствие `relay_init` в режиме `--full` означает, что прокси не достучался до Telegram DC:

1. Проверьте `dcIp` — адреса DC меняются, сверяйте с DNS `kws1..kws5.web.telegram.org`.
2. Проверьте сетевой доступ: `curl -vk --max-time 10 https://kws2.web.telegram.org/apiws -o NUL`. TCP-коннект проходит, а TLS-таймаут — признак DPI/блокировки.
3. Если и TLS, и сырой MTProto к DC молчат — прокси нужно запускать на VPS/VPN.

Подробнее о ключах — [Конфигурация → WEB proxy](configuration.md#web-proxy-browser-relay-bridge).

## Проверка WEB proxy локально, без домена

Для локального прогона не нужен публичный домен: возьмите имя, которое уже резолвится в `127.0.0.1` — `lvh.me` (или `*.lvh.me`), `localtest.me`, `127.0.0.1.sslip.io`. Telegram принимает только домен, поэтому `webProxy.publicHost: "127.0.0.1"` для ссылок не годится, а `lvh.me` — годится.

1. Сертификат с нужным SAN (скрипт не требует прав администратора — генерирует в `Cert:\CurrentUser\My`):

   ```powershell
   .\tools\scripts\cert.ps1 -DnsName lvh.me -Password MyPfxPassword -PfxName bridge-local.pfx
   ```

   Флаг `-Local` дополнительно кладёт `lvh.me` и `*.lvh.me` в SAN, `-Trust` импортирует сертификат в `Cert:\CurrentUser\Root` (тогда браузер не показывает предупреждение; удалить — `cert.ps1 -Remove <thumbprint>`).

2. Конфиг: `webProxy.enabled: true`, `webProxy.port: 443`, `webProxy.publicHost: "lvh.me"`, `webProxy.certPath` — путь к `certs/bridge-local.pfx`, `webProxy.certPassword` — пароль PFX.

3. Запуск и смоук по обоим носителям:

   ```powershell
   dotnet run --project src/TgWsProxy -- --config config.json
   node tools/web-proxy-smoke.mjs --host lvh.me --secret <32hex> --carrier websocket --ca certs/bridge-local.pem
   node tools/web-proxy-smoke.mjs --host lvh.me --secret <32hex> --carrier https --ca certs/bridge-local.pem
   ```

   `--ca` принимает PEM (не DER) и оставляет проверку TLS включённой: скрипт доверяет только этому корню и не меняет системное хранилище. Если PEM нет, экспортируйте его из PFX или используйте `--insecure`.

4. Смену носителя на живом процессе можно проверить через API: `PUT /api/config` с `{"webProxy":{"carrierMode":"websocket"}}` — ключ горячий, перезапуск не требуется.

Сертификат из `certs/` попадёт в git только при явном `git add -f`: `*.pfx` и `*.pem` не отслеживаются.

## E2E с Telegram Desktop

WEB proxy в Telegram Desktop — proof-of-concept: код есть в дереве v7.3.1, но тип `WEB` может отсутствовать в релизном билде. Сначала убедитесь, что он есть: `Settings → Advanced → Connection type → Proxy settings → Add proxy`. Если пункта `WEB` нет — нужен самостоятельный билд tdesktop из dev-ветки (это не задача сервера).

Клиент принимает два значения, **без** `https://`, порта и query:

| Поле | root (`webProxy.basePath: ""`) | c base path (`webProxy.basePath: "v1"`) |
|---|---|---|
| Proxy server | `proxy.example.com` | `proxy.example.com/v1` |
| Proxy secret | `<32-hex>` | `<32-hex>` — при ручном вводе остаётся plain-hex |

Ссылка для расшаривания (под base path секрет — маркированная форма `0x70‖secret` в base64url):

```text
https://t.me/webproxy?server=proxy.example.com%2Fv1&secret=<base64url>
tg://webproxy?server=proxy.example.com%2Fv1&secret=<base64url>
```

Роут `t.me/webproxy` Telegram пока не зарегистрирован — PoC-тест предполагает ввод значений вручную либо открытие ссылки прямо в клиенте.

### Локально (Telegram Desktop на той же машине)

1. Доверить самоподписанный серт именно для WebView — Windows-клиент грузит bridge в WebView2, который использует системное хранилище Windows (Node `--ca` его не заменяет):

   ```powershell
   .\tools\scripts\cert.ps1 -Local -Trust
   ```

2. Конфиг как в разделе «Проверка WEB proxy локально» (`webProxy.publicHost: "lvh.me"`, `webProxy.basePath` по необходимости).
3. Клиент: тип `WEB`, server `lvh.me` или `lvh.me/v1`, secret `<32-hex>`, включить.
4. Строка прокси должна сменить `connecting…` → `online` в пределах ~10 с.

⚠️ На сети с блокировкой DC (TLS/MTProto к `kws*.web.telegram.org` не проходит) клиент подключится, `WINDOW` заработает, но потоки MTProto повиснут на `relay_init` — как в `--full`-смоуке. Это барьер сети, а не сервера.

### На VPS (полноценный E2E)

1. Домен с `A`-записью на VPS (IP или хост без точки клиент отвергнет), порты 80/443 открыты.
2. Валидный серт (Let's Encrypt перед Kestrel либо PFX от ЦС в `webProxy.certPath`).
3. `webProxy.publicHost: "<домен>"`; сервер на VPS должен иметь доступ к Telegram DC.
4. Клиент: `WEB` → server `<домен>` или `<домен>/<slug>`, secret `<32-hex>` → включить.
5. Успех: строка `online`, грузятся диалоги, идут сообщения и медиа.

### Сигналы «сервер готов, проблема не в нём»

- смоук зелёный (оба носителя) — транспорт/HMAC/flow-control исправны;
- bridge-страница открывается в браузере с доверенным сертом (`-Trust`) — WebView-путь рабочий;
- в логах прокси при подключении клиента виден `WS pool refilled/hit` или `fallback` — мост реально пошёл к DC;
- если при этом строка висит `connecting…` — проверяйте DC-доступ: `curl -vk --max-time 10 https://kws2.web.telegram.org/apiws`.
