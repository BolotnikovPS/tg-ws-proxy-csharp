using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Поля Bot API message, которые BuildMessage выводит из MessageReplyHeader и MessageFwdHeader:
/// reply_to_message, message_thread_id, is_topic_message, quote, forward_origin, edit_date, views.
/// </summary>
public class BotGatewayMessageFieldsTests
{
    private static MtProtoTL.Message NewMessage()
    {
        return new MtProtoTL.Message
        {
            id = 10,
            peer_id = new MtProtoTL.PeerUser { user_id = 20 },
            date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            message = "hello",
        };
    }

    [Fact]
    public void BuildMessage_MapsReplyToMessage()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_to;
        msg.reply_to = new MtProtoTL.MessageReplyHeader
        {
            flags = MtProtoTL.MessageReplyHeader.Flags.has_reply_to_msg_id,
            reply_to_msg_id = 7,
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        var reply = Assert.IsType<Dictionary<string, object>>(built["reply_to_message"]);
        Assert.Equal(7, (int)reply["message_id"]);
        Assert.Equal(7, (int)built["reply_to_message_id"]);

        var chat = Assert.IsType<Dictionary<string, object>>(reply["chat"]);
        Assert.Equal(20L, (long)chat["id"]);
        Assert.Equal("private", (string)chat["type"]);

        // Ответ не в треде — топиковых полей быть не должно.
        Assert.False(built.ContainsKey("message_thread_id"));
        Assert.False(built.ContainsKey("is_topic_message"));
    }

    [Fact]
    public void BuildMessage_MapsTopicThread()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_to;
        msg.reply_to = new MtProtoTL.MessageReplyHeader
        {
            flags = MtProtoTL.MessageReplyHeader.Flags.has_reply_to_top_id
                    | MtProtoTL.MessageReplyHeader.Flags.forum_topic,
            reply_to_top_id = 42,
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal(42, (int)built["message_thread_id"]);
        Assert.True((bool)built["is_topic_message"]);
        // reply_to_msg_id отсутствует — самого reply_to_message тоже быть не должно.
        Assert.False(built.ContainsKey("reply_to_message"));
    }

    [Fact]
    public void BuildMessage_MapsQuote()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_to;
        msg.reply_to = new MtProtoTL.MessageReplyHeader
        {
            flags = MtProtoTL.MessageReplyHeader.Flags.has_reply_to_msg_id
                    | MtProtoTL.MessageReplyHeader.Flags.has_quote_text
                    | MtProtoTL.MessageReplyHeader.Flags.has_quote_entities
                    | MtProtoTL.MessageReplyHeader.Flags.has_quote_offset,
            reply_to_msg_id = 7,
            quote_text = "quoted",
            quote_entities = [new MtProtoTL.MessageEntityBold { offset = 0, length = 6 }],
            quote_offset = 3,
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        var quote = Assert.IsType<Dictionary<string, object>>(built["quote"]);
        Assert.Equal("quoted", (string)quote["text"]);
        Assert.Equal(3, (int)quote["position"]);

        var entities = Assert.IsType<List<Dictionary<string, object>>>(quote["entities"]);
        var bold = Assert.Single(entities);
        Assert.Equal("bold", (string)bold["type"]);
        Assert.Equal(6, (int)bold["length"]);
    }

    [Fact]
    public void BuildMessage_MapsForwardOriginFromUser()
    {
        var sender = new MtProtoTL.User
        {
            id = 77,
            flags = MtProtoTL.User.Flags.has_first_name,
            first_name = "Carol",
        };
        var users = new Dictionary<long, MtProtoTL.User> { [77] = sender };

        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_fwd_from;
        msg.fwd_from = new MtProtoTL.MessageFwdHeader
        {
            flags = MtProtoTL.MessageFwdHeader.Flags.has_from_id,
            from_id = new MtProtoTL.PeerUser { user_id = 77 },
            date = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        var built = BotGateway.BuildMessage(msg, users, null);

        var origin = Assert.IsType<Dictionary<string, object>>(built["forward_origin"]);
        Assert.Equal("user", (string)origin["type"]);
        Assert.Equal(
            new DateTimeOffset(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            (long)origin["date"]);

        var senderUser = Assert.IsType<Dictionary<string, object>>(origin["sender_user"]);
        Assert.Equal(77L, (long)senderUser["id"]);
        Assert.Equal("Carol", (string)senderUser["first_name"]);
    }

    [Fact]
    public void BuildMessage_MapsForwardOriginHiddenUser()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_fwd_from;
        msg.fwd_from = new MtProtoTL.MessageFwdHeader
        {
            flags = MtProtoTL.MessageFwdHeader.Flags.has_from_name,
            from_name = "Anonymous",
            date = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        var origin = Assert.IsType<Dictionary<string, object>>(built["forward_origin"]);
        Assert.Equal("hidden_user", (string)origin["type"]);
        Assert.Equal("Anonymous", (string)origin["sender_user_name"]);
        Assert.False(origin.ContainsKey("sender_user"));
    }

    [Fact]
    public void BuildMessage_MapsForwardOriginFromChannel()
    {
        var channel = new MtProtoTL.Channel { id = 555, flags = MtProtoTL.Channel.Flags.broadcast };
        var chats = new Dictionary<long, MtProtoTL.ChatBase> { [555] = channel };

        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_fwd_from;
        msg.fwd_from = new MtProtoTL.MessageFwdHeader
        {
            flags = MtProtoTL.MessageFwdHeader.Flags.has_from_id
                    | MtProtoTL.MessageFwdHeader.Flags.has_channel_post,
            from_id = new MtProtoTL.PeerChannel { channel_id = 555 },
            channel_post = 900,
            date = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        var built = BotGateway.BuildMessage(msg, null!, chats);

        var origin = Assert.IsType<Dictionary<string, object>>(built["forward_origin"]);
        Assert.Equal("channel", (string)origin["type"]);
        Assert.Equal(900, (int)origin["message_id"]);

        var chat = Assert.IsType<Dictionary<string, object>>(origin["chat"]);
        Assert.Equal(555L, (long)chat["id"]);
        Assert.Equal("channel", (string)chat["type"]);
    }

    [Fact]
    public void BuildMessage_OmitsForwardOrigin_WhenSenderUnresolvable()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_fwd_from;
        msg.fwd_from = new MtProtoTL.MessageFwdHeader
        {
            flags = MtProtoTL.MessageFwdHeader.Flags.has_from_id,
            from_id = new MtProtoTL.PeerUser { user_id = 999 },
            date = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        var built = BotGateway.BuildMessage(msg, new Dictionary<long, MtProtoTL.User>(), null);

        Assert.False(built.ContainsKey("forward_origin"));
    }

    [Fact]
    public void BuildMessage_MapsEditDateAndViews()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_edit_date | MtProtoTL.Message.Flags.has_views;
        msg.edit_date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        msg.views = 1234;

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal(
            new DateTimeOffset(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            (long)built["edit_date"]);
        Assert.Equal(1234, (int)built["views"]);
    }

    [Fact]
    public void BuildMessage_OmitsEditDateAndViews_WhenFlagsAbsent()
    {
        var built = BotGateway.BuildMessage(NewMessage(), null!, null);

        Assert.False(built.ContainsKey("edit_date"));
        Assert.False(built.ContainsKey("views"));
    }

    [Fact]
    public void BuildMessage_IgnoresNonHeaderReplyKind()
    {
        // Ответ на историю (messageReplyStoryHeader) не является messageReplyHeader —
        // таких полей в Bot API нет, поэтому reply_to_message не отдаём.
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_to;
        msg.reply_to = new MtProtoTL.MessageReplyStoryHeader { peer = new MtProtoTL.PeerUser { user_id = 20 } };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.False(built.ContainsKey("reply_to_message"));
        Assert.False(built.ContainsKey("message_thread_id"));
    }

    [Fact]
    public void BuildMessage_MapsMediaGroupId_FromGroupedId()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_grouped_id;
        msg.grouped_id = 5_123_456_789_012L;

        var built = BotGateway.BuildMessage(msg, null!, null);

        // Bot API отдаёт media_group_id строкой; по ней клиент склеивает альбом.
        Assert.Equal("5123456789012", Assert.IsType<string>(built["media_group_id"]));
    }

    [Fact]
    public void BuildMessage_OmitsMediaGroupId_WhenGroupedIdEmpty()
    {
        var msg = NewMessage();

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.False(built.ContainsKey("media_group_id"));
    }

    [Fact]
    public void BuildMessage_MapsInlineKeyboard()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_markup;
        msg.reply_markup = new MtProtoTL.ReplyInlineMarkup
        {
            flags = 0,
            rows =
            [
                new MtProtoTL.KeyboardInlineButtonRow
                {
                    buttons =
                    [
                        new MtProtoTL.KeyboardInlineButton
                        {
                            flags = 0,
                            text = "press",
                            type = new MtProtoTL.InlineButtonTypeCallback
                            {
                                flags = 0,
                                data = System.Text.Encoding.UTF8.GetBytes("cb_test_1"),
                            },
                        },
                        new MtProtoTL.KeyboardInlineButton
                        {
                            flags = 0,
                            text = "site",
                            type = new MtProtoTL.InlineButtonTypeUrl { url = "https://example.org" },
                        },
                    ],
                },
            ],
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        var markup = Assert.IsType<Dictionary<string, object>>(built["reply_markup"]);
        var rows = Assert.IsType<List<object>>(markup["inline_keyboard"]);
        var row = Assert.IsType<List<object>>(Assert.Single(rows));
        Assert.Equal(2, row.Count);

        var callback = Assert.IsType<Dictionary<string, object>>(row[0]);
        Assert.Equal("press", callback["text"]);
        Assert.Equal("cb_test_1", callback["callback_data"]);

        var url = Assert.IsType<Dictionary<string, object>>(row[1]);
        Assert.Equal("site", url["text"]);
        Assert.Equal("https://example.org", url["url"]);
    }

    [Fact]
    public void BuildMessage_MapsForceReplyFlagOfInlineMarkup()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_reply_markup;
        msg.reply_markup = new MtProtoTL.ReplyInlineMarkup
        {
            flags = MtProtoTL.ReplyInlineMarkup.Flags.force_reply,
            rows = [],
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        var markup = Assert.IsType<Dictionary<string, object>>(built["reply_markup"]);
        Assert.Equal(true, markup["force_reply"]);
        Assert.Empty(Assert.IsType<List<object>>(markup["inline_keyboard"]));
    }

    [Fact]
    public void BuildMessage_OmitsReplyMarkup_WhenAbsent()
    {
        var msg = NewMessage();

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.False(built.ContainsKey("reply_markup"));
    }

    [Fact]
    public void BuildMessage_LinkPreview_IsTextNotCaption()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_media | MtProtoTL.Message.Flags.has_entities;
        msg.entities = [new MtProtoTL.MessageEntityBold { offset = 0, length = 5 }];
        msg.media = new MtProtoTL.MessageMediaWebPage
        {
            webpage = new MtProtoTL.WebPage { url = "https://example.org/" },
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal("hello", built["text"]);
        Assert.False(built.ContainsKey("caption"));
        Assert.True(built.ContainsKey("entities"));
        Assert.False(built.ContainsKey("caption_entities"));

        var options = Assert.IsType<Dictionary<string, object>>(built["link_preview_options"]);
        Assert.Equal(false, options["is_disabled"]);
        Assert.Equal("https://example.org/", options["url"]);
    }

    [Fact]
    public void BuildMessage_LinkPreviewDisabled_WhenWebPageEmpty()
    {
        var msg = NewMessage();
        msg.flags = MtProtoTL.Message.Flags.has_media;
        msg.media = new MtProtoTL.MessageMediaWebPage { webpage = new MtProtoTL.WebPageEmpty() };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal("hello", built["text"]);
        var options = Assert.IsType<Dictionary<string, object>>(built["link_preview_options"]);
        Assert.Equal(true, options["is_disabled"]);
    }
}
