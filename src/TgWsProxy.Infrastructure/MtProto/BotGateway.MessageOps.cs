#nullable disable
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Операции над уже отправленными сообщениями: edit*, delete*, forward/copy, answerCallbackQuery.</summary>
public sealed partial class BotGateway
{
    // ===== Tier 2: editMessage*/deleteMessage/forwardMessage/copyMessage/answerCallbackQuery =====

    /// <summary>Возвращает self-пользователя бота (users.getUsers → User) или null при неудаче.</summary>
    private static async Task<User> GetSelfUserAsync(MtProtoClient client, CancellationToken ct)
    {
        var users = await client.GetUsersAsync([new InputUserSelf()], ct).ConfigureAwait(false);
        return users is { Length: > 0 } && users[0] is User u ? u : null;
    }

    /// <summary>
    /// Резолв цели редактирования (peer + message_id) после валидации; требует selfId для self-пира.
    /// Асинхронный, потому что <c>chat_id</c> может быть username'ом (<c>contacts.resolveUsername</c>).
    /// </summary>
    private async Task<(InputPeer Peer, int MessageId)> ResolveEditTargetAsync(
        MtProtoClient client,
        IReadOnlyDictionary<string, object> args,
        long selfId,
        string methodName,
        CancellationToken ct)
    {
        ValidateEditTarget(args, methodName);
        var peer = await ResolvePeerAsync(client, args, selfId, ct).ConfigureAwait(false);
        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            throw new BotGatewayException(400, $"message_id is required for {methodName}");
        }

        return (peer, (int)messageId);
    }

    /// <summary>editMessageText: messages.editMessage (has_message + no_webpage/entities/reply_markup).</summary>
    private async Task<BotApiResult> EditMessageTextAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var text = ReadStringArg(args, "text");
        if (string.IsNullOrEmpty(text))
        {
            return BotApiResult.Fail(400, "text is required for editMessageText");
        }

        ValidateEditTarget(args, "editMessageText");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = await ResolveEditTargetAsync(connection.Client, args, selfUser.id, "editMessageText", ct).ConfigureAwait(false);
        var (message, entities) = ResolveTextAndEntities(args, text);

        var request = new Messages_EditMessage
        {
            flags = 0,
            peer = peer,
            id = messageId,
            message = message,
        };
        request.flags |= Messages_EditMessage.Flags.has_message;

        if (ReadNoWebpageFlag(args))
        {
            request.flags |= Messages_EditMessage.Flags.no_webpage;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= Messages_EditMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await connection.Client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>editMessageCaption: messages.editMessage (message-поле = caption); пустая строка очищает подпись.</summary>
    private async Task<BotApiResult> EditMessageCaptionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var caption = ReadStringArg(args, "caption");
        ValidateEditTarget(args, "editMessageCaption");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = await ResolveEditTargetAsync(connection.Client, args, selfUser.id, "editMessageCaption", ct).ConfigureAwait(false);
        var (message, entities) = ResolveCaptionAndEntities(args, caption);

        var request = new Messages_EditMessage { flags = 0, peer = peer, id = messageId };
        if (caption != null)
        {
            request.flags |= Messages_EditMessage.Flags.has_message;
            request.message = message ?? "";
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= Messages_EditMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await connection.Client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>editMessageMedia: messages.editMessage (has_media; media собирается как элемент альбома Tier 1).</summary>
    private async Task<BotApiResult> EditMessageMediaAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var mediaEl = ReadJsonObjectArg(args, "media");
        if (mediaEl == null)
        {
            return BotApiResult.Fail(400, "media is required for editMessageMedia");
        }

        ValidateEditTarget(args, "editMessageMedia");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = await ResolveEditTargetAsync(connection.Client, args, selfUser.id, "editMessageMedia", ct).ConfigureAwait(false);
        var single = await BuildSingleMediaAsync(connection.Client, peer, mediaEl.Value, args, ct).ConfigureAwait(false);

        var request = new Messages_EditMessage
        {
            flags = Messages_EditMessage.Flags.has_media,
            peer = peer,
            id = messageId,
            media = single.media,
        };

        if (!string.IsNullOrEmpty(single.message))
        {
            request.flags |= Messages_EditMessage.Flags.has_message;
            request.message = single.message;
        }

        if (single.entities is { Length: > 0 })
        {
            request.flags |= Messages_EditMessage.Flags.has_entities;
            request.entities = single.entities;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        var result = await connection.Client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>deleteMessage: удаление одного сообщения (см. <see cref="DeleteMessagesCoreAsync"/>).</summary>
    private async Task<BotApiResult> DeleteMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId is <= 0 or > int.MaxValue)
        {
            return BotApiResult.Fail(400, "message_id is required for deleteMessage");
        }

        return await DeleteMessagesCoreAsync(
            botId, botToken, apiId, apiHash, args, [(int)messageId], ct).ConfigureAwait(false);
    }

    /// <summary>deleteMessages: удаление 1..100 сообщений пакетом (Bot API 7.0+).</summary>
    private async Task<BotApiResult> DeleteMessagesAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var messageIds = ReadIntListArg(args, "message_ids");
        if (messageIds == null || messageIds.Count == 0)
        {
            return BotApiResult.Fail(400, "message_ids is required for deleteMessages");
        }

        if (messageIds.Count > 100)
        {
            return BotApiResult.Fail(400, "message_ids must contain at most 100 items for deleteMessages");
        }

        return await DeleteMessagesCoreAsync(
            botId, botToken, apiId, apiHash, args, messageIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Общая часть <c>deleteMessage</c>/<c>deleteMessages</c>: канал/супергруппа удаляются через
    /// <c>channels.deleteMessages</c>, остальные чаты — через <c>messages.deleteMessages</c> с
    /// флагом <c>revoke</c> (удаление «у всех», как в Bot API). Тип чата определяется по
    /// резолву <c>chat_id</c>, поэтому поддерживается и <c>@channelusername</c>.
    /// </summary>
    private async Task<BotApiResult> DeleteMessagesCoreAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, IReadOnlyList<int> messageIds, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        var peer = await ResolvePeerAsync(connection.Client, args, selfUser.id, ct).ConfigureAwait(false);

        if (peer is InputPeerChannel channel)
        {
            // channels.deleteMessages принимает InputChannel (inputChannel#f35aec28), а не InputPeer.
            await connection.Client.InvokeAsync<Messages_AffectedMessages>(
                new Channels_DeleteMessages
                {
                    channel = new InputChannel(channel.channel_id, channel.access_hash),
                    id = [.. messageIds],
                }, ct).ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<Messages_AffectedMessages>(
                new Messages_DeleteMessages
                {
                    flags = Messages_DeleteMessages.Flags.revoke,
                    id = [.. messageIds],
                }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>forwardMessage: messages.forwardMessages (silent=noforwards по флагам Bot API) → Message.</summary>
    private async Task<BotApiResult> ForwardMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (!HasChatIdArg(args, "from_chat_id"))
        {
            return BotApiResult.Fail(400, "from_chat_id is required for forwardMessage");
        }

        if (!HasChatIdArg(args, "chat_id"))
        {
            return BotApiResult.Fail(400, "chat_id is required for forwardMessage");
        }

        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            return BotApiResult.Fail(400, "message_id is required for forwardMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var toPeer = await ResolvePeerAsync(connection.Client, args, selfUser.id, ct).ConfigureAwait(false);
        var fromPeer = await ResolvePeerAsync(
            connection.Client, args, selfUser.id, "from_chat_id", "from_access_hash", ct).ConfigureAwait(false);

        var request = new Messages_ForwardMessages
        {
            flags = 0,
            from_peer = fromPeer,
            to_peer = toPeer,
            id = [(int)messageId],
            random_id = [NextRandomId()],
        };

        if (ReadBoolArg(args, "disable_notification"))
        {
            request.flags |= Messages_ForwardMessages.Flags.silent;
        }

        if (ReadBoolArg(args, "protect_content"))
        {
            request.flags |= Messages_ForwardMessages.Flags.noforwards;
        }

        var result = await connection.Client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildForwardedMessage(result, toPeer, selfUser));
    }

    /// <summary>copyMessage: forwardMessages (drop_author, +drop_media_captions при заданном caption), затем messages.editMessage по caption/reply_markup → Message.</summary>
    private async Task<BotApiResult> CopyMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (!HasChatIdArg(args, "from_chat_id"))
        {
            return BotApiResult.Fail(400, "from_chat_id is required for copyMessage");
        }

        if (!HasChatIdArg(args, "chat_id"))
        {
            return BotApiResult.Fail(400, "chat_id is required for copyMessage");
        }

        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            return BotApiResult.Fail(400, "message_id is required for copyMessage");
        }

        var caption = ReadStringArg(args, "caption");
        var replyMarkup = ReadReplyMarkupArg(args);

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var toPeer = await ResolvePeerAsync(connection.Client, args, selfUser.id, ct).ConfigureAwait(false);
        var fromPeer = await ResolvePeerAsync(
            connection.Client, args, selfUser.id, "from_chat_id", "from_access_hash", ct).ConfigureAwait(false);

        var request = new Messages_ForwardMessages
        {
            flags = Messages_ForwardMessages.Flags.drop_author,
            from_peer = fromPeer,
            to_peer = toPeer,
            id = [(int)messageId],
            random_id = [NextRandomId()],
        };

        if (caption != null)
        {
            request.flags |= Messages_ForwardMessages.Flags.drop_media_captions;
        }

        if (ReadBoolArg(args, "disable_notification"))
        {
            request.flags |= Messages_ForwardMessages.Flags.silent;
        }

        if (ReadBoolArg(args, "protect_content"))
        {
            request.flags |= Messages_ForwardMessages.Flags.noforwards;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= Messages_ForwardMessages.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var result = await connection.Client.InvokeAsync<UpdatesBase>(request, ct).ConfigureAwait(false);
        var forwarded = BuildForwardedMessage(result, toPeer, selfUser);
        var newMessageId = Convert.ToInt32(forwarded["message_id"]);

        // Caption (или reply_markup) заданы явно → применяем их к скопированному сообщению вторым шагом:
        // messages.editMessage по to_peer + новому message_id. Forward сам не умеет задавать caption/reply_markup.
        var hasCaption = caption != null;
        var hasMarkup = replyMarkup != null;
        if (hasCaption || hasMarkup)
        {
            var edit = new Messages_EditMessage { flags = 0, peer = toPeer, id = newMessageId };

            if (hasCaption)
            {
                var (text, entities) = ResolveCaptionAndEntities(args, caption);
                edit.flags |= Messages_EditMessage.Flags.has_message;
                edit.message = text ?? "";
                if (entities is { Length: > 0 })
                {
                    edit.flags |= Messages_EditMessage.Flags.has_entities;
                    edit.entities = entities;
                }
            }

            if (hasMarkup)
            {
                edit.flags |= Messages_EditMessage.Flags.has_reply_markup;
                edit.reply_markup = replyMarkup;
            }

            await connection.Client.InvokeAsync<UpdatesBase>(edit, ct).ConfigureAwait(false);
        }

        // Bot API у copyMessage возвращает MessageId, а не Message (в отличие от forwardMessage).
        return BotApiResult.Success(new Dictionary<string, object> { ["message_id"] = newMessageId });
    }

    /// <summary>answerCallbackQuery: messages.setBotCallbackAnswer → bool.</summary>
    private async Task<BotApiResult> AnswerCallbackQueryAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var queryId = ReadIntArg(args, "callback_query_id", 0);
        if (queryId == 0)
        {
            return BotApiResult.Fail(400, "callback_query_id is required for answerCallbackQuery");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var request = new Messages_SetBotCallbackAnswer
        {
            flags = 0,
            query_id = queryId,
            cache_time = 0,
        };

        var text = ReadStringArg(args, "text");
        if (text != null)
        {
            request.flags |= Messages_SetBotCallbackAnswer.Flags.has_message;
            request.message = text;
        }

        if (ReadBoolArg(args, "show_alert"))
        {
            request.flags |= Messages_SetBotCallbackAnswer.Flags.alert;
        }

        var url = ReadStringArg(args, "url");
        if (!string.IsNullOrEmpty(url))
        {
            request.flags |= Messages_SetBotCallbackAnswer.Flags.has_url;
            request.url = url;
        }

        request.cache_time = (int)ReadIntArg(args, "cache_time", 0);
        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }
}
