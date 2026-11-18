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
| `BotGateway.cs` + `BotGateway.*.cs` | bot API поверх MTProto: `BotGateway` — partial-класс, логика разнесена по файлам (см. ниже) |
| `BotSession.cs` / `BotSessionStore.cs` | bot-сессии (сериализация состояния и хранилище) |

**Bot HTTP-слой** (`MtProto/`, всё — один partial-класс `BotGateway`)

| Файл | Роль |
| --- | --- |
| `BotGateway.cs` | ядро: конструктор, поля, `HandleAsync` (диспетчер методов Bot API), `BotApiResult`/`BotGatewayException`/`BotUploadFile` |
| `BotGateway.Session.cs` | подключение и логин бота (`GetConnectionAsync`/`LoginAsync`), `BotConnection` |
| `BotGateway.Files.cs` | `getFile`, кэш файлов, LRU-вытеснение |
| `BotGateway.Updates.cs` | `getUpdates`/`getWebhook*`, очередь апдейтов (`UpdateQueue`), разбор `updates.difference` и «протолкнутых» контейнеров |
| `BotGateway.Mapping.cs` | мапперы TL → Bot API: `BuildMessage`/`BuildUpdate*`/`BuildChat*`/`BuildUser` |
| `BotGateway.Args.cs` | чтение аргументов запроса (`Read*Arg`), `file_id`, dispatch-таблицы методов |
| `BotGateway.Messages.cs` | отправка/редактирование/удаление сообщений и медиа, `sendMediaGroup` |
| `BotGateway.MessageOps.cs` | forward/copy/reply/pin/`sendChatAction` |
| `BotGateway.Peers.cs` | резолв `chat_id`/`@username` и кэш access_hash |
| `BotGateway.Chats.cs` | `getChat`/`getChatMember`/администрирование чата |
| `BotGateway.Commands.cs` | команды над участниками (ban/leave/…), ответы на callback/inline |
| `BotGateway.Keyboards.cs` | `reply_markup` (inline/reply-клавиатуры) |
| `BotGateway.RichText.cs` | `entities`/`caption_entities`, HTML → rich-блоки |
| `BotGateway.Markdown.cs` | локальный парсер значений `parse_mode` (Markdown / MarkdownV2) |
| `BotApiResult.cs` | DTO Bot API, вынесенные из `BotGateway.cs` (`BotApiResult`, `BotGatewayException`, `BotUploadFile`) |


**Сгенерированный TL-слой** (`MtProto/TL/`)

| Файл | namespace | Роль |
| --- | --- | --- |
| `TL/Schema.cs` | `…MtProto.TL` | полная schema layer 229 (1473 `[TLDef]`) |
| `TL/MtProtos/MTProto.cs` | `…MtProto.TL.MtProtos` | transport/auth-типы (`ResPQ`, `ServerDH…`, 56 `[TLDef]`) |
| `TL/Methods.cs` | `…MtProto.TL` | RPC-методы схемы (`IMethod<>`, 29 `[TLDef]`) |
| `TL/Methods.Core.cs` | `…MtProto.TL` | методы, вызываемые движком напрямую: PQ/DH-handshake, `invokeWithLayer`/`initConnection`, bot-bootstrap |
| `TL/Xtended.cs` | `…MtProto.TL` | engine-хелперы (`InputPeer*`, приведения) |

**Контракты wire-формата** (`MtProto/TL/Abstractions/`)

| Файл | Роль |
| --- | --- |
| `Abstractions/TlInterfaces.cs` | `IObject`, `IMethod<>`, `IPeerResolver` |
| `Abstractions/IPeerInfo.cs` | `IPeerInfo`: `ID`, `IsActive`, `MainUsername`, `ToInputPeer()` |
| `Abstractions/TlAttributes.cs` | `[TLDef]`, `[IfFlag]` |

**Обвязка типов и сериализация** (разнесённый `TL.cs`)

| Файл | Роль |
| --- | --- |
| `MTProto/TL/TlPrimitives.cs` | `TLVector`, `Int128`, `Int256`, `BoolValue` |
| `Extensions.cs` | пустая partial-оболочка для группировки файлов-расширений |
| `Extensions.Serialization.cs` | `BinaryReader`/`BinaryWriter`-расширения: read/write значений, строк, байтов, vector, dictionary, gzip |
| `MTProto/MtProtoEnvelope.cs` | envelope-типы tdesktop: `RSAPublicKey`, `RpcResult`, `_Message`, `MsgContainer`, `MsgCopy`, `GzipPacked` |
| `MTProto/TL/Layer.cs` | `Layer.Table` (ctor → Type), `Nullables`, `EmptyCtors` |

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
`Extensions.WriteTLNull`. `EmptyCtors` (`ctor → null`) — конструкторы, читающиеся как `null`.

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

**Ядро:** `getMe`, `getUpdates`, `sendMessage`, `sendRichMessage`.

`sendRichMessage` (Bot API 10.1) принимает объект `rich_message` с `html` **или** `markdown`
(плюс опциональные `rtl`, `disable_autolink`) и отправляет его через `messages.sendMessage`
с флагом `rich_message` (`inputRichMessageHTML` / `inputRichMessageMarkdown`). Так как у
rich-сообщения нет текстового тела, в ответе `text` пустой — читать контент нужно из
`rich_message` в самом сообщении.

`getMe` отдаёт базовые поля пользователя плюс ботовые флаги из `User.flags`/`flags2`:
`can_join_groups` (`!bot_nochats`), `can_read_all_group_messages` (`bot_chat_history`),
`supports_inline_queries` (`bot_inline_placeholder`), `can_connect_to_business`
(`bot_business`), `has_main_web_app` (`bot_has_main_app`).

**Тир 1 — медиа и файлы:** `sendPhoto`, `sendDocument`, `sendAudio`, `sendVideo`,
`sendAnimation`, `sendVoice`, `sendVideoNote`, `sendSticker`, `sendMediaGroup`,
`getFile` (+ реальный маршрут `/file/bot*`).

**Тир 2 — правка и пересылка:** `editMessageText`, `editMessageCaption`,
`editMessageMedia`, `deleteMessage`, `deleteMessages`, `forwardMessage`, `copyMessage`,
`answerCallbackQuery`.

`copyMessage` возвращает `{"message_id": N}`, как и описано в Bot API (не полный `Message`).

### Удаление сообщений

`deleteMessage` (`chat_id` + `message_id`) и `deleteMessages` (`chat_id` + `message_ids`,
до 100 штук) маршрутизируются по типу чата:

- канал/супергруппа (`-100…`) → `channels.deleteMessages` (для них `messages.deleteMessages`
  сообщения не удаляет);
- остальные чаты → `messages.deleteMessages` с флагом `revoke` (удаление у всех).

`chat_id` здесь нужен именно для выбора MTProto-метода, ссылка `peer` не используется.

### Что отдаёт `getChat`

Базовые поля (`id`, `type`, `title`, `username`) дополняются из full-info:
`description`, `invite_link`, `permissions` (права всех участников, инвертированные из
`chatBannedRights`), а для каналов ещё `is_forum`, `has_protected_content`,
`has_hidden_members`, `has_aggressive_anti_spam_enabled`, `slow_mode_delay`,
`linked_chat_id`. За это платим вторым запросом (`messages.getFullChat` /
`channels.getFullChannel`) на каждый вызов.

Намеренно не возвращаем: `photo` (у `ChatPhoto` нет `access_hash`/`file_reference`,
скачать нельзя), `pinned_message` (нужен отдельный `messages.getMessages`),
`has_visible_history` (MTProto-флаг описывает видимость для текущего аккаунта, а не
для новых участников).

**Тир 3 — чаты:** `getChat`, `getChatAdministrators`, `getChatMember`,
`getChatMemberCount`, `setChatTitle`, `setChatDescription`, `setChatPhoto`,
`leaveChat`, `banChatMember`.

**Тир 4 — служебные:** `sendChatAction`, `setMyCommands`, `getMyCommands`,
`answerInlineQuery`.

Неизвестный метод возвращает `{ok:false, error_code:501, description:"method '...' is not implemented"}`.

### Резолв `chat_id`

Bot API объявляет `chat_id` как `Integer | String`; поддерживаются оба варианта:

- число — пользователь (положительный id), базовый чат (отрицательный),
  канал/супергруппа (`-100…`);
- `@channelusername` (или без `@`) — резолвится через `contacts.resolveUsername`; боты
  видят только публичные username. Найденный `access_hash` попадает в кэш шлюза, поэтому
  последующие вызовы к этому чату в сеть уже не ходят. Строковый числовой `chat_id`
  (`"123456789"`) обрабатывается как число.

Несуществующий или недопустимый username возвращает `400 Bad Request: chat not found`.

### Разбор текста: `entities` и `parse_mode`

Если передан массив `entities` / `caption_entities`, он имеет приоритет и `parse_mode`
не применяется вовсе (текст уходит как есть). Иначе `parse_mode` разбирается локально
в `MessageEntity[]` (`{"type","offset","length",…}`), никакого серверного разбора у
Telegram не запрашивается:

- `HTML` — теги `b/strong`, `i/em`, `u`, `s/strike/del`, `tg-spoiler`,
  `span class="tg-spoiler"`, `code`, `pre` (с атрибутом `language`),
  `a href="…"` (в том числе `tg://user?id=N` → mention по id), `blockquote`;
- `Markdown` (legacy) — `*bold*`, `_italic_`, `` `code` ``, ```` ```pre``` ````,
  `[текст](url)`, `[текст](tg://user?id=N)`;
- `MarkdownV2` — то же плюс `__underline__`, `~strike~`, `||spoiler||`
  и строки-цитаты, начинающиеся с `>`.

Экранирование — обратный слэш (`\*`). Смещения считаются в UTF-16 code units, как и
у MTProto, поэтому не пересчитываются. Разбор намеренно мягкий: незакрытый маркер
остаётся обычным текстом (Bot API в таком случае отвечает `400`), а неизвестное
значение `parse_mode` — это `400 unknown parse_mode '...'`.

### Семантика getUpdates

Апдейты читаются из `updates.getDifference` (инкрементально, через сохранённые в сессии
`pts`/`qts`/`date`/`seq`), но `update_id` **не** берётся из `Message.id` — id сообщения
локален для чата и не монотонен между чатами. Вместо этого шлюз держит на каждого бота
очередь неотданных апдейтов (`UpdateQueue`) и присваивает `update_id` при постановке в
очередь, монотонно и глобально для бота. Счётчик `UpdateId` живёт в `BotSession` (версия 3),
поэтому нумерация продолжается после перезапуска процесса; файлы сессий версий 1–2
читаются как есть (`UpdateId = 0`), ре-логин не нужен.

- `offset` — подтверждение: всё, у чего `update_id < offset`, снимается с головы очереди
  и больше не отдаётся. Если `offset` забегает вперёд, счётчик поднимается до `offset - 1`.
- `limit` (1..100) ограничивает размер пачки; неотданный хвост остаётся в очереди и
  отдаётся следующим вызовом — потерять его нельзя, так как `pts` уже сдвинут.
- Очередь ограничена (10 000 записей на бота); при переполнении отбрасывается самый старый
  апдейт с предупреждением в лог.
- Long-poll (`timeout` 0..60) реализован опросом раз в 500 мс — вебхуки не поддерживаются.
  `setWebhook`, `deleteWebhook` и `getWebhookInfo` отвечают `400`
  `"webhooks are not supported; this gateway is long-poll only (use getUpdates)"` —
  это осознанное ограничение, а не недоделанный метод.
- `allowed_updates` фильтрует по типу апдейта (ключ объекта). Без параметра отдаются все
  типы. Поддерживаются `message`, `callback_query`, `inline_query`,
  `chosen_inline_result`. Учтите, что клиенты Bot API по умолчанию присылают
  `allowed_updates=["message"]`, поэтому `callback_query` не придёт, пока его явно не
  разрешить.
- `callback_query` / `inline_query` / `chosen_inline_result` собираются из `other_updates`
  того же `updates.difference`, поэтому `answerCallbackQuery` и `answerInlineQuery`
  достижимы. `chosen_inline_result` не содержит `inline_message_id`: обратного маппинга из
  MTProto `inputBotInlineMessageID` в непрозрачную строку Bot API нет, а редактирование
  inline-сообщений шлюз не поддерживает.

## Соответствие версии Bot API

Шлюз реализует **подмножество** Bot API, а не транслирует его целиком, и не объявляет
клиенту версию API (поля `api_version` нет ни в ответах, ни в `getMe`).
Сверка с апстримом проведена для **Bot API 10.4** (09.11.2026).

Практические следствия:

- **Метод вне тиров 1–4** → `501` (см. выше), а не тихая ошибка.
- **Новые опциональные поля** апстрима в ответах не появляются: `BuildMessage`/`BuildUser`/
  `BuildChatDto` собирают фиксированный набор полей. Клиент обязан трактовать отсутствующее
  поле как отсутствующее, а не как ошибку.
- **Bot API 10.4 не потребовал изменений**: все новинки аддитивны (новые методы + новые
  опциональные поля). Единственное ломающее изменение апстрима — переименование
  `StoryAreaPosition` → `AreaPosition` — проект не затрагивает, так как stories не поддерживаются.
- **Фичи 10.4, требующие нового MTProto-слоя** (widgets, TON wallet, chat share themes),
  в layer 229 отсутствуют как конструкторы, поэтому их поддержка — это перегенерация схемы
  на более свежий layer плюс реализация методов, а не вопрос совместимости.
- **Типы `MessageEntity` из 10.4**: `bank_card_number` поддержан в обе стороны (MTProto
  `messageEntityBankCard`); `ton_address` в layer 229 отсутствует — такие сущности
  отбрасываются при приёме и не отдаются в ответах.
- **Форматирование сущностями** поддержано в обе стороны: входящий `entities`
  (в `sendMessage`/`sendRichMessage`/`editMessageText`…) транслируется в `MessageEntity[]`,
  а ответы несут `entities` (для медиа — `caption_entities`) при наличии `has_entities`.
  Смещения передаются как есть: и MTProto, и Bot API считают их в UTF-16 code units.
  Сущности без аналога в Bot API (`formatted_date`, `diff_*`) при выводе пропускаются.
- **Состав объекта `Message`** ограничен теми полями, которые шлюз умеет собрать локально:
  `message_id`, `date`, `text`/`caption`, `from`, `chat`, `entities`/`caption_entities`,
  `photo`, `document`, `reply_to_message`, `quote`, `message_thread_id`, `is_topic_message`,
  `forward_origin`, `edit_date`, `views`. Остальное (например, `reply_to_message` с полным
  телом цитируемого сообщения) не запрашивается отдельно через `messages.getMessages` —
  чтобы не тратить лишние запросы на каждый апдейт.
- **Входящие параметры reply** читаются из `reply_to_message_id`, `message_thread_id` и
  объекта `reply_parameters` (`message_id`, `quote`, `quote_entities`, `quote_position` —
  последний маппится в `quote_offset`). `allow_sending_without_reply` в MTProto аналога не
  имеет и молча игнорируется.

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

### `file_id` фото и размеры

У фото `file_id` дополнительно кодирует **тип размера** (`PhotoSize.type`), потому что
для `upload.getFile` этот параметр обязателен: с пустым `thumb_size` Telegram отвечает
`FILE_REFERENCE_EXPIRED`. Поэтому:

- массив `photo` в объекте `Message` содержит **все доступные размеры** (`s`, `m`, `x`, …),
  у каждого свой `file_id`, `width`, `height` и `file_size`; `file_unique_id` общий
  (зависит только от `id` фото);
- `getFile` по такому `file_id` возвращает ровно тот размер, который запросили;
- `file_id` старого формата (без типа размера, выданный сборками до этого изменения)
  продолжает приниматься: тип размера подставляется как `x` (самый крупный), так что
  скачивание работает, но конкретный размер не гарантируется — перевыпуск `file_id`
  из свежего апдейта вернёт точный размер.

`file_reference` внутри `file_id` — снимок на момент отправки/получения; Telegram
периодически его ротирует, и по истечении срока `getFile` вернёт `FILE_REFERENCE_EXPIRED`
(в этом случае `file_id` нужно получить заново из апдейта или повторной отправки).

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
| `botApi.requestsPerSecondPerToken` | int | `30` | Максимум запросов в секунду на бота (botId — число до `:` в токене); `0` — выключено. Превышение — `429` с `parameters.retry_after`. |

### Ответ `429 Too Many Requests`

Как в официальном Bot API, ответ на превышение лимита содержит паузу в теле — клиенты
(включая TBotPlatform, см. `docs/prompts/REQUIREMENTS-BOT-API.md`) читают именно
`parameters.retry_after`, а не только HTTP-статус:

```json
{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 1","parameters":{"retry_after":1}}
```

Тот же формат отдаётся, когда флуд-лимит приходит от Telegram (`FLOOD_WAIT_X`, RPC-код `420`):
секунды берутся из имени ошибки, код `420` клиенту не показывается. Дополнительно
выставляется HTTP-заголовок `Retry-After` с тем же числом секунд.

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
- **Rate-лимиты:** собственный ограничитель `botApi.requestsPerSecondPerToken` плюс
  флуд-лимиты Telegram; и то, и другое отдаётся клиенту как `429` с `parameters.retry_after`.
- **Без `migrate_to_chat_id`:** при превращении группы в супергруппу ошибка приходит
  без параметров миграции.
- **CDN-файлы** в `getFile` пока не поддерживаются.
- **Методы вне тиров 1–4** возвращают `501`.
