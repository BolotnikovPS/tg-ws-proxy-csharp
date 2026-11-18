#nullable disable
using System.Globalization;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Разрешение адресатов: chat_id/@username/raw peer, access_hash, inputPeer/inputChannel.</summary>
public sealed partial class BotGateway
{
    // ===== Tier 3: чат-методы =====

    /// <summary>Кэширует access_hash канала (если он есть в объекте Channel).</summary>
    private static void RememberChannelAccessHash(Channel ch)
    {
        if (ch != null && ch.flags.HasFlag(Channel.Flags.has_access_hash) && ch.access_hash != 0)
        {
            _channelAccessHash[ch.id] = ch.access_hash;
        }
    }

    /// <summary>Кэширует access_hash всех каналов из словаря chats результата метода.</summary>
    private static void RememberChannelAccessHashes(IReadOnlyDictionary<long, ChatBase> chats)
    {
        if (chats == null)
        {
            return;
        }

        foreach (var kv in chats)
        {
            if (kv.Value is Channel ch)
            {
                RememberChannelAccessHash(ch);
            }
        }
    }

    /// <summary>Кэширует access_hash пользователя (если он есть в объекте User).</summary>
    private static void RememberUserAccessHash(User u)
    {
        if (u != null && u.flags.HasFlag(User.Flags.has_access_hash) && u.access_hash != 0)
        {
            _userAccessHash[u.id] = u.access_hash;
        }
    }

    /// <summary>Кэширует access_hash всех пользователей из словаря users результата метода/updates.</summary>
    private static void RememberUserAccessHashes(IReadOnlyDictionary<long, User> users)
    {
        if (users == null)
        {
            return;
        }

        foreach (var kv in users)
        {
            RememberUserAccessHash(kv.Value);
        }
    }

    /// <summary>Кэширует access_hash пользователей из массива результата users.getUsers и подобных.</summary>
    private static void RememberUserAccessHashes(IEnumerable<UserBase> users)
    {
        if (users == null)
        {
            return;
        }

        foreach (var u in users)
        {
            if (u is User user)
            {
                RememberUserAccessHash(user);
            }
        }
    }

    /// <summary>
    /// access_hash пользователя для InputUser/InputPeerUser: явный аргумент (<c>access_hash</c>/<c>user_access_hash</c>)
    /// имеет приоритет, иначе берётся из кэша, заполненного updates/результатами RPC. Настоящий Bot API
    /// не требует access_hash от клиента, поэтому 0 — нормальный случай.
    /// </summary>
    private static long ResolveUserAccessHash(
        IReadOnlyDictionary<string, object> args, long userId, string argName = "access_hash")
    {
        var accessHash = ReadIntArg(args, argName, 0);
        if (accessHash == 0)
        {
            _userAccessHash.TryGetValue(userId, out accessHash);
        }

        return accessHash;
    }

    /// <summary>Собирает InputChannel из Bot API chat_id, беря access_hash из кэша; иначе 400 с подсказкой.</summary>
    private static InputChannel ResolveInputChannel(long botApiChatId)
    {
        var channelId = -1000000000000L - botApiChatId;
        if (!_channelAccessHash.TryGetValue(channelId, out var accessHash) || accessHash == 0)
        {
            throw new BotGatewayException(
                400, "channel access_hash not resolved yet: bot must receive an update mentioning this chat first");
        }

        return new InputChannel { channel_id = channelId, access_hash = accessHash };
    }

    /// <summary>Требует chat_id группы/канала (без обращения к сети); иначе BotGatewayException(400).</summary>
    private static long RequireGroupChatId(IReadOnlyDictionary<string, object> args, string methodName)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            throw new BotGatewayException(400, $"chat_id is required for {methodName}");
        }

        return chatId;
    }

    /// <summary>
    /// То же, что <see cref="RequireGroupChatId"/>, но принимает и <c>@username</c>: для него нужен
    /// <c>contacts.resolveUsername</c>, поэтому метод асинхронный. Найденный access_hash попадает
    /// в кэш, так что последующий <see cref="ResolveInputChannel"/> работает без сети.
    /// </summary>
    private async Task<long> RequireGroupChatIdAsync(
        MtProtoClient client,
        IReadOnlyDictionary<string, object> args,
        string methodName,
        CancellationToken ct)
    {
        var (chatId, _) = await ResolveChatIdAsync(client, args, "chat_id", "access_hash", ct).ConfigureAwait(false);
        if (chatId == long.MinValue)
        {
            throw new BotGatewayException(400, $"chat_id is required for {methodName}");
        }

        return chatId;
    }

    /// <summary>
    /// Проверяет, что <c>chat_id</c> вообще передан — числом или username'ом
    /// (используется на этапе валидации до похода в сеть).
    /// </summary>
    internal static bool HasChatIdArg(IReadOnlyDictionary<string, object> args, string name)
        => ReadUsernameArg(args, name) != null || ReadIntArg(args, name, long.MinValue) != long.MinValue;

    /// <summary>Резолв chat_id+access_hash в InputPeer (без обращения к args-словарю); self → InputPeerSelf.</summary>
    private static InputPeer ResolvePeerById(long chatId, long accessHash, long selfId)
    {
        if (chatId == selfId)
        {
            return new InputPeerSelf();
        }

        if (chatId > 0)
        {
            if (accessHash == 0)
            {
                throw new BotGatewayException(400, "private chat_id requires 'access_hash'");
            }

            return new InputPeerUser { user_id = chatId, access_hash = accessHash };
        }

        if (chatId > -1000000000000L)
        {
            return new InputPeerChat { chat_id = -chatId };
        }

        if (accessHash == 0)
        {
            throw new BotGatewayException(400, "channel chat_id requires 'access_hash'");
        }

        return new InputPeerChannel { channel_id = -1000000000000L - chatId, access_hash = accessHash };
    }

    /// <summary>
    /// Резолв Bot API <c>chat_id</c> (или raw-объекта <c>peer</c>) в MTProto <see cref="InputPeer"/>.
    /// Пользователям и каналам нужен access_hash: его передают отдельным аргументом, либо целиком как <c>peer</c>.
    /// </summary>
    internal static InputPeer ResolvePeer(IReadOnlyDictionary<string, object> args, long selfId)
    {
        if (args != null && args.TryGetValue("peer", out var rawPeer) && rawPeer is JsonElement pe)
        {
            return ParsePeerJson(pe);
        }

        return ResolvePeer(args, selfId, "chat_id", "access_hash");
    }

    /// <summary>
    /// Резолв пира по произвольному ключу chat_id (например, <c>from_chat_id</c>) с отдельным ключом access_hash.
    /// </summary>
    internal static InputPeer ResolvePeer(
        IReadOnlyDictionary<string, object> args, long selfId, string chatIdKey, string accessHashKey)
    {
        var chatId = ReadIntArg(args, chatIdKey, long.MinValue);
        if (chatId == long.MinValue)
        {
            throw new BotGatewayException(400, $"{chatIdKey} (or raw 'peer') is required");
        }

        if (chatId == selfId)
        {
            return new InputPeerSelf();
        }

        var accessHash = ReadIntArg(args, accessHashKey, 0);

        if (chatId > 0)
        {
            if (accessHash == 0)
            {
                // Настоящий Bot API не требует access_hash от клиента: он известен серверу.
                // Берём его из кэша, заполненного updates/результатами RPC.
                _userAccessHash.TryGetValue(chatId, out accessHash);
            }

            if (accessHash == 0)
            {
                throw new BotGatewayException(400, "Bad Request: chat not found");
            }

            return new InputPeerUser { user_id = chatId, access_hash = accessHash };
        }

        if (chatId > -1000000000000L)
        {
            // Базовый групповой чат: MTProto chat_id положительный (= -chat_id).
            return new InputPeerChat { chat_id = -chatId };
        }

        // Канал/супергруппа (-100…): channel_id = -1000000000000 - chat_id.
        if (accessHash == 0)
        {
            _channelAccessHash.TryGetValue(-1000000000000L - chatId, out accessHash);
        }

        if (accessHash == 0)
        {
            throw new BotGatewayException(
                400, "channel access_hash not resolved yet: bot must receive an update mentioning this chat first");
        }

        return new InputPeerChannel
        {
            channel_id = -1000000000000L - chatId,
            access_hash = accessHash,
        };
    }

    /// <summary>
    /// Асинхронный резолв <c>chat_id</c>: помимо числа Bot API принимает username канала
    /// (<c>@channelusername</c>), как официальный Bot API. Username резолвится через
    /// <c>contacts.resolveUsername</c>; найденный access_hash попадает в кэш, поэтому повторные
    /// вызовы к тому же каналу уже не ходят в сеть.
    /// </summary>
    internal async Task<InputPeer> ResolvePeerAsync(
        MtProtoClient client, IReadOnlyDictionary<string, object> args, long selfId, CancellationToken ct)
    {
        if (args != null && args.TryGetValue("peer", out var rawPeer) && rawPeer is JsonElement pe)
        {
            return ParsePeerJson(pe);
        }

        return await ResolvePeerAsync(client, args, selfId, "chat_id", "access_hash", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Асинхронный резолв пира по произвольному ключу chat_id (например, <c>from_chat_id</c>):
    /// username обрабатывается через <c>contacts.resolveUsername</c>, остальное — числовой резолв.
    /// </summary>
    internal async Task<InputPeer> ResolvePeerAsync(
        MtProtoClient client,
        IReadOnlyDictionary<string, object> args,
        long selfId,
        string chatIdKey,
        string accessHashKey,
        CancellationToken ct)
    {
        var username = ReadUsernameArg(args, chatIdKey);
        if (username == null)
        {
            return ResolvePeer(args, selfId, chatIdKey, accessHashKey);
        }

        var resolved = await client
            .InvokeAsync<Contacts_ResolvedPeer>(new Contacts_ResolveUsername { username = username }, ct)
            .ConfigureAwait(false);
        var (chatId, accessHash) = MapResolvedPeer(resolved);
        return ResolvePeerById(chatId, accessHash, selfId);
    }

    /// <summary>
    /// Резолв <c>chat_id</c> в пару (id, access_hash) с поддержкой username. Нужен методам,
    /// которые работают с числовым id напрямую (<c>getChat</c>, <c>setChatTitle</c>, <c>banChatMember</c>…):
    /// Bot API объявляет <c>chat_id</c> как <c>Integer | String</c>, и <c>@channelusername</c> там
    /// равноправен числу. Если аргумент не username, возвращает число как есть
    /// (отсутствие — <see cref="long.MinValue"/>).
    /// </summary>
    internal async Task<(long ChatId, long AccessHash)> ResolveChatIdAsync(
        MtProtoClient client,
        IReadOnlyDictionary<string, object> args,
        string chatIdKey,
        string accessHashKey,
        CancellationToken ct)
    {
        var username = ReadUsernameArg(args, chatIdKey);
        if (username == null)
        {
            return (ReadIntArg(args, chatIdKey, long.MinValue), ReadIntArg(args, accessHashKey, 0));
        }

        var resolved = await client
            .InvokeAsync<Contacts_ResolvedPeer>(new Contacts_ResolveUsername { username = username }, ct)
            .ConfigureAwait(false);
        return MapResolvedPeer(resolved);
    }

    /// <summary>
    /// Читает <c>chat_id</c> в виде username (<c>@channelusername</c> либо <c>channelusername</c>).
    /// Возвращает null, если значение отсутствует или числовое — тогда работает обычный числовой резолв
    /// (числовая строка тоже допустима, Bot API объявляет <c>chat_id</c> как Integer|String).
    /// </summary>
    internal static string ReadUsernameArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        var text = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        var username = text.StartsWith('@') ? text[1..] : text;
        if (username.Length is 0 or > 32 || !IsUsernameChar(username))
        {
            throw new BotGatewayException(400, $"Bad Request: chat not found ('{text}')");
        }

        return username;
    }

    /// <summary>Username в Telegram: латиница, цифры и «_» (кириллица в username не встречается).</summary>
    private static bool IsUsernameChar(string username)
    {
        foreach (var c in username)
        {
            if (c is not ('_' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Переводит ответ <c>contacts.resolveUsername</c> в пару (Bot API chat_id, access_hash) и пополняет
    /// кэш access_hash'ей. Bot API chat_id: пользователь — положительный id, базовый чат — отрицательный,
    /// канал/супергруппа — <c>-100…</c>.
    /// </summary>
    internal static (long ChatId, long AccessHash) MapResolvedPeer(Contacts_ResolvedPeer resolved)
    {
        switch (resolved?.peer)
        {
            case PeerUser pu:
                var userHash = resolved.users != null &&
                               resolved.users.TryGetValue(pu.user_id, out var user) && user != null
                    ? user.access_hash
                    : 0;
                if (userHash != 0)
                {
                    _userAccessHash[pu.user_id] = userHash;
                }

                return (pu.user_id, userHash);
            case PeerChannel pc:
                var channelHash = resolved.chats != null &&
                                  resolved.chats.TryGetValue(pc.channel_id, out var chat) && chat is Channel channel
                    ? channel.access_hash
                    : 0;
                if (channelHash != 0)
                {
                    _channelAccessHash[pc.channel_id] = channelHash;
                }

                return (-1000000000000L - pc.channel_id, channelHash);
            case PeerChat pch:
                // Базовый групповой чат: access_hash в MTProto не нужен.
                return (-pch.chat_id, 0);
            default:
                throw new BotGatewayException(400, "Bad Request: chat not found");
        }
    }

    /// <summary>Разбирает raw MTProto peer: {"_":"inputPeerUser","user_id":…,"access_hash":…}.</summary>
    private static InputPeer ParsePeerJson(JsonElement pe)
    {
        if (pe.ValueKind != JsonValueKind.Object || !pe.TryGetProperty("_", out var t) ||
            t.ValueKind != JsonValueKind.String)
        {
            throw new BotGatewayException(400, "'peer' must be an object with a '_' discriminator");
        }

        return t.GetString() switch
        {
            "inputPeerSelf" => new InputPeerSelf(),
            "inputPeerUser" => new InputPeerUser
            {
                user_id = ReadPeerLong(pe, "user_id"),
                access_hash = ReadPeerLong(pe, "access_hash"),
            },
            "inputPeerChat" => new InputPeerChat { chat_id = ReadPeerLong(pe, "chat_id") },
            "inputPeerChannel" => new InputPeerChannel
            {
                channel_id = ReadPeerLong(pe, "channel_id"),
                access_hash = ReadPeerLong(pe, "access_hash"),
            },
            var other => throw new BotGatewayException(400, $"unsupported peer type '{other}'"),
        };
    }

    private static long ReadPeerLong(JsonElement pe, string name)
    {
        if (!pe.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var val))
        {
            throw new BotGatewayException(400, $"'peer.{name}' must be an integer");
        }

        return val;
    }
}
