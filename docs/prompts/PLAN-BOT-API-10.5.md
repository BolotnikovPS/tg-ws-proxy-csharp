# PLAN: tg-ws-proxy-csharp → Bot API 10.5 (изменения проекта)

> Источник фактов об апстриме: официальные страницы Telegram Bot API — запись
> «November 22, 2026 — Bot API 10.5» в https://core.telegram.org/bots/api-changelog и в
> https://core.telegram.org/bots/api (проверено 2026-11-23); итоговая сверка зафиксирована
> в `docs/bot-api.md`, раздел «Соответствие версии Bot API». Далее «вердикт» — для каждой
> позиции дельты 10.5 vs 10.4.
> Это ПЛАН: код не меняется, `docs/bot-api.md` не правится (отдельная задача). Доставляемый
> артефакт — этот файл.
>
> Сверка с кодом выполнена по `src/TgWsProxy.Infrastructure/MtProto/` (диспетчер
> `BotGateway.cs`, `BotGateway.Session.cs`, `BotGateway.Mapping.cs`, `BotGateway.Updates.cs`,
> генерированная схема layer 229 `TL/Schema.cs`, `TL/Methods.cs`) и по тестам
> `tests/TgWsProxy.Test/` (`BotGatewayChatInfoTests.cs`, `BotGatewayTests.cs`).

## 0. Резюме

Дельта 10.5 маленькая. Обязательная доработка кода — **одна**: `getMe` должен возвращать
объект `BotFullInfo`, а не `User` (у `User` 10 бот-полей удалены). Ещё два пункта — сугубо
документационные (обновить сверку 10.4 → 10.5 и зафиксировать `setMySettings` как новый
501-метод). Всё остальное аддитивно и фиксированными мапперами не затрагивается.

| # | Изменение 10.5 | Классификация (задание) | Вердикт | Оценка |
|---|---|---|---|---|
| 1 | `getMe` → `BotFullInfo` (12 required boolean + 2 optional) | (d) ломающее изменение возвращаемого типа поддерживаемого метода | **implement** | 2–3 ч + тесты |
| 2 | Новый метод `setMySettings` | (b) новый метод вне тиров 1–4 | **ignore** (уже 501; зафиксировать в доке и в плане как опцию) | 0 (док 0,5 ч) |
| 3 | `User`: удалено 10 бот-полей | (a)/(c) — поля существуют только в `getMe` | **ignore** (код не затрагивается: эти поля добавлял только `AddBotSelfFields`, который переезжает в `BotFullInfo`) | часть п.1 |
| 4 | `BotFullInfo.active_usernames` / `inline_query_placeholder` (opt) | (a) аддитивные опциональные поля | **implement** (по желанию, дёшево; иначе — document) | +0,5–1 ч |
| 5 | `WriteAccessAllowed.inviter` | (a) аддитивное поле; сервис-сообщение `write_access_allowed` шлюз не собирает | **ignore** | 0 |
| 6 | `RichBlockExpandableBlockQuotation`/`InputRichBlock…`: `text` → `blocks` | (c) переименование-замена поля | **ignore** (в `src/` нет «expandable», rich-конвертер такой блок не порождает) | 0 |
| 7 | `CallbackQuery.inline_message_id` семантика (`callback_query_mode`) | (a)/(d) поведенческое; по умолчанию `"default"` = старое | **ignore** (режимы `auto`/`inline` недостижимы: `setMySettings` → 501; `inline_message_id` шлюз всё равно не отдаёт) | 0 |
| 8 | `docs/bot-api.md` — сверка версии | — | **document** (следующая задача) | 0,5–1 ч |
| 9 | `docs/prompts/REQUIREMENTS-TELEGRAMBOT.md` — пакет 22.10.4 → новее | — | **document** (опция; вне скоупа этой задачи, отметить в доке) | 0,5 ч |

---

## 1. Обязательная доработка: `getMe` возвращает `BotFullInfo` (verdict: implement)

### 1.1 Текущее состояние (проверено по коду)

- `GetMeAsync` (`BotGateway.Session.cs:10-22`): `users.getUsers([InputUserSelf])` → `BuildUser(user)` + `AddBotSelfFields(me, user)`.
- `BuildUser` (`BotGateway.Session.cs:101-130`): отдаёт `id`, `is_bot`, `first_name`, `last_name`,
  `username`, `language_code` — это ровно база, которая остаётся в `User` и в 10.5.
- `AddBotSelfFields` (`BotGateway.Session.cs:143-152`, `internal static`): добавляет только 5
  флагов (`can_join_groups`, `can_read_all_group_messages`, `supports_inline_queries`,
  `can_connect_to_business`, `has_main_web_app`).
- Диспетчер: `"getMe" => GetMeAsync(...)` (`BotGateway.cs:123`).
- Тесты, которые придётся править: `BotGatewayChatInfoTests.AddBotSelfFields_*` (используют
  5 флагов и ровно их и проверяют); `BotGatewayTests.BuildUser_MapsBotApiFields` — не трогаем
  (это база `User`, она остаётся).
- Документация `docs/bot-api.md:203-206`: описание getMe («базовые поля + ботовые флаги») —
  поправить при документационной задаче.

### 1.2 Маппинг полей BotFullInfo на MTProto layer 229 (факты по `TL/Schema.cs`)

Поля `BotFullInfo` (17) и наличие источника в `User.flags`/`flags2`/полях:

| Поле BotFullInfo | Источник в layer 229 | Статус |
|---|---|---|
| `id`, `is_bot` (=true), `first_name`, `username` | `User.id` / `User.Flags.bot` / `first_name` / `username` | есть (уже в `BuildUser`) |
| `can_join_groups` | `!Flags.bot_nochats` (инверсия) | есть (уже в `AddBotSelfFields`) |
| `can_read_all_group_messages` | `Flags.bot_chat_history` | есть (уже) |
| `supports_inline_queries` | `Flags.has_bot_inline_placeholder` | есть (уже) |
| `can_connect_to_business` | `Flags2.bot_business` | есть (уже) |
| `has_main_web_app` | `Flags2.bot_has_main_app` | есть (уже) |
| `has_topics_enabled` | `Flags2.bot_forum_view` | есть в layer 229 (см. ниже) |
| `allows_users_to_create_topics` | `Flags2.bot_forum_can_manage_topics` | есть в layer 229 (см. ниже) |
| `can_manage_bots` | `Flags2.bot_can_manage_bots` (0x40000) | есть в layer 229 (см. ниже) |
| `supports_guest_queries` | `Flags2.bot_guestchat` (0x80000) — **наиболее близкий**; семантика в layer 229 описана как «guest-режим в чатах», т.е. соответствует | есть в layer 229, маппинг правдоподобный |
| `needs_inline_query_location` | `Flags.bot_inline_geo` (0x200000) — прямое описание «bot can request our geolocation in inline mode» | есть в layer 229, прямое соответствие |
| `inline_query_placeholder` (opt) | `User.bot_inline_placeholder` (поле, флаг `has_bot_inline_placeholder`) | есть в layer 229 |
| `active_usernames` (opt) | `User.usernames` (поле, флаг2 `has_usernames`), фильтр `Username.Flags.active` + `username` как первый активный | есть в layer 229; **проверить** факт заполнения для self-бота при `users.getUsers` |
| `supports_join_request_queries` | **нет флага** в layer 229 (`User` не содержит ничего про join-request квоты) | **нет источника** → отдавать `false` |
| `has_topic_sidebar` | **нет флага** в layer 229 (ни «sidebar», ни «bot_has_topics» в `User` не найдено) | **нет источника** → отдавать `false` |

Точные строки схемы (layer 229, `TL/Schema.cs`):

- `Flags2.bot_forum_view = 0x10000` — «this bot supports bot forum topics»;
- `Flags2.bot_forum_can_manage_topics = 0x20000` — «...and users are allowed to create and manage bot forum topics»;
- `Flags2.bot_can_manage_bots = 0x40000` — «manager bot that can create and manage managed bots»;
- `Flags2.bot_guestchat = 0x80000` — «this bot can be invoked as a guest in chats»;
- `Flags.bot_inline_geo = 0x200000` — «whether the bot can request our geolocation in inline mode»;
- `Flags2.has_usernames = 0x1` (+ поле `usernames`, `Username.Flags.active` — см. `TL/Xtended.cs:183`: `MainUsername` уже выбирает `usernames.FirstOrDefault(active)`).

Таким образом для 12 обязательных boolean: **10 имеют прямой источник, 2 — нет
(`supports_join_request_queries`, `has_topic_sidebar`) и отдаются константой `false`**
(для `BotFullInfo` они обязательные, их нужно эмитить, а не опускать).

### 1.3 Рекомендация по дизайну

1. Переименовать/переписать `AddBotSelfFields` → `BuildBotFullInfo(Dictionary<string,object>, User)`
   (или `AddBotFullInfoFields`), оставив имя метода статическим, чтобы тесты пережили правку с минимумом шума;
   либо ввести отдельный `BuildBotFullInfo(User)`. Вариант на усмотрение исполнителя, но:
   - **обязательные boolean отдаются всегда** (для `BotFullInfo` они required, а не опциональные);
   - **опциональные** (`active_usernames`, `inline_query_placeholder`) — только при наличии данных;
   - **`is_bot` = true** (в `BotFullInfo` тип `True`).
2. `GetMeAsync` собирает ответ через `BuildUser`-базу + новый метод: база `User` остаётся как есть,
   потому что `BuildUser` используется и для `from`/`chat` в апдейтах, где бот-поля не нужны.
   Отдельный метод для `getMe`-ответа исключает риск «протёкших» бот-полей в другие места.
3. `User.Flags2.bot_manage` не существует — использовать `bot_can_manage_bots` (см. 1.2).
4. Проверить на живом self-боте (или в интеграционном тесте с фейковым ответом `users.getUsers`):
   - заполняется ли `User.usernames` для бота (тогда `active_usernames` можно отдавать по-настоящему);
   - заполняется ли `bot_inline_placeholder`, когда включён inline mode (это уже используется флагом
     `has_bot_inline_placeholder`, так что поле, скорее всего, есть — проверить).
   Если данные не приходят — поля опускать (optional), это не ломает контракт.

### 1.4 Объём работ (предварительная оценка)

| Шаг | Что | Оценка |
|---|---|---|
| 1 | Переписать `AddBotSelfFields` → полноценный маппинг BotFullInfo (12 boolean + 2 optional) в `BotGateway.Session.cs` | 1–1,5 ч |
| 2 | Обновить `BotGatewayChatInfoTests.cs` (добавить флаги `bot_inline_geo`, `bot_forum_view`, `bot_forum_can_manage_topics`, `bot_can_manage_bots`, `bot_guestchat`; ожидать 12 ключей; проверить `false` для `supports_join_request_queries`/`has_topic_sidebar`) + новый тест на optional-поля (`usernames`, `bot_inline_placeholder`) и на отсутствие ключей при пустых данных | 1–1,5 ч |
| 3 | Прогнать `dotnet test` (весь `TgWsProxy.Test`); затронутые: `BotGatewayChatInfoTests`, остальные не должны сломаться | 0,5 ч |
| | **Итого implement** | **2–3,5 ч** |

---

## 2. Новый метод `setMySettings` (verdict: ignore — остаётся 501)

- Новый метод в диспетчер не добавляем: он **вне тиров 1–4**, по контракту шлюза
  («неизвестный метод → 501», `BotGateway.cs:158`) уже возвращает
  `501 "method 'setMySettings' is not implemented"`. Кода не требуется.
- Зафиксировать в `docs/bot-api.md` (задача «документация»): упомянуть `setMySettings` в
  перечне известных-но-нереализованных методов (как `setWebhook`* и пр.), чтобы 501 не выглядел
  случайным.
- **Опция (не в скоупе 10.5-совместимости):** если продукту нужен паритет с @BotFather-настройками —
  отдельная фича: 15 параметров надо разложить на MTProto (в layer 229 нет одного
  «setMySettings»; настройки живут в `User`-флагах и в отдельных RPC: `bots.setBotInfo`,
  `bots.toggleUsername`, `account.`-методы, `help.getConfig` и т.п.) — это исследование +
  реализация, **отдельная задача**, не блокер для 10.5.
- Оценка: 0 (если не делаем опцию); документация 0,5 ч.

---

## 3. Аддитивные/поведенческие изменения, не требующие кода

| Изменение | Почему ignore | Проверено по коду |
|---|---|---|
| `User` − 10 бот-полей | Эти поля добавлял только `AddBotSelfFields` (в `getMe`). В `from`/`chat` апдейтов `BuildUser` их никогда не эмитил. После п.1 они живут только в `BotFullInfo`. | grep по `src/`: поля встречаются только в `BotGateway.Session.cs` + тесты |
| `WriteAccessAllowed.inviter` | Сервис-сообщение `messageActionBotAllowed` (TL-аналог) в `Message` не собирается; «состав `Message` ограничен» (`docs/bot-api.md`). Нечему добавлять поле. | `TL/Schema.cs:2843-2866` (нет `inviter` в конструкторе layer 229) |
| `RichBlock…Quotation: text → blocks` | Шлюз не генерирует и не разбирает expandable blockquote: rich-HTML конвертер (`BotGateway.RichText.cs`) таких блоков не порождает, «expandable» в `src/` не встречается (grep: 0 совпадений). | grep `expandable` → нет совпадений |
| `CallbackQuery.inline_message_id` (режимы `auto`/`inline`) | `setMySettings` не реализован → режим всегда `"default"` (старое поведение). Плюс `inline_message_id` шлюз сознательно не отдаёт (нет обратного маппинга `InputBotInlineMessageID` → Bot API-строка, `BotGateway.Updates.cs:416-418`; `BuildCallbackQuery` строит только stub-`message`, `BotGateway.Updates.cs:343-385`). | `BotGateway.Updates.cs` |

---

## 4. Документация (verdict: document; следующая задача, `docs/bot-api.md`)

Точки правки (`docs/bot-api.md`):

1. **«Соответствие версии Bot API»** (строки 316-321): `Bot API 10.4 (09.11.2026)` →
   `Bot API 10.5 (22.11.2026)`. Заменить пункт «Bot API 10.4 не потребовал изменений» на
   пункт про 10.5: единственное ломающее изменение — `getMe` → `BotFullInfo` (реализовано/планируется),
   `setMySettings` — новый 501-метод.
2. **«Поддерживаемые методы» → getMe** (строки 203-206): описать, что getMe возвращает
   `BotFullInfo` (набор полей), а не `User`; отметить поля, которых нет в layer 229.
3. **Список «что даёт неизвестный метод»** (строка 250 + раздел «Ограничения»): добавить
   `setMySettings` в примеры (рядом с `setWebhook`-семейством).
4. **Опция:** `docs/prompts/REQUIREMENTS-TELEGRAMBOT.md` (строки 10-14, 231, 617) ссылается на
   пакет `Telegram.Bot 22.10.4` «покрывает Bot API 10.4». Для 10.5-контента (setMySettings,
   BotFullInfo) клиенту понадобится пакет новее — обновить при желании (вне скоупа plan-задачи,
   но упомянуть в task-описании).

Оценка: 0,5–1 ч.

---

## 5. Порядок выполнения и зависимости

1. **implement (getMe/BotFullInfo)** — можно начинать сразу (независимо).
2. **документация bot-api.md** — после (1) или параллельно; фиксирует новую сверку 10.5 и
   факт реализации getMe. Это следующая задача в цепочке.
3. **setMySettings** — только если продукт решит делать опцию (отдельная задача, зависит от
   исследования MTProto-аналогов).

Открытые вопросы (для исполнителя implement-задачи):

- [ ] Заполняется ли `User.usernames` у self-бота в layer 229 (для `active_usernames`)?
- [ ] Отдавать ли `supports_guest_queries` с `bot_guestchat` (правдоподобно) или константу?
      Для плана принято: `bot_guestchat` → да; `supports_join_request_queries`,
      `has_topic_sidebar` → `false` (источника нет).

---

## 6. Сводная таблица вердиктов (для трекинга)

| Позиция | Вердикт | Оценка | Куда |
|---|---|---|---|
| getMe → BotFullInfo | implement | 2–3,5 ч | `BotGateway.Session.cs` + `BotGatewayChatInfoTests.cs` (+ новый тест) |
| setMySettings | ignore (501) + document | 0 (+0,5 ч док) | диспетчер не трогаем; `docs/bot-api.md` |
| Удаление 10 полей User | ignore | — | — |
| BotFullInfo active_usernames / inline_query_placeholder | implement (optional) | +0,5–1 ч | тот же маппер |
| WriteAccessAllowed.inviter | ignore | — | — |
| RichBlockQuotation text→blocks | ignore | — | — |
| CallbackQuery.inline_message_id | ignore | — | — |
| docs/bot-api.md сверка 10.4→10.5 | document | 0,5–1 ч | следующая задача |
| (опция) setMySettings реализация | вне скоупа | 1–2 дня | отдельная задача |