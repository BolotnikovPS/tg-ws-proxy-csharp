# Дизайн-документ: поддержка WEB proxy (browser-relay) в `tg-ws-proxy-csharp`

**Проект:** `tg-ws-proxy-csharp` (порт Flowseal/tg-ws-proxy на C#/.NET, `net11.0`)
**Статус документа:** черновик дизайна, редакция 1
**Область:** `TgWsProxy.Domain`, `TgWsProxy.Application`, `TgWsProxy.Infrastructure`, `TgWsProxy` (host), `config.json`, `README.md`
**Зависит от:** `docs/REQUIREMENTS-WEB-API.md` (действующий контракт web-only режима и мультитокенов)

> **Важное замечание о статусе фичи.** «WEB proxy» — свежая возможность Telegram Desktop, реализованная в
> ветке `dev` и ещё **не вышедшая в стабильный релиз** (см. § 1.2). Публичной серверной спецификации нет:
> контракт серверной стороны в этом документе **выведен из клиентского кода** tdesktop и помечен как
> «выведено/предположение» там, где нет гарантии стабильности. Реализация должна быть изолирована за
> отдельным интерфейсом, чтобы дрейф протокола не задевал существующий MTProto-прокси.

---

## 1. Цель и контекст

### 1.1. Что такое «WEB proxy» и чем он НЕ является

Telegram Desktop вводит третий тип прокси в `ProxyData::Type` — **`Web`**, наряду с уже знакомыми
`Mtproto` (секрет `dd`/`ee`) и `Socks5`. Суть — **подключение к прокси-серверу через браузер**:

```
Telegram Desktop ── локальный loopback WS ── браузер пользователя ──(iframe + postMessage + loopback WebRTC)──
        bridge-страница прокси-оператора ──(WebSocket, TLS 443)── WEB-прокси-сервер ── Telegram DC
```

Зачем это нужно: когда прямое TCP-соединение до прокси-сервера заблокировано/невозможно, клиент
маршрутизирует трафик через обычный браузер и HTTPS-сайт прокси-оператора. Для оператора это ещё один
канал доставки MTProto-трафика поверх уже существующей серверной логики.

**Важно не путать:**

| Термин | Что это | Статус в проекте |
|---|---|---|
| **WebSocket-транспорт MTProto** | `kws{dc}.web.telegram.org/apiws`, obfuscated2, секрет `dd` | ✅ реализовано (`WsPool`, `RawWebSocket`) |
| **Fake TLS** | секрет `ee<secret><hex(домен)>`, маскировка под HTTPS | ✅ реализовано (`FakeTls`, `MaskingProxy`) |
| **WEB proxy (browser-relay)** | прокси через браузер/iframe/WebRTC, HMAC bridge-capability | ❌ предмет этого документа |

Префикса `ww` в коде tdesktop **не существует** — это популярное заблуждение, связанное с
«Web/Ws». WEB proxy идентифицируется не префиксом секрета, а отдельным маркером ссылки `0x70`
и HMAC-подписью `bridge` (§ 2.4).

### 1.2. Статус фичи в Telegram Desktop (ветка `dev`)

История коммитов (GitHub API, репозиторий tdesktop):

| Коммит | Сообщение | Дата |
|---|---|---|
| `979e116a23` | «Implement PoC webview-based WEB transport» | 11.08.2026 |
| `2cf236e8c4` | «Prepare WEB proxy for release» | 18.08.2026 |
| `e3688099a1` | «Support WEB proxies with /path» | 09.09.2026 |

Файлы клиента (источник контракта):

- `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_transport.{h,cpp}` — транспорт, loopback-сервер, состояния;
- `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_frame.{h,cpp}` — relay-frame протокол;
- `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_webview.{h,cpp}` — bridge-страница в iframe + WebRTC;
- `Telegram/SourceFiles/mtproto/mtproto_proxy_data.cpp` — секрет, ссылка, bridge capability.

### 1.3. Целевое состояние (что мы хотим получить)

Оператор, у которого уже развёрнут `tg-ws-proxy-csharp`, включает **опциональный** WEB-мост:

1. Прокси дополнительно отдаёт HTTPS-сайт (bridge-страница + JS) и WebSocket-эндпоинт релея.
2. Telegram Desktop в режиме WEB proxy подключается к этому сайту; трафик релеится в Telegram DC
   той же логикой, что и обычный TCP/WS MTProxy-режим.
3. Существующий режим (`Port`, `tokens[]`, WS-pool, fallback, Fake TLS) **не меняется**; web-proxy —
   отдельная возможность, выключенная по умолчанию.

### 1.4. Границы (что НЕ входит в объём)

- WebRTC на сервере — **не нужен**: WebRTC в tdesktop используется только между браузером и
  локальным loopback-сервером самого клиента; до сервера идёт обычный WebSocket.
- HTTP/3 / QUIC для bridge — вне объёма (TLS 1.2/1.3 поверх TCP).
- Эмуляция клиентской стороны (локальный QTcpServer, iframe, WebRTC) — это код tdesktop, не наш.
- Многопользовательская авторизация bridge — bridge-канал авторизуется секретом прокси (§ 2.4),
  это не отдельная учётная система.
- Горячая смена TLS-сертификата и привязки bridge — «только старт» (см. § 7).

---

## 2. Контракт WEB proxy (выведен из клиентского кода tdesktop)

> Раздел описывает **то, что должен реализовать сервер**, чтобы с ним работал клиент tdesktop.
> Пункты, где нет публичной серверной спецификации, помечены **[выведено]**.

### 2.1. Relay-frame протокол

Единица обмена между браузером (bridge-JS) и сервером — **relay-frame** поверх одного WebSocket-сообщения.

Заголовок — 8 байт, все поля big-endian:

```
byte 0            : тип кадра (FrameType)
bytes 1..3        : streamId (24-битное число) — идентификатор мультиплексированного потока
bytes 4..7        : длина payload (32-битное, беззнаковое)
payload           : length байт
```

Константы (`web_proxy_frame.h`):

| Константа | Значение |
|---|---|
| `kFrameHeaderSize` | `8` |
| `kMaxFramePayload` | `1024 * 1024` = 1 МиБ |
| `kMaxBatchFrames` | `4096` |
| `kInitialStreamWindow` | `4 * 1024 * 1024` = 4 МиБ |

Типы кадров (`FrameType`):

| Значение | Имя | Назначение |
|---:|---|---|
| `0x01` | `Open` | открыть новый поток с указанным `streamId` |
| `0x02` | `Data` | данные потока (payload = байты потока) |
| `0x03` | `Close` | закрыть поток |
| `0x04` | `Window` | flow-control: вернуть «кредит» приёмного окна потоку |
| `0x05` | `Ping` | keepalive-запрос |
| `0x06` | `Pong` | keepalive-ответ |
| `0x10` | `Hello` | служебное приветствие |
| `0x11` | `Welcome` | подтверждение приветствия |
| `0x12` | `AuthChallenge` | служебный запрос авторизации |
| `0x13` | `AuthResponse` | ответ авторизации |
| `0x1F` | `Bye` | отказ / завершение соединения |

Правила мультиплексирования **[выведено]**:

- Один WebSocket несёт несколько независимых потоков, различаемых по `streamId`.
- `Open` открывает поток; далее его байты идут в `Data`-кадрах; `Close` закрывает.
- Flow-control: каждая сторона объявляет приёмное окно (начальный кредит 4 МиБ) и отправляет
  `Window`, когда потребила данные, разрешая собеседнику слать дальше. Отправка сверх кредита —
  нарушение протокола, повод закрыть поток/соединение.
- `Data`-payload потока — **обфусцированный MTProto** (тот самый байтовый поток, что проект уже
  обрабатывает в TCP-режиме: 64-байтный obfs2 init + последующие пакеты).

### 2.2. Секрет и ссылка подключения

Канонический секрет прокси — **16 байт** (в проекте `ProxyToken.Secret` — 32 hex). Формы в tdesktop:

| Форма | Длина | Значение | В WEB proxy |
|---|---|---|---|
| каноническая | 16 байт | секрет | ✅ |
| с `0xDD` | 17 байт | padded intermediate | через префикс протокола |
| с `0xEE` | 21+ байт | fake TLS | ❌ `Unsupported` для WEB proxy |

Маркер ссылки WEB proxy (`mtproto_proxy_data.cpp`):

- `kWebProxyLinkSecretMarker = uchar(0x70)`;
- при наличии `basePath` ссылка строится как `base64url(0x70 + secret)` **без** `=`-паддинга;
- секрет `0xEE` (fake TLS) для WEB proxy отвергается клиентом.

### 2.3. Bridge capability (авторизация канала)

Сервер аутентифицирует bridge-соединение по параметру `?bridge=<capability>`:

```
capability = base64url( HMAC-SHA256( key = secret(16 байт),
                                     message = context ) )          // без '='-паддинга
```

Два контекста:

| Версия | Контекст (точные строки, `\n` — перевод строки) | Когда |
|---|---|---|
| `v1` | `tdesktop-web-proxy-bridge-v1\n<host>` | корневой путь (без `basePath`) |
| `v2` | `tdesktop-web-proxy-bridge-v2\n<host>\n<basePath>` | с `basePath` |

`<host>` — домен bridge-сайта **без пути**; `<basePath>` — нормализованный путь.

Bridge URL **[выведено]**: `https://host/basePath/?bridge=<capability>`.

- Порт **только 443** (в URL порт не передаётся).
- `basePath` нормализуется клиентом: без пустых сегментов, пробелов и точек, длина ≤ 128 символов.

### 2.4. Что обязан реализовать сервер (сводный контракт)

1. **HTTPS-статика** — отдать bridge-страницу и JS по `GET /` (или `GET /basePath/`).
2. **WS-эндпоинт релея** — принять WebSocket-апгрейд на `/transport` (или `/basePath/transport`),
   авторизовать по HMAC bridge-capability (§ 2.3).
3. **Мультиплексор relay-frame + flow-control** — разбирать/собирать кадры § 2.1, вести потоки.
4. **Терминация обфусцированного MTProto на поток** — каждый relay-поток — это MTProto-сессия:
   obfs2 init → выбор DC/proto → `relay_init` → перешифровка → маршрутизация в Telegram DC.
   Это **переиспользует** существующий стек `MtProto`/`WsPool`/`TcpBridgeService`/`CfWorkerPool`.

---

## 3. Текущая архитектура и точки переиспользования

### 3.1. Слои проекта

| Сборка | Роль |
|---|---|
| `TgWsProxy.Domain` | `Config`, `ProxyToken`, `MtProtoHandshake`, `SecretMask`, исключения |
| `TgWsProxy.Application` | порты (`IProxyServer`, `IWsPool`, `ITcpBridgeService`, `IProxyTokenRegistry`, …), `Protocol`, `StartConfig` |
| `TgWsProxy.Infrastructure` | реализации: `MtProto`, `Session`, `Instances`, `Wire`, `FakeTls`, `Hosting`, `Metrics` |
| `TgWsProxy` | host: Kestrel (панель), `ProxyHostedService`, Web API |
| `TgWsProxy.Test` / `TgWsProxy.Benchmarks` | тесты / бенчмарки |

### 3.2. Эталонный поток MTProto-сессии (сейчас)

`src\TgWsProxy.Infrastructure\Session\ClientSessionHandler.cs` — образец, который web-proxy должен повторить
для каждого relay-потока:

1. Прочитать init (`ClientInitReader` — разбирает PROXY protocol и Fake TLS до рукопожатия).
2. Подобрать токен: `SelectToken` перебирает `IProxyTokenRegistry.HandshakeOrder`,
   вызывая `IMtProtoInspector.TryHandshake(init, secret)` → `MtProtoHandshake(Dc, IsMedia, ProtoTag, ProtoInt)`.
3. `mtProtoInspector.GenerateRelayInit(protoTag, dcIdx)` — relay init для Telegram.
4. `BuildCryptoContext(init, relayInit, secret)` → `CryptoCtx` (4 AES-CTR потока: клиент туда/обратно + TG туда/обратно).
5. `new MsgSplitter(protoInt)` — резать поток на MTProto-пакеты.
6. `wsPool.TryGet(dc, isMedia, isTestDc)` → `IRawWebSocket`; при `null` — `FallbackRequest` →
   `ITcpBridgeService.DoFallback` (CF worker / H2 / TCP).
7. `ws.Send(relayInit)` + `bridgeService.BridgeWsReencrypt(...)` — перешифровка клиент↔Telegram.

### 3.3. Что переиспользуем без изменений

| Компонент | Путь | Роль в web-proxy |
|---|---|---|
| `Protocol` (константы) | `src\TgWsProxy.Application\Logic\Protocol.cs` | `HandshakeLen=64`, `ProtoTagPos=56`, `DcIdxPos=60`, proto-теги, `WsDomains`, пути/порты |
| `IMtProtoInspector` / `MtProtoInspector` | `src\TgWsProxy.Infrastructure\MtProto\` | `TryHandshake`, `GenerateRelayInit`, `IsHttpTransport` |
| `IncrementalCipher`, `CryptoCtx` | `src\TgWsProxy.Infrastructure\Wire\CryptoCtx.cs`, `IncrementalCipher.cs` | AES-CTR обфускация |
| `MsgSplitter` | `src\TgWsProxy.Infrastructure\Wire\MsgSplitter.cs` | разбиение потока на пакеты |
| `WsPool`, `RawWebSocket`, `RawWebSocketFactory` | `src\TgWsProxy.Infrastructure\Instances\` | исходящий WS к Telegram |
| `TcpBridgeService`, `CfWorkerPool`, `CfH2Pool`, `H2Bridge` | `src\TgWsProxy.Infrastructure\Instances\` | fallback-цепочка |
| `IProxyStats`, `IPeerStatsRegistry` | `src\TgWsProxy.Application\` | учёт трафика (добавить маршрут) |
| `IActiveSessionRegistry` | `src\TgWsProxy.Application\Abstractions\` | живые сессии / разрыв при отзыве токена |
| `Config` / `ProxyToken` | `src\TgWsProxy.Domain\` | секрет 16 байт, токены |

### 3.4. Что дорабатываем

| Точка | Путь | Изменение |
|---|---|---|
| `ClientSessionHandler` | `src\TgWsProxy.Infrastructure\Session\ClientSessionHandler.cs` | вынести ядро «после handshake» в переиспользуемый движок над `Stream` (сейчас жёстко завязан на `Socket`/`NetworkStream`) |
| `IProxyTokenRegistry` | `src\TgWsProxy.Application\Abstractions\IProxyTokenRegistry.cs` | добавить подбор токена по HMAC bridge-capability (аналог `SelectToken`) |
| `ServiceCollectionExtensions` | `src\TgWsProxy.Infrastructure\ServiceCollectionExtensions.cs` | зарегистрировать новые сервисы |
| `ConfigDto` / `Config` / `ConfigValidator` | `src\TgWsProxy.Application\StartConfig\`, `src\TgWsProxy.Domain\Config.cs` | новые ключи § 7 |
| `ProxyHostedService` | `src\TgWsProxy.Infrastructure\Hosting\ProxyHostedService.cs` | жизненный цикл bridge-листенера, readiness |

---

## 4. Целевая архитектура

### 4.1. Обзор

```
                         ┌──────────────────────────── WEB-прокси-сервер (этот проект) ───────────────────────────┐
браузер ── TLS 443 ──▶   │  [WebProxyBridgeServer: HTTPS/WS]                                                       │
  (bridge-JS)            │      │  WS-кадр (payload = relay-frame)                                                  │
                         │      ▼                                                                                    │
                         │  [RelayFrameCodec]  ──▶  [RelayMultiplexer]  ── поток streamId ──▶  [RelayStream : Stream] │
                         │                                              └── flow-control (Window, 4 МиБ)               │
                         │      ▼                                                                                    │
                         │  [MtProtoBridgeEngine]  (общий с TCP-режимом)                                              │
                         │      init → TryHandshake → relay_init → CryptoCtx → MsgSplitter                            │
                         │      → WsPool / fallback (CfWorkerPool / H2Bridge / TcpBridgeService)                      │
                         └───────────────────────────────────────────────┬───────────────────────────────────────────┘
                                                                          ▼
                                                                    Telegram DC (kws{dc}.web.telegram.org /apiws)
```

### 4.2. Новые компоненты

| Компонент | Слой | Назначение |
|---|---|---|
| `IWebProxyBridgeServer` | Application | порт: запуск/остановка/состояние bridge-листенера |
| `WebProxyBridgeServer` | Infrastructure | HTTPS-листенер + WS-апгрейд + отдача статики (§ 4.4) |
| `RelayFrameCodec` | Infrastructure | парсинг/сериализация relay-frame (заголовок 8 байт, лимиты) |
| `RelayMultiplexer` | Infrastructure | демультиплекс по `streamId`, flow-control, keepalive |
| `RelayStream` | Infrastructure | `Stream`-адаптер над одним relay-потоком (`Read`=Data, `Write`=Data+Window) |
| `BridgeCapabilityVerifier` | Infrastructure | проверка `?bridge=<capability>` → `ProxyToken` |
| `MtProtoBridgeEngine` | Infrastructure | вынесенное ядро «после handshake» (общее для TCP и web-proxy) |

### 4.3. Ключевое решение: вынести `MtProtoBridgeEngine`

Сейчас `ClientSessionHandler.Handle` выполняет и рукопожатие, и перешифровку, работая напрямую с
`Socket`/`NetworkStream`. Web-proxy-поток — тот же obfs2 MTProto-поток, но транспортом является не
TCP-сокет, а мультиплексированный relay-канал (`RelayStream`).

**Решение:** выделить из `ClientSessionHandler` фазу «приняли `init` и выбрали токен → дальше мост»
в `MtProtoBridgeEngine.BridgeAsync(Stream clientStream, MtProtoHandshake hs, ProxyTokenEntry token, ...)`.
TCP-режим передаёт `NetworkStream`, web-proxy — `RelayStream`. Рукопожатие (`ClientInitReader`,
PROXY protocol, Fake TLS) остаётся спецификой TCP-режима и в движок не входит.

Обоснование: единственный способ не продублировать логику перешифровки (`CryptoCtx`,
`GenerateRelayInit`, `MsgSplitter`, выбор `WsPool`/fallback, учёт трафика) и не разойтись в поведении
двух режимов. Это рефакторинг без изменения поведения — покрывается существующими тестами
(`tests\TgWsProxy.Test\TcpBridgeCryptoTests.cs` и др.).

### 4.4. Решение: транспорт bridge — Kestrel (второй endpoint) vs hand-rolled TLS

Проект уже использует ASP.NET Core/Kestrel для панели (`MetricsHost`/`MetricsPort`), а данные к
Telegram гонит через hand-rolled `RawWebSocket`/`H2PacketCodec`.

| Критерий | **Kestrel (второй endpoint)** — рекомендуется | Hand-rolled `SslStream` + HTTP/WS |
|---|---|---|
| TLS + HTTP + WS-апгрейд | берёт на себя фреймворк | свой код (TLS, разбор заголовков, upgrade) |
| Объём нового рискованного кода | мал (только relay-frame + MTProto) | велик |
| Производительность | `System.Net.WebSockets.WebSocket`; замеряется | потенциально выше, но не доказано |
| Привязка | «только старт» — совпадает с § 8.1 действующего контракта | своя, отдельная от Kestrel |

**Рекомендация:** Kestrel-листенер `webProxyHost:webProxyPort` с `UseHttps(сертификат)` +
`UseWebSockets()`; маршруты: `GET /` → статика, `GET /transport` → relay (с поддержкой `basePath`).
Данные поверх `WebSocket` подаются в `RelayFrameCodec` → `RelayMultiplexer` → `RelayStream` →
`MtProtoBridgeEngine`. Если бенчмарки (`TgWsProxy.Benchmarks`) покажут, что `System.Net.WebSockets`
— узкое место, перейти на `SslStream`-реализацию за тем же `IWebProxyBridgeServer` (решение отложено).

### 4.5. Жизненный цикл relay-потока

```
Open(streamId) ─▶ создать RelayStream, буфер приёма (кредит 4 МиБ)
   └─ Data(streamId, payload) ─▶ подать в MtProtoBridgeEngine (init → … → Telegram)
        └─ ответ Telegram ─▶ Data(streamId, ...) обратно + Window по мере чтения
Close(streamId) / EOF ─▶ закрыть поток, освободить кредит, разорвать MTProto-сессию
Ping/Pong ─▶ keepalive (браузер держит вкладку)
Bye(0x1F) ─▶ отказ (неверный bridge HMAC / превышение лимитов)
```

---

## 5. Функциональные требования

Приоритеты: **Must** / **Should** / **Could** (как в `docs/REQUIREMENTS-WEB-API.md`).
Идентификаторы с префиксом `F-W` во избежание коллизии с существующими `F-1…F-9`.

### 5.1. Bridge-листенер и статика

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-W.1** | Bridge — отдельный HTTPS-листенер на `webProxyHost:webProxyPort` (по умолчанию порт `443`). Включается ключом `webProxyEnabled` (умолчание `false`). При выключении не занимает порт и не регистрирует маршруты. | Must |
| **F-W.2** | `GET /` (или `GET /{basePath}/`) отдаёт bridge-страницу и JS из локальных ресурсов (без внешних CDN). Содержимое — встроенный статический ресурс проекта. | Must |
| **F-W.3** | `GET /transport` (или `GET /{basePath}/transport`) выполняет WebSocket-апгрейд и становится каналом relay-frame. | Must |
| **F-W.4** | `basePath` поддерживается (`v2`-контекст HMAC и путь маршрутов); нормализация — без пустых сегментов/пробелов/точек, ≤ 128 символов (иначе `400`/отказ). | Should |
| **F-W.5** | Bridge-порт не должен совпадать с `Port` (MTProxy) и `MetricsPort` (панель): `ConfigValidator` отклоняет конфликт. | Must |

### 5.2. Авторизация bridge-канала

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-W.6** | Соединение принимается только с валидным `?bridge=<capability>`. `capability` = `base64url(HMAC-SHA256(context, secret))` без паддинга; контекст `v1` (`tdesktop-web-proxy-bridge-v1\n<host>`) или `v2` (`tdesktop-web-proxy-bridge-v2\n<host>\n<basePath>`). | Must |
| **F-W.7** | Подбор токена по HMAC: перебор включённых токенов (`IProxyTokenRegistry.HandshakeOrder`); совпавший токен фиксирует пару (IP, токен) для учёта и `Touch()`. | Must |
| **F-W.8** | Несовпадение — кадр `Bye(0x1F)` (или WS close) без выделения потока; попытка учитывается в метриках. | Must |
| **F-W.9** | Сравнение HMAC — постоянного времени (`CryptographicOperations.FixedTimeEquals`), без тайминг-канала по секрету. | Must |

### 5.3. Relay-frame и мультиплексирование

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-W.10** | `RelayFrameCodec` разбирает 8-байтный заголовок `[type][streamId:3 BE][length:4 BE]` + payload; сериализует обратно. | Must |
| **F-W.11** | `payload > kMaxFramePayload (1 МиБ)` или неполный/повреждённый заголовок — закрытие соединения/потока, событие в журнал. | Must |
| **F-W.12** | `RelayMultiplexer` ведёт независимые потоки по `streamId`; `Open` создаёт, `Data` доставляет, `Close` закрывает. | Must |
| **F-W.13** | Ограничение числа одновременных потоков на соединение и суммарного буфера (защита от исчерпания памяти). | Must |
| **F-W.14** | Flow-control: приёмное окно на поток 4 МиБ (`kInitialStreamWindow`); `Window` возвращает кредит; отправка сверх кредита — нарушение, поток закрывается. | Should |
| **F-W.15** | `Ping`/`Pong` — keepalive; `Bye` — корректный отказ с закрытием. | Must |

### 5.4. MTProto-мост поверх потока

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-W.16** | Каждый relay-поток — обфусцированная MTProto-сессия: `init` (64 байта) → `IMtProtoInspector.TryHandshake` → `GenerateRelayInit` → `CryptoCtx` → `MsgSplitter` → `WsPool`/fallback → перешифровка. | Must |
| **F-W.17** | Логика моста общая с TCP-режимом через `MtProtoBridgeEngine` (без дублирования). | Must |
| **F-W.18** | Маршрутизация DC/media/тест-DC — как в `ClientSessionHandler` (`dcIdx = ±dc`, `ForceTestDc`, тест-DC `>= 10000`). | Must |
| **F-W.19** | Неверный init/secret потока — закрытие потока (`Close`), инкремент `connectionsBad`, без влияния на соседние потоки. | Must |

### 5.5. Учёт, сессии, наблюдаемость

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **F-W.20** | Байты web-proxy-потоков учитываются в `IProxyStats`/`IPeerStatsRegistry` по паре (IP, токен); маршрут `route = "web-proxy"` (рядом с `ws`/`fallback`). | Should |
| **F-W.21** | Живые web-proxy-потоки регистрируются в `IActiveSessionRegistry`; выключение/отзыв токена разрывает их (как F-8.6 существующего контракта). | Should |
| **F-W.22** | Readiness отражает состояние bridge-листенера; метрики web-proxy (соединения, потоки, байты, отказы HMAC) в `/metrics` и `/api/metrics`. | Should |
| **F-W.23** | При `webProxyEnabled` в стартовый журнал выводится bridge URL (`https://host/basePath/`); секрет не печатается (только `SecretMask`). | Could |

---

## 6. Нефункциональные требования

| ID | Требование | Приоритет |
|----|-----------|-----------|
| **N-W.1** | Только HTTPS (TLS 1.2/1.3), порт `443` в URL клиента; plain-HTTP bridge не поддерживается. | Must |
| **N-W.2** | Горячий путь (frame-кодек, перешифровка) — без лишних аллокаций: `ArrayPool<byte>`, как в `RawWebSocket`; бенчмарк в `TgWsProxy.Benchmarks`. | Must |
| **N-W.3** | DoS-защита: лимит payload (1 МиБ), лимит потоков/соединений, переиспользование `ConnectionLimiter`/`PeerRateLimiter`/ACL там, где применимо. | Must |
| **N-W.4** | Секреты и capability не логируются; `TreatWarningsAsErrors`/`Nullable enable` сохраняются (проектное соглашение `Directory.Build.props`). | Must |
| **N-W.5** | Существующий TCP/WS MTProxy-режим регрессий не получает (web-proxy выключен по умолчанию; полный прогон `TgWsProxy.Test`). | Must |
| **N-W.6** | Наблюдаемость: web-proxy-метрики в едином `prometheus-net` registry (согласовано с F-3.1 действующего контракта). | Should |

---

## 7. Конфигурация (новые ключи)

Добавляются в `ConfigDto`/`Config`/`ConfigValidator` и панель. Категории применения — как в
`docs/REQUIREMENTS-WEB-API.md` (§ 4): **горячо** / **слушатель** / **только старт**.

| Ключ | Тип | Умолчание | Применение | Описание |
|---|---|---|---|---|
| `webProxyEnabled` | bool | `false` | только старт | включить bridge-листенер |
| `webProxyHost` | string | `"0.0.0.0"` | только старт | адрес привязки bridge |
| `webProxyPort` | int | `443` | только старт | порт bridge (≠ `Port`, ≠ `MetricsPort`) |
| `webProxyBasePath` | string | `""` | только старт | путь сайта (`""` → корень, `v1`) |
| `webProxyPublicHost` | string | `""` | горячо | внешний домен для ссылки (если за reverse-proxy) |
| `webProxyCertPath` | string | `""` | только старт | PFX/сертификат для TLS (обязателен при `webProxyEnabled`) |
| `webProxyCertPassword` | string | `""` | только старт | пароль PFX (в GET API маскируется) |

Замечания:

- `webProxyCertPath` обязателен при `webProxyEnabled` — bridge без TLS не имеет смысла (N-W.1).
- Поддержка TLS-терминации **за** reverse-proxy (nginx отдаёт 443, приложение слушает внутренний
  порт) — отдельный вопрос § 9; на первой итерации реализуется прямая привязка 443 с сертификатом.

---

## 8. Безопасность

1. **Авторизация до выделения ресурсов** — HMAC проверяется на этапе апгрейда, до создания потоков/буферов.
2. **Постоянное время сравнения** — `CryptographicOperations.FixedTimeEquals` для HMAC (F-W.9).
3. **Секреты в журнале** — только `SecretMask.Mask` (как в `ProxyServer.LogLinks`); `capability` не логируется.
4. **Границы входа** — лимит payload, лимит потоков, rate-limit по IP (N-W.3); bridge-сайт публичен,
   поэтому DoS-устойчивость обязательна.
5. **Модель угроз** — bridge-канал переносит зашифрованный MTProto; перехват TLS невозможен при
   валидном сертификате. Секрет прокси раскрывается только тому, кто его знает (HMAC необратим).
6. **Изоляция режимов** — bridge и панель (`/api/*` с `webApiToken`) — разные маршруты и порты;
   авторизация панели не распространяется на `/transport`, авторизация bridge не открывает `/api/*`.

---

## 9. Риски и открытые вопросы

| # | Риск / вопрос | Влияние | Смягчение |
|---|---|---|---|
| 1 | **Фича в `dev`-ветке tdesktop**, контракт может измениться до релиза | протокол «поплывёт» | изолировать за `RelayFrameCodec`/`IWebProxyBridgeServer`; версионировать контекст (`v1`/`v2`) |
| 2 | **Нет публичной серверной спецификации** — контракт выведен из клиента | риск неверной интерпретации | интеграционные тесты против реального tdesktop dev; следить за коммитами |
| 3 | **Порт 443** конкурирует с `Port` (MTProxy) и инфраструктурой | конфликт привязки | `ConfigValidator` (F-W.5); reverse-proxy как альтернатива |
| 4 | TLS-терминация: в приложении vs за nginx | сложность развёртывания | первая итерация — в приложении; далее поддержать `webProxyPublicHost` за прокси |
| 5 | Роль `Hello`/`Welcome`/`AuthChallenge`/`AuthResponse` (0x10–0x13) не до конца ясна | лишняя/недостающая логика | выяснить по bridge-JS tdesktop; на первом шаге обрабатывать как служебные no-op/отказ |
| 6 | `System.Net.WebSockets` может не дать нужную производительность | просадка throughput | бенчмарк; отложенный план — `SslStream`-реализация (§ 4.4) |
| 7 | Рефакторинг `ClientSessionHandler` → `MtProtoBridgeEngine` | риск регрессии TCP-режима | полный прогон `TgWsProxy.Test` до/после; поведенческий рефакторинг без изменения контрактов |

**Открытые вопросы для подтверждения:**

- OQ-1: подтвердить точную семантику `Hello`/`Welcome`/`AuthChallenge`/`AuthResponse` по bridge-JS.
- OQ-2: нужно ли серверу что-то отдавать в `AuthResponse`, или авторизация исчерпывается `?bridge=`.
- OQ-3: поддерживать ли `basePath` в первой версии или начать с корневого пути (`v1`).
- OQ-4: приоритет — прямая привязка 443 с сертификатом или режим за reverse-proxy.

---

## 10. План приёмки

**Юнит-тесты** (`TgWsProxy.Test`, xUnit, в стиле существующих):

- `RelayFrameCodecTests` — roundtrip заголовка, границы `streamId`/`length`, отказ при `payload > 1 МиБ`,
  обрезка заголовка, порядок байт (BE).
- `BridgeCapabilityVerifierTests` — HMAC `v1`/`v2` (фиксированные векторы), `base64url` без паддинга,
  несовпадение, пустой/`0xEE` секрет.
- `RelayMultiplexerTests` — `Open`/`Data`/`Close`, изоляция потоков, flow-control `Window`,
  превышение кредита, лимит потоков.
- `RelayStreamTests` — склейка `Data` в поток, разбиение записи на кадры, генерация `Window`.

**Интеграционные тесты:**

- Эмуляция bridge-клиента: WS → `?bridge=<cap>` → `Open` → `Data(obfs2 init)` → получить
  `relay_init`/ответ → `Close`; маршрутизация в **тест-DC** (`ForceTestDc`).
- Отказ: неверный `bridge` → `Bye`/close, без потока; регресс `SelectToken` по `HandshakeOrder`.
- Регресс TCP-режима: полный существующий набор зелёный после рефакторинга `MtProtoBridgeEngine`.

**E2E (ручное):**

- Telegram Desktop (сборка `dev`, `979e116a23` и новее) → добавить WEB proxy с нашим bridge URL →
  соединение через браузер; проверить приём/отправку сообщений, media (dc_idx < 0).

**Критерий готовности:** все юнит/интеграционные тесты зелёные, TCP-режим без регрессий,
E2E на тест-DC проходит, метрики и readiness корректны, документация `README.md` дополнена.

---

## 11. Ссылки

- Telegram Desktop (ветка `dev`):
  - `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_transport.{h,cpp}`
  - `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_frame.{h,cpp}`
  - `Telegram/SourceFiles/mtproto/web_proxy/web_proxy_webview.{h,cpp}`
  - `Telegram/SourceFiles/mtproto/mtproto_proxy_data.cpp`
  - Коммиты: `979e116a23`, `2cf236e8c4`, `e3688099a1`
- Документация Telegram:
  - <https://core.telegram.org/mtproto/transports> (WebSocket-транспорт, обфускация)
  - <https://core.telegram.org/api/links#mtproxy-links> (секрет в ссылке)
- Проект:
  - `docs/REQUIREMENTS-WEB-API.md` — действующий контракт web-only/мультитокенов
  - `src\TgWsProxy.Application\Logic\Protocol.cs` — константы протокола
  - `src\TgWsProxy.Infrastructure\Session\ClientSessionHandler.cs` — эталонный поток сессии
  - `src\TgWsProxy.Infrastructure\MtProto\MtProtoInspector.cs` — obfs2 handshake / relay init
