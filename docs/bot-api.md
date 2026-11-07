# Bot API Gateway для TBotPlatform (нативный MTProto)

Встроенный HTTP-шлюз для ботов Telegram на базе TBotPlatform. Сервис **сам** выполняет
MTProto-RPC в Telegram по TCP (или через собственный MTProxy при заданном `secret`),
а не редиректит запросы на `api.telegram.org` — то есть это локальный Bot API-сервер,
а не прозрачный HTTP-прокси.

## Зачем нужен

- **Единая точка входа** — боты ходят через этот же хост, трафик централизованно
  логируется, мониторится и контролируется.
- **Совместимость с TBotPlatform** — стандартный формат `/bot{token}/{method}`
  работает без изменений кода бота.
- **Собственная авторизация MTProto** — логин бота выполняется штатным
  `auth.importBotAuthorization`, сессии ботов кэшируются на диск.
- **Маскирование секретов** — токены ботов автоматически маскируются в логах
  (формат `12345:***`).
- **Метрики** — Prometheus-счётчики нагрузки и ошибок.

## Архитектура

```
TBotPlatform (бот)
  │
  │ HTTP POST /bot{token}/{method}   (JSON / query / multipart)
  │
  ▼
BotApiEndpoints (TgWsProxy.Web)          — маршрутизация, разбор args, multipart
  │
  ▼
BotGateway (TgWsProxy.Infrastructure)    — диспетчер методов, DTO-маппинг TL ↔ Bot API
  │
  ▼
MtProtoClient (TgWsProxy.Infrastructure) — нативный MTProto-RPC (auth key, шифрование)
  │
  ▼
Telegram DC (TCP / MTProxy при botApi.secret)
  │
  └─► Bot API-ответ {ok:true,result} / {ok:false,error_code,description}
```

Сервис не перечисляет методы «прозрачно»: каждый поддерживаемый Bot API-метод
явно сопоставлен с конкретным MTProto-методом и DTO-сериализацией.

## MTProto-слой (TgWsProxy.Infrastructure/MtProto)

Реализация MTProto в `Infrastructure`: транспорт, криптография, handshake, RPC и «bridge»
уже установленной сессии. TL-слой сгенерирован (layer 229), реестр конструкторов собирается рефлексией.

**Hand-written движок**

| Файл | Роль |
| --- | --- |
| `MtProtoBridgeEngine.cs` | ядро «после handshake» (init → relay → Telegram) |
| `MtProtoClient.cs` | нативный MTProto-RPC: auth key, шифрование, вызов методов |
| `MtProtoCrypto.cs` | криптография: AES-IGE, RSA, DH, факторизация |
| `MtProtoTransport.cs` | transport-кадрирование |
| `MtProtoInspector.cs` / `IMtProtoInspector.cs` | obfs2-handshake / relay-init |
| `ClientInitReader.cs` / `ClientInitResult.cs` / `ClientInitStatus.cs` | разбор клиентского `init` |
| `BotGateway.cs` / `BotSession.cs` / `BotSessionStore.cs` | bot-сессии (bot API поверх MTProto) |

**Сгенерированный TL-слой**

| Файл | namespace | Роль |
| --- | --- | --- |
| `TL.Schema.cs` | `…MtProto.TL` | полная schema layer 229 (1473 `[TLDef]`) |
| `TL.MTProto.cs` | `…MtProto` | transport/auth-типы (`ResPQ`, `ServerDH…`, 56 `[TLDef]`) |
| `TL.Methods.cs` | `…MtProto.TL.Methods` | RPC-методы (`IMethod<>`, 29 `[TLDef]`) |
| `TL.Xtended.cs` | `…MtProto.TL` | engine-хелперы (`IPeerInfo`, `InputPeer*`, приведения) |

**Обвязка типов и сериализация** (разнесённый `TL.cs`)

| Файл | Роль |
| --- | --- |
| `TlAttributes.cs` | `[TLDef]`, `[IfFlag]` |
| `TlInterfaces.cs` | `IObject`, `IMethod<>`, `IPeerResolver` |
| `TlPrimitives.cs` | `TLVector`, `Int128`, `Int256`, `BoolValue` |
| `TlSerializer.cs` | `Serialization`: read/write значений, строк, байтов, vector, dictionary, gzip |
| `MtProtoEnvelope.cs` | envelope-типы tdesktop: `RSAPublicKey`, `RpcResult`, `_Message`, `MsgContainer`, `MsgCopy`, `GzipPacked` |
| `TL.Table.cs` | `Layer.Table` (ctor → Type), `Nullables`, `EmptyCtors` |

`RpcException` вынесена в `TgWsProxy.Domain.Exceptions` — это доменное исключение («RPC вернул
ошибку»), одноранговое с `MTProtoTransportException`.

> `IObject` / `IMethod<>` — маркеры wire-схемы, `IPeerResolver` — внутренняя утилита адаптера
> (реализована ~100 типами схемы, тянет `Peer`/`IPeerInfo`). Они намеренно остаются в
> `Infrastructure.MtProto`, а не в `Application`: это контракты адаптера, а не порты прикладного слоя.

### Реестр конструкторов

`Layer.Table` (`ctor → Type`) строится один раз рефлексией по сборке `TgWsProxy.Infrastructure`:

1. сканируются все типы с `[TLDef]`;
2. `IMethod<>`-реализации исключаются (это вызовы, а не данные);
3. ctor из `EmptyCtors` (null/empty/notModified/default) исключаются — такие конструкторы
   десериализуются в `null` (например `Null#56730bcc`, `emojiStatusEmpty#2de11aae`);
4. дубликат ctor → `InvalidOperationException` («Duplicate TL constructor»).

`Nullables` (`base-type → ctor`) — ctor empty/notModified-варианта базового типа, используется
`Serialization.WriteTLNull`. `EmptyCtors` (`ctor → null`) — конструкторы, читающиеся как `null`.

Преимущество перед прежней ручной таблицей на ~1650 строк: реестр не может разойтись с
`[TLDef]`-атрибутами — расхождение либо отсутствует, либо падает сразу при первом обращении.

## Включение

В `config.json` добавьте блок `botApi`:

```json
{
  "botApi": {
    "enabled": true
  }
}
```

Маршруты регистрируются только при `enabled: true`; при `false` (по умолчанию)
маршруты Bot API не создаются. **Все ключи блока — «только старт»** (изменение
через `PUT /api/config` отклоняется с кодом 409 `startupOnly`).

## Формат запросов

```
GET  /bot{token}/{method}          — аргументы из query-строки
POST /bot{token}/{method}          — аргументы из JSON-тела или multipart/form-data
GET  /file/bot{token}/{file_path}  — скачивание файла, предварительно полученного getFile
POST /file/bot{token}/{file_path}
```

Примеры:

```bash
# Информация о боте
curl http://localhost/bot123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11/getMe

# Отправить сообщение
curl -X POST http://localhost/bot123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11/sendMessage \
  -H "Content-Type: application/json" \
  -d '{"chat_id": 123456789, "text": "Привет!"}'

# Отправить фото (multipart)
curl -X POST http://localhost/bot123456:ABC-DEF/sendPhoto \
  -F "chat_id=123456789" \
  -F "photo=@/path/to/image.jpg" \
  -F "caption=Описание фото"
```

## Заголовки apiId / apiHash

Для первого входа бота (создание auth_key и `auth.importBotAuthorization`) нужны
`api_id` и `api_hash`, полученные на [my.telegram.org](https://my.telegram.org):

- `X-Telegram-Api-Id`
- `X-Telegram-Api-Hash`

Они используются **только один раз** при логине: после сохранения сессии
(`{botId}.bin` в `botApi.dataDir`) повторная авторизация выполняется из сессии
и заголовки не обязательны. `apiHash` в логах маскируется (видны только последние
4 символа).

## Поддерживаемые методы

Реализуются тирами; каждый метод явно привязан к MTProto-методу.

**Ядро:** `getMe`, `getUpdates`, `sendMessage`.

**Тир 1 — медиа и файлы:** `sendPhoto`, `sendDocument`, `sendAudio`, `sendVideo`,
`sendAnimation`, `sendVoice`, `sendVideoNote`, `sendSticker`, `sendMediaGroup`,
`getFile` (+ реальный маршрут `/file/bot*`).

**Тир 2 — правка и пересылка:** `editMessageText`, `editMessageCaption`,
`editMessageMedia`, `deleteMessage`, `forwardMessage`, `copyMessage`,
`answerCallbackQuery`.

**Тир 3 — чаты:** `getChat`, `getChatAdministrators`, `getChatMember`,
`getChatMemberCount`, `setChatTitle`, `setChatDescription`, `setChatPhoto`,
`leaveChat`, `banChatMember`.

**Тир 4 — служебные:** `sendChatAction`, `setMyCommands`, `getMyCommands`,
`answerInlineQuery`.

Неизвестный метод возвращает `{ok:false, error_code:501, description:"method '...' is not implemented"}`.

## Скачивание файлов (getFile)

`getFile` скачивает файл по `file_id` фрагментами через `upload.getFile` и кладёт
его в in-memory LRU-кэш, после чего возвращает объект `File`:

```json
{ "ok": true, "result": {
    "file_id": "…", "file_unique_id": "…",
    "file_size": 12345, "file_path": "<base64url-32>" } }
```

Затем файл отдаётся `GET /file/bot{token}/{file_path}`, где `file_path` — неотгадываемый
маркер кэша. Ответ отдаётся потоком с `Content-Type` (по умолчанию
`application/octet-stream`).

Ограничения:

- Размер вложения ограничен `botApi.maxFileCacheBytes`; превышение — `413`.
- Кэш — только в памяти (суммарный лимит `maxFileCacheBytes`), LRU-вытеснение по
  последнему обращению.
- **CDN-файлы** (`upload.fileCdnRedirect`) пока не поддерживаются — возвращается
  `502` (требуется переход на второй DC и расшифровка; в плане).

## Конфигурация

| Ключ | Тип | По умолчанию | Описание |
|------|-----|--------------|----------|
| `botApi.enabled` | bool | `false` | Включает шлюз; при `false` маршруты не регистрируются. |
| `botApi.dataDir` | string | `./bot-sessions` | Каталог сессий ботов (`{botId}.bin`: auth_key + состояние обновлений). |
| `botApi.host` | string | `149.154.167.50` | DC bot-подключений (при `secret` — адрес собственного MTProxy). |
| `botApi.port` | int | `443` | Порт bot-подключений. |
| `botApi.secret` | string | `""` | MTProxy-secret (17 байт hex с префиксом `dd/ee` + 16 байт секрета, либо 16 байт без префикса); пусто — прямое подключение к DC без обфускации. |
| `botApi.maxUploadBytes` | long | `20971520` (20 MiB) | Лимит суммарного размера multipart-вложений; превышение — `413`. |
| `botApi.maxFileCacheBytes` | long | `67108864` (64 MiB) | Суммарный лимит in-memory кэша скачанных файлов. |
| `botApi.requestsPerSecondPerToken` | int | `30` | Максимум запросов в секунду на бота (botId — число до `:` в токене); `0` — выключено. Превышение — `429`. |

## Безопасность

- **Токены в логах:** маскируются автоматически (формат `12345:***`); паттерн
  `\d{5,}:[A-Za-z0-9_-]{20,}` → `digits:***` (общий `SecretMaskingPolicy`).
- **Авторизация маршрутов:** `/bot*` доступны без Bearer-токена (как у оригинального
  Bot API). Bearer (`webApiToken`) защищает только `/api/*`.
- **Сессии ботов:** хранятся в `botApi.dataDir`; `botApi.log` не пишет секреты.
- **HTTPS:** для продакшена рекомендуется web proxy (HTTPS) или внешний reverse
  proxy с TLS.

## Метрики

Prometheus-счётчики (префикс `tgwsproxy_`, доступны на `/metrics`):

| Метрика | Описание |
|---------|----------|
| `tgwsproxy_botapi_requests_total` | Общее число запросов через шлюз. |
| `tgwsproxy_botapi_request_errors_total` | Число ошибок (4xx/5xx от шлюза, включая `413`). |

## Ограничения

- **Без webhooks**: callback-и Telegram доставляются напрямую на URL бота.
- **Без retry** (кроме прозрачного повтора `bad_server_salt` внутри MTProto).
- **Без rate-лимитов** со стороны прокси; лимиты контролирует Telegram.
- **CDN-файлы** в `getFile` пока не поддерживаются.
- **Методы вне тиров 1–4** возвращают `501`.

## Интеграция с TBotPlatform

TBotPlatform работает без изменений: указывается адрес прокси вместо `api.telegram.org`.

```csharp
var bot = new TelegramBotClient("TOKEN",
    new TelegramBotClientOptions("http://proxy-host:80"));
```

При необходимости `api_id`/`api_hash` передаются заголовками `X-Telegram-Api-Id`
и `X-Telegram-Api-Hash` (нужны только для первого логина).
