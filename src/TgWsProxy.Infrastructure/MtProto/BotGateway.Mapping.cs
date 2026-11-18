#nullable disable
using System.Globalization;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Преобразование TL-объектов в JSON Bot API: сообщения, чаты, медиа, forwarded/edit-варианты.</summary>
public sealed partial class BotGateway
{
    internal static Dictionary<string, object> BuildMessage(
        Message m,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var message = new Dictionary<string, object>
        {
            ["message_id"] = m.id,
            ["date"] = new DateTimeOffset(m.date.ToUniversalTime()).ToUnixTimeSeconds(),
        };

        // Превью ссылки (MessageMediaWebPage) — не медиа: сообщение остаётся текстовым, и Bot API
        // отдаёт text/entities. Раньше такое сообщение уезжало как caption/caption_entities,
        // из-за чего клиент показывал текст ссылки подписью к «медиа».
        var hasMedia = m.flags.HasFlag(Message.Flags.has_media) && m.media is not (null or MessageMediaWebPage);

        if (!string.IsNullOrEmpty(m.message))
        {
            // У медиа-сообщений m.message — это подпись (caption), у обычных — текст.
            if (hasMedia)
            {
                message["caption"] = m.message;
            }
            else
            {
                message["text"] = m.message;
            }
        }

        // Rich-сообщение (Bot API 10.1+): текстового тела у него нет, содержимое лежит в
        // Message.rich_message как дерево page-блоков. Без этого апдейт приходил пустым
        // сообщением (ни text, ни медиа), поэтому блоки «сплющиваются» в простой текст:
        // форматирование теряется, но клиент видит контент.
        if (!hasMedia && !message.ContainsKey("text") && m.rich_message is { blocks.Length: > 0 } richMessage)
        {
            var richText = RenderRichBlocks(richMessage.blocks);
            if (richText.Length > 0)
            {
                message["text"] = richText;
            }
        }

        if (hasMedia)
        {
            switch (m.media)
            {
                case MessageMediaPhoto { photo: Photo photo }:
                    message["photo"] = BuildPhotoSizes(photo);
                    break;
                case MessageMediaDocument { document: Document doc }:
                    message["document"] = BuildDocument(doc);
                    break;
            }
        }

        if (m.flags.HasFlag(Message.Flags.has_from_id) && m.from_id is PeerUser fromUser
            && users != null && users.TryGetValue(fromUser.user_id, out var u) && u is User sender)
        {
            message["from"] = BuildUser(sender);
        }

        var chat = BuildChat(m.peer_id, chats);
        if (chat != null)
        {
            message["chat"] = chat;
        }

        if (!string.IsNullOrEmpty(m.message) && m.flags.HasFlag(Message.Flags.has_entities) && m.entities is { Length: > 0 })
        {
            var entities = BuildEntities(m.entities, users);
            if (entities.Count > 0)
            {
                message[hasMedia ? "caption_entities" : "entities"] = entities;
            }
        }

        // Превью ссылки в текстовом сообщении Bot API отдаёт в link_preview_options
        // (не в entities), поэтому заполняем его отдельно от caption/media.
        if (!hasMedia && m.media is MessageMediaWebPage linkPreview)
        {
            message["link_preview_options"] = linkPreview.webpage is WebPage page && !string.IsNullOrEmpty(page.url)
                ? new Dictionary<string, object> { ["is_disabled"] = false, ["url"] = page.url }
                : new Dictionary<string, object> { ["is_disabled"] = true };
        }

        if (m.flags.HasFlag(Message.Flags.has_reply_to) && m.reply_to is MessageReplyHeader reply)
        {
            AppendReplyInfo(message, m.peer_id, reply, users, chats);
        }

        if (m.flags.HasFlag(Message.Flags.has_fwd_from) && m.fwd_from is MessageFwdHeader fwd)
        {
            var origin = BuildForwardOrigin(fwd, users, chats);
            if (origin != null)
            {
                message["forward_origin"] = origin;
            }
        }

        if (m.flags.HasFlag(Message.Flags.has_edit_date))
        {
            message["edit_date"] = new DateTimeOffset(m.edit_date.ToUniversalTime()).ToUnixTimeSeconds();
        }

        if (m.flags.HasFlag(Message.Flags.has_views))
        {
            message["views"] = m.views;
        }

        // Альбом (media group): grouped_id — общий идентификатор нескольких сообщений,
        // отправленных одним sendMediaGroup. В Bot API это строка media_group_id, по ней
        // клиент собирает альбом; без неё элементы приходят как отдельные сообщения.
        if (m.flags.HasFlag(Message.Flags.has_grouped_id) && m.grouped_id != 0)
        {
            message["media_group_id"] = m.grouped_id.ToString(CultureInfo.InvariantCulture);
        }

        // Inline-клавиатура, прикреплённая к сообщению: Bot API отдаёт её в Message.reply_markup.
        // Без этого клиент, получивший апдейт, не видит кнопок у собственных отправленных сообщений.
        if (m.flags.HasFlag(Message.Flags.has_reply_markup) && m.reply_markup is ReplyInlineMarkup inlineMarkup)
        {
            message["reply_markup"] = BuildInlineMarkupJson(inlineMarkup);
        }

        return message;
    }

    /// <summary>
    /// Переносит <c>MessageReplyHeader</c> в поля Bot API: <c>reply_to_message</c>,
    /// <c>message_thread_id</c>, <c>is_topic_message</c>, <c>quote</c>.
    /// Текст цитируемого сообщения в шлюзе отсутствует — <c>reply_to_message</c> отдаётся
    /// минимальной заглушкой (message_id + chat), без text/caption: подтягивать его
    /// отдельным <c>messages.getMessages</c> на каждый апдейт слишком дорого.
    /// </summary>
    private static void AppendReplyInfo(
        Dictionary<string, object> message,
        Peer peer,
        MessageReplyHeader reply,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        if (reply.flags.HasFlag(MessageReplyHeader.Flags.has_reply_to_msg_id) && reply.reply_to_msg_id > 0)
        {
            var stub = new Dictionary<string, object>
            {
                ["message_id"] = reply.reply_to_msg_id,
                ["date"] = 0L,
            };

            // Ответ в discussion-треде может указывать на другой чат (reply_to_peer_id).
            var replyChat = BuildChat(
                reply.flags.HasFlag(MessageReplyHeader.Flags.has_reply_to_peer_id) ? reply.reply_to_peer_id : peer,
                chats);
            if (replyChat != null)
            {
                stub["chat"] = replyChat;
            }

            message["reply_to_message"] = stub;
            // Устаревшее, но ещё используемое клиентами поле — дублируем для совместимости.
            message["reply_to_message_id"] = reply.reply_to_msg_id;
        }

        if (reply.flags.HasFlag(MessageReplyHeader.Flags.has_reply_to_top_id) && reply.reply_to_top_id > 0)
        {
            message["message_thread_id"] = reply.reply_to_top_id;
        }

        if (reply.flags.HasFlag(MessageReplyHeader.Flags.forum_topic) || reply.reply_to_top_id > 0)
        {
            message["is_topic_message"] = true;
        }

        if (reply.flags.HasFlag(MessageReplyHeader.Flags.has_quote_text) && !string.IsNullOrEmpty(reply.quote_text))
        {
            var quote = new Dictionary<string, object> { ["text"] = reply.quote_text };

            if (reply.flags.HasFlag(MessageReplyHeader.Flags.has_quote_entities) && reply.quote_entities is { Length: > 0 })
            {
                var quoteEntities = BuildEntities(reply.quote_entities, users);
                if (quoteEntities.Count > 0)
                {
                    quote["entities"] = quoteEntities;
                }
            }

            if (reply.flags.HasFlag(MessageReplyHeader.Flags.has_quote_offset))
            {
                quote["position"] = reply.quote_offset;
            }

            message["quote"] = quote;
        }
    }

    /// <summary>
    /// Собирает Bot API <c>forward_origin</c> из <c>MessageFwdHeader</c>. Отдаются только
    /// восстановимые без дополнительных запросов варианты: user / hidden_user / chat / channel.
    /// При включённой приватности форварда сервер присылает лишь имя автора — это hidden_user.
    /// </summary>
    private static Dictionary<string, object> BuildForwardOrigin(
        MessageFwdHeader fwd,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats)
    {
        var date = new DateTimeOffset(fwd.date.ToUniversalTime()).ToUnixTimeSeconds();

        if (fwd.flags.HasFlag(MessageFwdHeader.Flags.has_from_name) && !string.IsNullOrEmpty(fwd.from_name))
        {
            return new Dictionary<string, object>
            {
                ["type"] = "hidden_user",
                ["date"] = date,
                ["sender_user_name"] = fwd.from_name,
            };
        }

        if (!fwd.flags.HasFlag(MessageFwdHeader.Flags.has_from_id))
        {
            return null;
        }

        switch (fwd.from_id)
        {
            case PeerUser peerUser when users != null
                                          && users.TryGetValue(peerUser.user_id, out var sender)
                                          && sender != null:
                return new Dictionary<string, object>
                {
                    ["type"] = "user",
                    ["date"] = date,
                    ["sender_user"] = BuildUser(sender),
                };

            case PeerChannel peerChannel when chats != null
                                               && chats.TryGetValue(peerChannel.channel_id, out var channel)
                                               && channel != null:
                var channelOrigin = new Dictionary<string, object>
                {
                    ["type"] = "channel",
                    ["date"] = date,
                    ["chat"] = BuildChat(fwd.from_id, chats)!,
                };
                if (fwd.flags.HasFlag(MessageFwdHeader.Flags.has_channel_post))
                {
                    channelOrigin["message_id"] = fwd.channel_post;
                }

                return channelOrigin;

            case PeerChat peerChat when chats != null
                                        && chats.TryGetValue(peerChat.chat_id, out var chat)
                                        && chat != null:
                return new Dictionary<string, object>
                {
                    ["type"] = "chat",
                    ["date"] = date,
                    ["sender_chat"] = BuildChat(fwd.from_id, chats)!,
                };
        }

        return null;
    }

    /// <summary>
    /// Конвертирует MTProto <c>MessageEntity[]</c> в Bot API <c>entities</c>. Смещения переносятся
    /// как есть: и MTProto, и Bot API считают их в UTF-16 code units.
    /// Без Bot API-аналога (unknown, formatted_date, diff_*) сущности пропускаются.
    /// </summary>
    internal static List<Dictionary<string, object>> BuildEntities(
        MessageEntity[] entities,
        Dictionary<long, User> users)
    {
        var list = new List<Dictionary<string, object>>(entities.Length);
        foreach (var e in entities)
        {
            var item = new Dictionary<string, object>
            {
                ["offset"] = e.offset,
                ["length"] = e.length,
            };

            switch (e)
            {
                case MessageEntityBold: item["type"] = "bold"; break;
                case MessageEntityItalic: item["type"] = "italic"; break;
                case MessageEntityUnderline: item["type"] = "underline"; break;
                case MessageEntityStrike: item["type"] = "strikethrough"; break;
                case MessageEntitySpoiler: item["type"] = "spoiler"; break;
                case MessageEntityBlockquote: item["type"] = "blockquote"; break;
                case MessageEntityCode: item["type"] = "code"; break;
                case MessageEntityPre pre:
                    item["type"] = "pre";
                    if (!string.IsNullOrEmpty(pre.language))
                    {
                        item["language"] = pre.language;
                    }

                    break;
                case MessageEntityTextUrl textUrl:
                    item["type"] = "text_link";
                    item["url"] = textUrl.url;
                    break;
                case MessageEntityUrl: item["type"] = "url"; break;
                case MessageEntityEmail: item["type"] = "email"; break;
                case MessageEntityPhone: item["type"] = "phone"; break;
                case MessageEntityBankCard: item["type"] = "bank_card_number"; break;
                case MessageEntityMention: item["type"] = "mention"; break;
                case MessageEntityHashtag: item["type"] = "hashtag"; break;
                case MessageEntityCashtag: item["type"] = "cashtag"; break;
                case MessageEntityBotCommand: item["type"] = "bot_command"; break;
                case MessageEntityMentionName mentionName:
                    // Bot API требует здесь объект user, поэтому без разрешимого User сущность не отдаём.
                    // ReadTLDictionary кладёт null вместо userEmpty, так что одной TryGetValue мало.
                    if (users == null || !users.TryGetValue(mentionName.user_id, out var mentionedUser) || mentionedUser == null)
                    {
                        continue;
                    }

                    item["type"] = "text_mention";
                    item["user"] = BuildUser(mentionedUser);
                    break;
                case MessageEntityCustomEmoji customEmoji:
                    item["type"] = "custom_emoji";
                    item["custom_emoji_id"] = customEmoji.document_id;
                    break;
                default:
                    continue;
            }

            list.Add(item);
        }

        return list;
    }

    /// <summary>Минимальный Bot API chat {id, type} по типу пира; channel различает supergroup по megagroup-флагу.</summary>
    internal static Dictionary<string, object> BuildChat(
        Peer peer,
        IReadOnlyDictionary<long, ChatBase> chats)
    {
        switch (peer)
        {
            case PeerUser pu:
                return new Dictionary<string, object> { ["id"] = pu.user_id, ["type"] = "private" };
            case PeerChat pc:
                return new Dictionary<string, object> { ["id"] = pc.chat_id, ["type"] = "group" };
            case PeerChannel pch:
                var type = "channel";
                if (chats != null && chats.TryGetValue(pch.channel_id, out var c) && c is Channel ch)
                {
                    RememberChannelAccessHash(ch);
                    if (ch.flags.HasFlag(Channel.Flags.megagroup))
                    {
                        type = "supergroup";
                    }
                }

                return new Dictionary<string, object> { ["id"] = pch.channel_id, ["type"] = type };
            default:
                return null;
        }
    }

    /// <summary>
    /// Bot API photo: массив размеров — по записи на каждый <see cref="PhotoSize"/>, как в настоящем Bot API
    /// (у каждого размера свой file_id, т.к. внутрь зашит thumb_size).
    /// Скачиваемые размеры: classic <see cref="PhotoSize"/> и <see cref="PhotoSizeProgressive"/>;
    /// stripped/cached/empty размеры скачать нельзя, они пропускаются.
    /// </summary>
    internal static Dictionary<string, object>[] BuildPhotoSizes(Photo photo)
    {
        if (photo == null)
        {
            return [];
        }

        var uniqueId = BuildFileUniqueId(2, photo.id);
        var result = new List<Dictionary<string, object>>();

        if (photo.sizes != null)
        {
            foreach (var s in photo.sizes)
            {
                string type;
                int width, height, fileSize;
                switch (s)
                {
                    case PhotoSize ps when !string.IsNullOrEmpty(ps.type):
                        type = ps.type;
                        width = ps.w;
                        height = ps.h;
                        fileSize = ps.size;
                        break;
                    case PhotoSizeProgressive psp when !string.IsNullOrEmpty(psp.type):
                        type = psp.type;
                        width = psp.w;
                        height = psp.h;
                        fileSize = psp.sizes is { Length: > 0 } progressive ? progressive[^1] : 0;
                        break;
                    default:
                        continue;
                }

                result.Add(new Dictionary<string, object>
                {
                    ["file_id"] = EncodeInputPhoto(photo.id, photo.access_hash, photo.dc_id, photo.file_reference, type),
                    ["file_unique_id"] = uniqueId,
                    ["width"] = width,
                    ["height"] = height,
                    ["file_size"] = fileSize,
                });
            }
        }

        if (result.Count == 0)
        {
            // Пригодных размеров не нашлось (редкий случай): отдаём file_id без thumb_size.
            result.Add(new Dictionary<string, object>
            {
                ["file_id"] = EncodeInputPhoto(photo.id, photo.access_hash, photo.dc_id, photo.file_reference, string.Empty),
                ["file_unique_id"] = uniqueId,
            });
        }

        return [.. result];
    }

    /// <summary>Bot API document: file_id/file_unique_id/mime_type/file_size (+ file_name из атрибутов).</summary>
    internal static Dictionary<string, object> BuildDocument(Document doc)
    {
        var result = new Dictionary<string, object>
        {
            ["file_id"] = EncodeInputDocument(doc.id, doc.access_hash, doc.dc_id, doc.file_reference),
            ["file_unique_id"] = BuildFileUniqueId(5, doc.id),
            ["mime_type"] = doc.mime_type ?? string.Empty,
            ["file_size"] = doc.size,
        };

        if (doc.attributes != null)
        {
            foreach (var a in doc.attributes)
            {
                if (a is DocumentAttributeFilename fna && !string.IsNullOrEmpty(fna.file_name))
                {
                    result["file_name"] = fna.file_name;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Извлекает отредактированное сообщение из результата messages.editMessage; null — изменений не было.</summary>
    private static Dictionary<string, object> BuildEditedMessage(UpdatesBase result, User self)
    {
        SplitUpdates(result, out var updates, out var users, out var chats);
        if (updates == null)
        {
            return null;
        }

        foreach (var u in updates)
        {
            var message = u switch
            {
                UpdateEditMessage em => em.message,
                UpdateNewMessage nm => nm.message,
                _ => null,
            };

            if (message is Message m && m.id > 0)
            {
                var d = BuildMessage(m, users, chats);
                if (self != null)
                {
                    d["from"] = BuildUser(self);
                }

                return d;
            }
        }

        return null;
    }

    /// <summary>Извлекает пересланное сообщение (UpdateNewMessage/UpdateNewChannelMessage или UpdateMessageID).</summary>
    private static Dictionary<string, object> BuildForwardedMessage(
        UpdatesBase result, InputPeer toPeer, User self)
    {
        SplitUpdates(result, out var updates, out var users, out var chats);
        if (updates != null)
        {
            foreach (var u in updates)
            {
                if (u is UpdateNewMessage nm && nm.message is Message m && m.id > 0)
                {
                    var d = BuildMessage(m, users, chats);
                    if (self != null)
                    {
                        d["from"] = BuildUser(self);
                    }

                    return d;
                }
            }

            foreach (var u in updates)
            {
                if (u is UpdateMessageID um)
                {
                    var d = BuildSimpleMessage(um.id, DateTime.UtcNow, null, self);
                    var chat = BuildSentChat(toPeer, self?.id ?? 0);
                    if (chat != null)
                    {
                        d["chat"] = chat;
                    }

                    return d;
                }
            }
        }

        throw new BotGatewayException(500, "forwardMessages returned no message update");
    }

    /// <summary>Преобразует результат sendMessage (UpdatesBase) в минимальный Bot API message.</summary>
    internal static Dictionary<string, object> BuildSentMessage(
        UpdatesBase result,
        InputPeer target,
        User self,
        string text)
    {
        switch (result)
        {
            case UpdateShortSentMessage ssm:
                {
                    var msg = BuildSimpleMessage(ssm.id, ssm.date, text, self);
                    var chat = BuildSentChat(target, self?.id ?? 0);
                    if (chat != null)
                    {
                        msg["chat"] = chat;
                    }

                    return msg;
                }
            case UpdateShortMessage m:
                {
                    var msg = BuildSimpleMessage(m.id, m.date, m.message, self);
                    msg["chat"] = new Dictionary<string, object> { ["id"] = m.user_id, ["type"] = "private" };
                    return msg;
                }
            case UpdateShortChatMessage m:
                {
                    var msg = BuildSimpleMessage(m.id, m.date, m.message, self);
                    msg["chat"] = new Dictionary<string, object> { ["id"] = m.chat_id, ["type"] = "group" };
                    return msg;
                }
            case Updates u:
                return BuildSentFromUpdates(u.updates, u.users, u.chats, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            case UpdatesCombined c:
                return BuildSentFromUpdates(c.updates, c.users, c.chats, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            case UpdateShort s when s.update is UpdateNewMessage unm:
                return BuildSentFromUpdates([unm], null, null, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            default:
                throw new BotGatewayException(500, $"unsupported sendMessage result type '{result?.GetType().Name}'");
        }
    }

    private static Dictionary<string, object> BuildSentFromUpdates(
        Update[] updates,
        Dictionary<long, User> users,
        Dictionary<long, ChatBase> chats,
        User self)
    {
        if (updates == null)
        {
            return null;
        }

        foreach (var u in updates)
        {
            if (u is UpdateNewMessage unm && unm.message is Message m && m.id > 0)
            {
                var d = BuildMessage(m, users, chats);
                if (self != null)
                {
                    d["from"] = BuildUser(self); // отправитель — сам бот
                }

                return d;
            }
        }
        return null;
    }

    private static Dictionary<string, object> BuildSimpleMessage(
        int id, DateTime date, string text, User self)
    {
        var msg = new Dictionary<string, object>
        {
            ["message_id"] = id,
            ["date"] = new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds(),
        };
        if (!string.IsNullOrEmpty(text))
        {
            msg["text"] = text;
        }

        if (self != null)
        {
            msg["from"] = BuildUser(self);
        }

        return msg;
    }

    /// <summary>Bot API chat {id, type} по InputPeer цели отправки (для sendMessage).</summary>
    internal static Dictionary<string, object> BuildSentChat(InputPeer target, long selfId) => target switch
    {
        InputPeerUser u => new Dictionary<string, object> { ["id"] = u.user_id, ["type"] = "private" },
        InputPeerChat c => new Dictionary<string, object> { ["id"] = c.chat_id, ["type"] = "group" },
        InputPeerChannel ch => new Dictionary<string, object> { ["id"] = ch.channel_id, ["type"] = "channel" },
        InputPeerSelf => new Dictionary<string, object> { ["id"] = selfId, ["type"] = "private" },
        _ => null,
    };
}
