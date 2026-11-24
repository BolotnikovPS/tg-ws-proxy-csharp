#nullable disable
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>MTProto-сессии ботов: логин через auth.importBotAuthorization, соединение по botId, getMe.</summary>
public sealed partial class BotGateway
{
    private async Task<BotApiResult> GetMeAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var users = await connection.Client.GetUsersAsync([new InputUserSelf()], ct).ConfigureAwait(false);
        if (users is not { Length: > 0 } || users[0] is not User user)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        return BotApiResult.Success(BuildBotFullInfo(user));
    }

    /// <summary>
    /// Кэш соединений по botId: первый запрос выполняет логин, остальные ждут тот же Task.
    /// Живое соединение переиспользуется; оборванное вытесняется и освобождается, после чего
    /// выполняется повторный логин (неудавшийся логин также вытесняется).
    /// </summary>
    private async Task<BotConnection> GetConnectionAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        if (_connections.TryGetValue(botId, out var cached)
            && cached.IsCompletedSuccessfully
            && cached.Result.Client.IsConnected)
        {
            return cached.Result;
        }

        if (_connections.TryRemove(botId, out cached) && cached.IsCompletedSuccessfully)
        {
            cached.Result.Client.Dispose(); // освобождаем сокет мёртвого соединения
        }

        return await _connections.GetOrAdd(botId, _ => LoginAndCacheAsync(botId, botToken, apiId, apiHash, ct))
            .ConfigureAwait(false);
    }

    private async Task<BotConnection> LoginAndCacheAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        try
        {
            return await LoginAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        }
        catch
        {
            _connections.TryRemove(botId, out _);
            throw;
        }
    }

    private async Task<BotConnection> LoginAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        var session = _store.Load(botId);

        // Сессия без ApiId (файл прежней версии) не позволяет отправить initConnection,
        // обязательный первым сообщением новой сессии, — считаем её отсутствующей.
        var hadSession = session is { IsValid: true, ApiId: > 0 };

        // Первый вход требует api_id/api_hash; проверяем до сетевых операций (fail-fast).
        // При восстановленной сессии повторный importBotAuthorization не нужен: self-id берётся через users.getUsers.
        var aid = 0;
        if (!hadSession)
        {
            if (apiId is not int parsedApiId || string.IsNullOrWhiteSpace(apiHash))
            {
                throw new BotGatewayException(401,
                    $"{ApiIdHeader} and {ApiHashHeader} headers are required for the first request (login)");
            }
            aid = parsedApiId;
        }

        var client = new MtProtoClient(config.Host, config.Port, _secret);
        await client.ConnectAsync(session, testMode: false, ct).ConfigureAwait(false);

        UserBase me = null;
        if (!hadSession)
        {
            var auth = await client.ImportBotAuthorizationAsync(aid, apiHash, botToken, ct).ConfigureAwait(false);
            if (auth is not Auth_Authorization authorization)
            {
                throw new BotGatewayException(401, "auth.importBotAuthorization returned sign-up-required");
            }

            me = authorization.user;
        }

        _store.Save(botId, client.ExportSession());
        return new BotConnection(client, me);
    }

    /// <summary>Собирает объект <c>User</c> Bot API из MTProto-конструктора <c>user</c>.</summary>
    public static Dictionary<string, object> BuildUser(User u)
    {
        var flags = u.flags;
        var user = new Dictionary<string, object>
        {
            ["id"] = u.id,
            ["is_bot"] = flags.HasFlag(User.Flags.bot),
        };
        if (flags.HasFlag(User.Flags.has_first_name))
        {
            user["first_name"] = u.first_name;
        }

        if (flags.HasFlag(User.Flags.has_last_name))
        {
            user["last_name"] = u.last_name;
        }

        if (flags.HasFlag(User.Flags.has_username))
        {
            user["username"] = u.username;
        }

        if (flags.HasFlag(User.Flags.has_lang_code))
        {
            user["language_code"] = u.lang_code;
        }

        return user;
    }

    /// <summary>
    /// Собирает объект <c>BotFullInfo</c> Bot API (10.5) — ответ <b>только для getMe</b>:
    /// база <see cref="BuildUser"/> (id, is_bot, first_name, last_name, username, language_code),
    /// все 12 обязательных boolean и опциональные <c>active_usernames</c>/<c>inline_query_placeholder</c>.
    /// <para>
    /// Соответствие MTProto-флагов layer 229: <c>bot_nochats</c> (инвертируется в «можно добавить
    /// в группы»), <c>bot_chat_history</c>, <c>has_bot_inline_placeholder</c>, <c>bot_inline_geo</c>,
    /// <c>flags2.bot_business</c>, <c>flags2.bot_has_main_app</c>, <c>flags2.bot_forum_view</c>,
    /// <c>flags2.bot_forum_can_manage_topics</c>, <c>flags2.bot_can_manage_bots</c>,
    /// <c>flags2.bot_guestchat</c>. Для <c>supports_join_request_queries</c> и <c>has_topic_sidebar</c>
    /// в layer 229 источника нет — они обязательные в <c>BotFullInfo</c>, поэтому отдаются <c>false</c>.
    /// </para>
    /// <para>
    /// Опциональные поля эмитятся только при наличии данных: <c>active_usernames</c> — из
    /// <c>User.usernames</c> (флаг2 <c>has_usernames</c>) с фильтром <c>Username.Flags.active</c>;
    /// <c>inline_query_placeholder</c> — при флаге <c>has_bot_inline_placeholder</c> и непустом
    /// <c>bot_inline_placeholder</c>. В объекты User для <c>from</c>/<c>chat</c> в апдейтах эти поля
    /// не попадают: их добавляет только этот метод.
    /// </para>
    /// </summary>
    internal static Dictionary<string, object> BuildBotFullInfo(User u)
    {
        var dto = BuildUser(u);
        var flags = u.flags;
        var flags2 = u.flags2;

        // BotFullInfo.is_bot — тип True: ответ getMe всегда про самого бота.
        dto["is_bot"] = true;

        // 10 обязательных boolean с прямым источником в layer 229.
        dto["can_join_groups"] = !flags.HasFlag(User.Flags.bot_nochats);
        dto["can_read_all_group_messages"] = flags.HasFlag(User.Flags.bot_chat_history);
        dto["supports_inline_queries"] = flags.HasFlag(User.Flags.has_bot_inline_placeholder);
        dto["can_connect_to_business"] = flags2.HasFlag(User.Flags2.bot_business);
        dto["has_main_web_app"] = flags2.HasFlag(User.Flags2.bot_has_main_app);
        dto["has_topics_enabled"] = flags2.HasFlag(User.Flags2.bot_forum_view);
        dto["allows_users_to_create_topics"] = flags2.HasFlag(User.Flags2.bot_forum_can_manage_topics);
        dto["can_manage_bots"] = flags2.HasFlag(User.Flags2.bot_can_manage_bots);
        dto["supports_guest_queries"] = flags2.HasFlag(User.Flags2.bot_guestchat);
        dto["needs_inline_query_location"] = flags.HasFlag(User.Flags.bot_inline_geo);

        // 2 обязательных boolean без источника в layer 229 — отдаём константой false.
        dto["supports_join_request_queries"] = false;
        dto["has_topic_sidebar"] = false;

        // Опциональные: только при наличии данных, иначе ключи отсутствуют.
        if (flags2.HasFlag(User.Flags2.has_usernames) && u.usernames is { Length: > 0 })
        {
            var activeUsernames = u.usernames
                .Where(x => x.flags.HasFlag(Username.Flags.active))
                .Select(x => x.username)
                .ToArray();
            if (activeUsernames.Length > 0)
            {
                dto["active_usernames"] = activeUsernames;
            }
        }

        if (flags.HasFlag(User.Flags.has_bot_inline_placeholder) && !string.IsNullOrEmpty(u.bot_inline_placeholder))
        {
            dto["inline_query_placeholder"] = u.bot_inline_placeholder;
        }

        return dto;
    }

    private sealed class BotConnection(MtProtoClient client, UserBase me)
    {
        public MtProtoClient Client { get; } = client;
        public UserBase Me { get; } = me;
    }
}
