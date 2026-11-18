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

        var me = BuildUser(user);
        AddBotSelfFields(me, user);
        return BotApiResult.Success(me);
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
    /// Дополняет объект <c>User</c> полями, которые Bot API отдаёт <b>только в getMe</b>:
    /// <c>can_join_groups</c>, <c>can_read_all_group_messages</c>, <c>supports_inline_queries</c>,
    /// <c>can_connect_to_business</c>, <c>has_main_web_app</c>.
    /// <para>
    /// Соответствие MTProto-флагов: <c>bot_nochats</c> (инвертируется в «можно добавить в группы»),
    /// <c>bot_chat_history</c>, <c>has_bot_inline_placeholder</c>, <c>flags2.bot_business</c>,
    /// <c>flags2.bot_has_main_app</c>. В объектах чужих User (в апдейтах) этих полей нет — их
    /// добавляет только этот метод.
    /// </para>
    /// </summary>
    internal static void AddBotSelfFields(Dictionary<string, object> dto, User u)
    {
        var flags = u.flags;
        var flags2 = u.flags2;
        dto["can_join_groups"] = !flags.HasFlag(User.Flags.bot_nochats);
        dto["can_read_all_group_messages"] = flags.HasFlag(User.Flags.bot_chat_history);
        dto["supports_inline_queries"] = flags.HasFlag(User.Flags.has_bot_inline_placeholder);
        dto["can_connect_to_business"] = flags2.HasFlag(User.Flags2.bot_business);
        dto["has_main_web_app"] = flags2.HasFlag(User.Flags2.bot_has_main_app);
    }

    private sealed class BotConnection(MtProtoClient client, UserBase me)
    {
        public MtProtoClient Client { get; } = client;
        public UserBase Me { get; } = me;
    }
}
