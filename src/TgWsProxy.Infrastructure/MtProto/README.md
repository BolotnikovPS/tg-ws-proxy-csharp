# MtProto

Реализация MTProto-прокси: транспорт, криптография, handshake, RPC и «bridge» уже установленной
сессии. Слой типов (TL) сгенерирован (layer 229), реестр конструкторов собирается рефлексией.

## Состав

### Hand-written движок
| Файл | Роль |
| --- | --- |
| `MtProtoBridgeEngine.cs` | ядро «после handshake» (общее для TCP и web-proxy): init → relay → Telegram |
| `MtProtoClient.cs` | нативный MTProto-RPC: auth key, шифрование, вызов методов |
| `MtProtoCrypto.cs` | криптография: AES-IGE, RSA, DH, факторизация |
| `MtProtoTransport.cs` | transport-кадрирование |
| `MtProtoInspector.cs` / `IMtProtoInspector.cs` | obfs2-handshake / relay-init |
| `ClientInitReader.cs` / `ClientInitResult.cs` / `ClientInitStatus.cs` | разбор клиентского `init` |
| `BotGateway.cs` / `BotSession.cs` / `BotSessionStore.cs` | bot-сессии (bot API поверх MTProto) |

### Сгенерированный TL-слой
| Файл | namespace | Роль |
| --- | --- | --- |
| `TL.Schema.cs` | `…MtProto.TL` | полная schema layer 229 (1473 `[TLDef]`) |
| `TL.MTProto.cs` | `…MtProto` | MTProto transport/auth-типы (`ResPQ`, `ServerDH…`, 56 `[TLDef]`) |
| `TL.Methods.cs` | `…MtProto.TL.Methods` | RPC-методы (`IMethod<>`, 29 `[TLDef]`) |
| `TL.Xtended.cs` | `…MtProto.TL` | engine-хелперы поверх schema: `IPeerInfo`, `InputPeer*`, неявные приведения |

### Реестр типов (reflection)
| Файл | Роль |
| --- | --- |
| `TL.Table.cs` | `Layer.Table` (ctor → Type), `Nullables`, `EmptyCtors` — описание ниже |

### Обвязка типов (разнесённый `TL.cs`)
| Файл | Роль |
| --- | --- |
| `TlAttributes.cs` | `[TLDef]`, `[IfFlag]` |
| `TlInterfaces.cs` | `IObject`, `IMethod<>`, `IPeerResolver` |
| `TlExceptions.cs` | `RpcException` |
| `TlPrimitives.cs` | `TLVector`, `Int128`, `Int256`, `BoolValue` |
| `TlSerializer.cs` | `Serialization`: read/write TL-значений, строк, байтов, vector, dictionary, gzip |
| `MtProtoEnvelope.cs` | envelope-типы из tdesktop `mtproto.tl`: `RSAPublicKey`, `RpcResult`, `_Message`, `MsgContainer`, `MsgCopy`, `GzipPacked` |

## Реестр конструкторов

`Layer.Table` (`ctor → Type`) строится один раз рефлексией по сборке `TgWsProxy.Infrastructure`:

1. сканируются все типы с `[TLDef]`;
2. `IMethod<>`-реализации исключаются (это вызовы, а не данные);
3. ctor из `EmptyCtors` (null/empty/notModified/default) исключаются — такие конструкторы
   десериализуются в `null` (например `Null#56730bcc`, `emojiStatusEmpty#2de11aae`);
4. дубликат ctor → `InvalidOperationException` («Duplicate TL constructor»).

`Nullables` (`base-type → ctor`) — обратное направление: ctor empty/notModified-варианта базового
типа, используется `Serialization.WriteTLNull`.

`EmptyCtors` (`ctor → null`) — множество конструкторов, которые читаются как `null`.

Преимущество перед прежней ручной таблицей на ~1650 строк: реестр не может разойтись с
`[TLDef]`-атрибутами — расхождение либо отсутствует, либо падает сразу при первом обращении.

## Что изменилось при рефакторинге

- `TL.cs` (589 строк: `IObject` + `Serialization` + примитивы + envelope) разнесён на
  `TlAttributes/Interfaces/Exceptions/Primitives/Serializer` + `MtProtoEnvelope`.
- `TlBinary.cs` — удалён (мёртвый код).
- `TL.Secret.cs` (legacy secret-chat: layer 8/23/45/46/66/73/101/143/144) — удалён; живые базовые
  типы `DecryptedMessageBase/Media/Action` перенесены в `TL.Xtended.cs`.
- `TL.Table.cs` — ручной `Dictionary<uint, Type>` заменён reflection-реестром.