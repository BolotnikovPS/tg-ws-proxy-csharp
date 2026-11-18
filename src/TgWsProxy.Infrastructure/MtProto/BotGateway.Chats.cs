#nullable disable
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Информация о чате и участниках, настройки чата, ограничения (ban/leave).</summary>
public sealed partial class BotGateway
{
    /// <summary>Chat DTO Bot API ({id(-100…), type, title, username}) из Chat/Channel MTProto.</summary>
    private static Dictionary<string, object> BuildChatDto(ChatBase chatBase)
    {
        switch (chatBase)
        {
            case Chat c:
                return new Dictionary<string, object>
                {
                    ["id"] = -c.id,
                    ["type"] = "group",
                    ["title"] = c.title,
                };
            case Channel ch:
                RememberChannelAccessHash(ch);
                var d = new Dictionary<string, object>
                {
                    ["id"] = -1000000000000L - ch.id,
                    ["type"] = ch.flags.HasFlag(Channel.Flags.megagroup) ? "supergroup" : "channel",
                    ["title"] = ch.title,
                };
                if (ch.flags.HasFlag(Channel.Flags.has_username))
                {
                    d["username"] = ch.username;
                }

                return d;
            default:
                return null;
        }
    }

    /// <summary>
    /// ChatPermissions Bot API из MTProto <c>ChatBannedRights</c>: в MTProto биты означают
    /// «запрещено», поэтому все значения инвертируются. Легаси-биты <c>send_messages</c>
    /// (любые сообщения, включая текст) и <c>send_media</c> (любое медиа) запрещают целую
    /// группу типов и учитываются во всех соответствующих разрешениях.
    /// </summary>
    internal static Dictionary<string, object> BuildChatPermissions(ChatBannedRights banned)
    {
        var flags = banned?.flags ?? default;
        bool Allowed(ChatBannedRights.Flags denied) => !flags.HasFlag(denied);
        var mediaAllowed = Allowed(ChatBannedRights.Flags.send_media);
        var anyMessageAllowed = Allowed(ChatBannedRights.Flags.send_messages);
        return new Dictionary<string, object>
        {
            ["can_send_messages"] = anyMessageAllowed && Allowed(ChatBannedRights.Flags.send_plain),
            ["can_send_audios"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_audios),
            ["can_send_documents"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_docs),
            ["can_send_photos"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_photos),
            ["can_send_videos"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_videos),
            ["can_send_video_notes"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_roundvideos),
            ["can_send_voice_notes"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_voices),
            ["can_send_polls"] = mediaAllowed && Allowed(ChatBannedRights.Flags.send_polls),
            ["can_send_other_messages"] = mediaAllowed
                && Allowed(ChatBannedRights.Flags.send_stickers)
                && Allowed(ChatBannedRights.Flags.send_gifs)
                && Allowed(ChatBannedRights.Flags.send_games)
                && Allowed(ChatBannedRights.Flags.send_inline),
            ["can_add_web_page_previews"] = anyMessageAllowed && Allowed(ChatBannedRights.Flags.embed_links),
            ["can_change_info"] = Allowed(ChatBannedRights.Flags.change_info),
            ["can_invite_users"] = Allowed(ChatBannedRights.Flags.invite_users),
            ["can_pin_messages"] = Allowed(ChatBannedRights.Flags.pin_messages),
            ["can_manage_topics"] = Allowed(ChatBannedRights.Flags.manage_topics),
        };
    }

    /// <summary>Общие для групп и каналов поля ChatFullInfo из <c>ChatFull</c>/<c>ChannelFull</c>.</summary>
    private static void MergeCommonFullInfo(Dictionary<string, object> dto, string about, ExportedChatInvite invite)
    {
        if (!string.IsNullOrEmpty(about))
        {
            dto["description"] = about;
        }

        if (invite is ChatInviteExported exported && !string.IsNullOrEmpty(exported.link))
        {
            dto["invite_link"] = exported.link;
        }
    }

    /// <summary>
    /// Дополняет DTO базовой группы полями ChatFullInfo из <c>messages.getFullChat</c>.
    /// Не отдаются: <c>photo</c> (у <c>ChatPhoto</c> нет access_hash/file_reference, скачать нельзя)
    /// и <c>pinned_message</c> (нужен отдельный <c>messages.getMessages</c>).
    /// </summary>
    internal static void MergeChatFullInfo(Dictionary<string, object> dto, Chat chat, ChatFull full)
    {
        if (dto == null || full == null)
        {
            return;
        }

        MergeCommonFullInfo(dto, full.about, full.exported_invite);
        if (chat?.default_banned_rights is { } banned)
        {
            dto["permissions"] = BuildChatPermissions(banned);
        }
    }

    /// <summary>
    /// Дополняет DTO канала/супергруппы полями ChatFullInfo из <c>channels.getFullChannel</c>.
    /// Не отдаются: <c>photo</c>, <c>pinned_message</c>, <c>has_visible_history</c>
    /// (MTProto <c>hidden_prehistory</c> описывает видимость истории для нас, а не для новых участников).
    /// </summary>
    internal static void MergeChannelFullInfo(Dictionary<string, object> dto, Channel channel, ChannelFull full)
    {
        if (dto == null || full == null)
        {
            return;
        }

        MergeCommonFullInfo(dto, full.about, full.exported_invite);
        if (channel != null)
        {
            if (channel.flags.HasFlag(Channel.Flags.forum))
            {
                dto["is_forum"] = true;
            }

            if (channel.flags.HasFlag(Channel.Flags.noforwards))
            {
                dto["has_protected_content"] = true;
            }

            if (channel.default_banned_rights is { } banned)
            {
                dto["permissions"] = BuildChatPermissions(banned);
            }
        }

        if (full.flags2.HasFlag(ChannelFull.Flags2.participants_hidden))
        {
            dto["has_hidden_members"] = true;
        }

        if (full.flags2.HasFlag(ChannelFull.Flags2.antispam))
        {
            dto["has_aggressive_anti_spam_enabled"] = true;
        }

        if (full.flags.HasFlag(ChannelFull.Flags.has_slowmode_seconds) && full.slowmode_seconds > 0)
        {
            dto["slow_mode_delay"] = full.slowmode_seconds;
        }

        if (full.flags.HasFlag(ChannelFull.Flags.has_linked_chat_id) && full.linked_chat_id != 0)
        {
            dto["linked_chat_id"] = -1000000000000L - full.linked_chat_id;
        }
    }

    /// <summary>ChatMember DTO Bot API: {status, user:{…}, custom_title?, until_date?}.</summary>
    private static Dictionary<string, object> BuildChatMember(
        long userId,
        IReadOnlyDictionary<long, User> users,
        string status,
        string customTitle = null,
        long? untilDate = null)
    {
        var m = new Dictionary<string, object> { ["status"] = status };
        if (users != null && users.TryGetValue(userId, out var u) && u is User user)
        {
            m["user"] = BuildUser(user);
        }
        else
        {
            m["user"] = new Dictionary<string, object> { ["id"] = userId };
        }

        if (!string.IsNullOrEmpty(customTitle))
        {
            m["custom_title"] = customTitle;
        }

        if (untilDate.HasValue)
        {
            m["until_date"] = untilDate.Value;
        }

        return m;
    }

    /// <summary>Извлекает (userId, status) из участника базовой группы.</summary>
    private static (long UserId, string Status) ExtractBasicParticipant(ChatParticipantBase p)
        => p switch
        {
            ChatParticipantCreator creator => (creator.user_id, "creator"),
            ChatParticipantAdmin admin => (admin.user_id, "administrator"),
            ChatParticipant member => (member.user_id, "member"),
            _ => (0, null),
        };

    /// <summary>Извлекает (userId, status, customTitle, untilDate) из участника канала/супергруппы.</summary>
    private static (long UserId, string Status, string CustomTitle, long? UntilDate) ExtractChannelParticipant(
        ChannelParticipantBase p)
    {
        switch (p)
        {
            case ChannelParticipantCreator c:
                return (c.user_id, "creator", string.IsNullOrEmpty(c.rank) ? null : c.rank, null);
            case ChannelParticipantAdmin a:
                return (a.user_id, "administrator", string.IsNullOrEmpty(a.rank) ? null : a.rank, null);
            case ChannelParticipantSelf s:
                return (s.user_id, "member", string.IsNullOrEmpty(s.rank) ? null : s.rank, null);
            case ChannelParticipant m:
                return (m.user_id, "member", string.IsNullOrEmpty(m.rank) ? null : m.rank, null);
            case ChannelParticipantBanned b:
                long bannedUntil = 0;
                if (b.banned_rights != null && b.banned_rights.until_date != default)
                {
                    bannedUntil = new DateTimeOffset(b.banned_rights.until_date.ToUniversalTime()).ToUnixTimeSeconds();
                }

                return (PeerUserId(b.peer), "kicked", null, bannedUntil);
            case ChannelParticipantLeft l:
                return (PeerUserId(l.peer), "left", null, null);
            default:
                return (0, null, null, null);
        }
    }

    private static long PeerUserId(Peer peer) => peer is PeerUser pu ? pu.user_id : 0;

    private static async Task<Dictionary<string, object>[]> GetChannelAdminsAsync(
        MtProtoClient client, long botApiChatId, CancellationToken ct)
    {
        var channel = ResolveInputChannel(botApiChatId);
        var res = await client.InvokeAsync<Channels_ChannelParticipants>(
            new Channels_GetParticipants
            {
                channel = channel,
                filter = new ChannelParticipantsAdmins(),
                offset = 0,
                limit = 100,
                hash = 0,
            }, ct).ConfigureAwait(false);
        if (res == null)
        {
            return [];
        }

        RememberChannelAccessHashes(res.chats);
        var result = new List<Dictionary<string, object>>();
        foreach (var p in res.participants)
        {
            var (userId, status, customTitle, _) = ExtractChannelParticipant(p);
            if (userId == 0)
            {
                continue;
            }

            result.Add(BuildChatMember(userId, res.users, status, customTitle));
        }

        return [.. result];
    }

    /// <summary>getChat: private→users.getUsers; group→messages.getChats; channel→channels.getChannels.</summary>
    private async Task<BotApiResult> GetChatAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;
        var (chatId, accessHash) = await ResolveChatIdAsync(client, args, "chat_id", "access_hash", ct)
            .ConfigureAwait(false);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChat");
        }

        if (chatId > 0)
        {
            var users = await client.GetUsersAsync(
                [new InputUser { user_id = chatId, access_hash = accessHash != 0 ? accessHash : ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            // A missing user is a CLIENT error, not a server fault: the real Bot API answers
            // 400 "Bad Request: chat not found" for an unknown chat_id.
            if (users is not { Length: > 0 } || users[0] is not User u)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            var userChat = BuildUser(u);
            userChat["type"] = "private";
            return BotApiResult.Success(userChat);
        }

        if (chatId > -1000000000000L)
        {
            var mtChatId = -chatId;
            var res = await client.InvokeAsync<Messages_Chats>(
                new Messages_GetChats { id = [mtChatId] }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            if (res.chats == null || !res.chats.TryGetValue(mtChatId, out var cb) || cb is not Chat c)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            var groupDto = BuildChatDto(c);
            // Bot API getChat отдаёт ChatFullInfo: about, invite_link и permissions берём из getFullChat
            // (второй запрос — цена соответствия контракту; chats/users из ответа не нужны).
            var fullChat = await client.InvokeAsync<Messages_ChatFull>(
                new Messages_GetFullChat { chat_id = mtChatId }, ct).ConfigureAwait(false);
            MergeChatFullInfo(groupDto, c, fullChat?.full_chat as ChatFull);
            return BotApiResult.Success(groupDto);
        }

        var channel = ResolveInputChannel(chatId);
        var cres = await client.InvokeAsync<Messages_Chats>(
            new Channels_GetChannels { id = [channel] }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(cres.chats);
        if (cres.chats == null || !cres.chats.TryGetValue(channel.channel_id, out var ccb) || ccb is not Channel ch)
        {
            return BotApiResult.Fail(400, "Bad Request: chat not found");
        }

        var channelDto = BuildChatDto(ch);
        var fullChannel = await client.InvokeAsync<Messages_ChatFull>(
            new Channels_GetFullChannel { channel = channel }, ct).ConfigureAwait(false);
        MergeChannelFullInfo(channelDto, ch, fullChannel?.full_chat as ChannelFull);
        return BotApiResult.Success(channelDto);
    }

    /// <summary>getChatAdministrators: channel→Channels_GetParticipants(Admins); group→Messages_GetFullChat.participants.</summary>
    private async Task<BotApiResult> GetChatAdministratorsAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;
        var (chatId, accessHash) = await ResolveChatIdAsync(client, args, "chat_id", "access_hash", ct)
            .ConfigureAwait(false);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatAdministrators");
        }

        if (chatId > 0)
        {
            // A private chat has no administrators, but the chat itself must exist.
            var users = await client.GetUsersAsync(
                [new InputUser { user_id = chatId, access_hash = accessHash != 0 ? accessHash : ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            return BotApiResult.Success(new object[0]);
        }

        if (chatId <= -1000000000000L)
        {
            return BotApiResult.Success(await GetChannelAdminsAsync(client, chatId, ct).ConfigureAwait(false));
        }

        var res = await client.InvokeAsync<Messages_ChatFull>(
            new Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(res.chats);
        var result = new List<Dictionary<string, object>>();
        if (res.full_chat is ChatFull full && full.participants is ChatParticipants cp)
        {
            foreach (var p in cp.participants)
            {
                var (userId, status) = ExtractBasicParticipant(p);
                if (userId == 0 || status is "member" or null)
                {
                    continue;
                }

                result.Add(BuildChatMember(userId, res.users, status));
            }
        }

        return BotApiResult.Success(result);
    }

    /// <summary>getChatMember: channel→Channels_GetParticipant; group→Messages_GetFullChat; private→member(user).</summary>
    private async Task<BotApiResult> GetChatMemberAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var userId = ReadIntArg(args, "user_id", long.MinValue);
        if (userId == long.MinValue)
        {
            return BotApiResult.Fail(400, "user_id is required for getChatMember");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;
        var (chatId, accessHash) = await ResolveChatIdAsync(client, args, "chat_id", "access_hash", ct)
            .ConfigureAwait(false);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatMember");
        }

        if (chatId > 0)
        {
            // Validate the chat first: an unknown chat_id must be a 400, not a synthetic "member".
            var users = await client.GetUsersAsync(
                [new InputUser { user_id = chatId, access_hash = ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            // A private chat contains only the user and this bot; anything else is not a member.
            if (userId != chatId && userId != botId)
            {
                return BotApiResult.Fail(400, "Bad Request: user not found");
            }

            // Пользователь уже получен выше — отдаём его целиком (Bot API ждёт в ChatMember.user
            // полноценный User, а не заглушку с одним id). Для самого бота дозапрашиваем себя.
            var members = new Dictionary<long, User>();
            foreach (var u in users)
            {
                if (u is User known)
                {
                    members[known.id] = known;
                }
            }

            if (userId == botId && !members.ContainsKey(botId)
                && await GetSelfUserAsync(client, ct).ConfigureAwait(false) is User selfMember)
            {
                members[selfMember.id] = selfMember;
            }

            return BotApiResult.Success(BuildChatMember(userId, members, "member"));
        }

        if (chatId <= -1000000000000L)
        {
            var channel = ResolveInputChannel(chatId);
            var res = await client.InvokeAsync<Channels_ChannelParticipant>(
                new Channels_GetParticipant
                {
                    channel = channel,
                    participant = new InputPeerUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            var (pUserId, status, customTitle, untilDate) = ExtractChannelParticipant(res.participant);
            if (pUserId == 0)
            {
                return BotApiResult.Success(BuildChatMember(userId, res.users, "left"));
            }

            return BotApiResult.Success(BuildChatMember(pUserId, res.users, status, customTitle, untilDate));
        }

        var fres = await client.InvokeAsync<Messages_ChatFull>(
            new Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(fres.chats);
        if (fres.full_chat is ChatFull full && full.participants is ChatParticipants cp)
        {
            foreach (var p in cp.participants)
            {
                var (pUserId, status) = ExtractBasicParticipant(p);
                if (pUserId == userId)
                {
                    return BotApiResult.Success(BuildChatMember(pUserId, fres.users, status));
                }
            }
        }

        return BotApiResult.Success(BuildChatMember(userId, null, "left"));
    }

    /// <summary>getChatMemberCount: group→messages.getFullChat (participants_count); channel→channels.getFullChannel.</summary>
    private async Task<BotApiResult> GetChatMemberCountAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;
        var (chatId, accessHash) = await ResolveChatIdAsync(client, args, "chat_id", "access_hash", ct)
            .ConfigureAwait(false);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatMemberCount");
        }

        if (chatId > 0)
        {
            // A private chat must exist before we report a member count; otherwise an unknown
            // chat_id would look successful. Real Bot API: 400 "Bad Request: chat not found".
            var users = await client.GetUsersAsync(
                [new InputUser { user_id = chatId, access_hash = accessHash != 0 ? accessHash : ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            // A private chat holds exactly two members: the user and the bot.
            return BotApiResult.Success(2);
        }

        if (chatId <= -1000000000000L)
        {
            var channel = ResolveInputChannel(chatId);
            var res = await client.InvokeAsync<Messages_ChatFull>(
                new Channels_GetFullChannel { channel = channel }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            return res.full_chat is ChannelFull cf
                ? BotApiResult.Success(cf.participants_count)
                : BotApiResult.Fail(400, "Bad Request: chat not found");
        }

        var fres = await client.InvokeAsync<Messages_ChatFull>(
            new Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(fres.chats);
        if (fres.full_chat is ChatFull full && full.participants is ChatParticipants cp)
        {
            return BotApiResult.Success(cp.participants.Length);
        }

        if (fres.chats != null && fres.chats.TryGetValue(-chatId, out var cb) && cb is Chat c)
        {
            return BotApiResult.Success(c.participants_count);
        }

        return BotApiResult.Fail(400, "Bad Request: chat not found");
    }

    /// <summary>setChatTitle: group→messages.editChatTitle; channel/supergroup→channels.editTitle.</summary>
    private async Task<BotApiResult> SetChatTitleAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var title = ReadStringArg(args, "title");
        if (string.IsNullOrEmpty(title))
        {
            return BotApiResult.Fail(400, "title is required for setChatTitle");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var chatId = await RequireGroupChatIdAsync(connection.Client, args, "setChatTitle", ct).ConfigureAwait(false);
        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Channels_EditTitle { channel = ResolveInputChannel(chatId), title = title }, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Messages_EditChatTitle { chat_id = -chatId, title = title }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>setChatDescription: group→messages.editChatAbout (bool); channel/supergroup — не поддержано.</summary>
    private async Task<BotApiResult> SetChatDescriptionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var description = ReadStringArg(args, "description") ?? "";
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var chatId = await RequireGroupChatIdAsync(connection.Client, args, "setChatDescription", ct)
            .ConfigureAwait(false);
        if (chatId <= -1000000000000L)
        {
            return BotApiResult.Fail(400, "setChatDescription is not supported for channels/supergroups yet");
        }

        var ok = await connection.Client.InvokeBoolAsync(
            new Messages_EditChatAbout
            {
                peer = new InputPeerChat { chat_id = -chatId },
                about = description,
            }, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>setChatPhoto: file_id→InputChatPhoto, multipart→InputChatUploadedPhoto; group→editChatPhoto / channel→editPhoto.</summary>
    private async Task<BotApiResult> SetChatPhotoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var photoRaw = ReadMediaArg(args, "photo");
        if (photoRaw == null)
        {
            return BotApiResult.Fail(400, "photo is required for setChatPhoto");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var chatId = await RequireGroupChatIdAsync(connection.Client, args, "setChatPhoto", ct).ConfigureAwait(false);

        InputChatPhotoBase chatPhoto;
        if (photoRaw is BotUploadFile upload)
        {
            var file = await UploadMediaAsync(connection.Client, upload, ct).ConfigureAwait(false);
            chatPhoto = new InputChatUploadedPhoto { flags = InputChatUploadedPhoto.Flags.has_file, file = file };
        }
        else
        {
            chatPhoto = new InputChatPhoto { id = DecodeInputPhoto((string)photoRaw) };
        }

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Channels_EditPhoto { channel = ResolveInputChannel(chatId), photo = chatPhoto }, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Messages_EditChatPhoto { chat_id = -chatId, photo = chatPhoto }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>leaveChat: group→messages.deleteChatUser(self); channel→channels.leaveChannel.</summary>
    private async Task<BotApiResult> LeaveChatAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var chatId = await RequireGroupChatIdAsync(connection.Client, args, "leaveChat", ct).ConfigureAwait(false);

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Channels_LeaveChannel { channel = ResolveInputChannel(chatId) }, ct).ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Messages_DeleteChatUser
                {
                    flags = 0,
                    chat_id = -chatId,
                    user_id = new InputUserSelf(),
                }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>banChatMember: channel→channels.editBanned(ChatBannedRights.view_messages); group→messages.deleteChatUser(kick).</summary>
    private async Task<BotApiResult> BanChatMemberAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var userId = ReadIntArg(args, "user_id", long.MinValue);
        if (userId == long.MinValue)
        {
            return BotApiResult.Fail(400, "user_id is required for banChatMember");
        }

        // until_date — Unix-время автоматического снятия бана; 0 или отсутствие = бессрочно.
        var untilDate = ReadIntArg(args, "until_date", 0);

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var chatId = await RequireGroupChatIdAsync(connection.Client, args, "banChatMember", ct).ConfigureAwait(false);

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Channels_EditBanned
                {
                    channel = ResolveInputChannel(chatId),
                    participant = new InputPeerUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                    banned_rights = new ChatBannedRights
                    {
                        flags = ChatBannedRights.Flags.view_messages,
                        until_date = ToBanUntilDate(untilDate),
                    },
                }, ct).ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<UpdatesBase>(
                new Messages_DeleteChatUser
                {
                    flags = 0,
                    chat_id = -chatId,
                    user_id = new InputUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }
}
