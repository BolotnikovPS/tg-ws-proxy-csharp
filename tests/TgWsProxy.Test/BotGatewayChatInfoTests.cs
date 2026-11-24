using System.Collections.Generic;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// P2.7/P2.8: поля, которые Bot API отдаёт только в getMe (BotFullInfo: флаги бота,
/// обязательные boolean и опциональные active_usernames/inline_query_placeholder) и в getChat
/// (ChatFullInfo: description, invite_link, permissions, is_forum, has_protected_content,
/// has_hidden_members, has_aggressive_anti_spam_enabled, slow_mode_delay, linked_chat_id).
/// </summary>
public class BotGatewayChatInfoTests
{
    [Fact]
    public void BuildBotFullInfo_MapsAllTrueFlags()
    {
        var user = new MtProtoTL.User
        {
            id = 42,
            flags = MtProtoTL.User.Flags.bot
                | MtProtoTL.User.Flags.has_first_name
                | MtProtoTL.User.Flags.has_last_name
                | MtProtoTL.User.Flags.has_username
                | MtProtoTL.User.Flags.has_lang_code
                | MtProtoTL.User.Flags.bot_chat_history
                | MtProtoTL.User.Flags.has_bot_inline_placeholder
                | MtProtoTL.User.Flags.bot_inline_geo,
            flags2 = MtProtoTL.User.Flags2.has_usernames
                | MtProtoTL.User.Flags2.bot_business
                | MtProtoTL.User.Flags2.bot_has_main_app
                | MtProtoTL.User.Flags2.bot_forum_view
                | MtProtoTL.User.Flags2.bot_forum_can_manage_topics
                | MtProtoTL.User.Flags2.bot_can_manage_bots
                | MtProtoTL.User.Flags2.bot_guestchat,
            first_name = "Bot",
            last_name = "Botov",
            username = "mybot",
            lang_code = "ru",
            bot_inline_placeholder = "search…",
            usernames =
            [
                new MtProtoTL.Username { flags = MtProtoTL.Username.Flags.active, username = "mybot" },
                new MtProtoTL.Username { flags = MtProtoTL.Username.Flags.active, username = "mybot_alt" },
                new MtProtoTL.Username { flags = 0, username = "inactive_one" },
            ],
        };

        var dto = BotGateway.BuildBotFullInfo(user);

        // База BuildUser
        Assert.Equal(42L, dto["id"]);
        Assert.Equal(true, dto["is_bot"]);
        Assert.Equal("Bot", dto["first_name"]);
        Assert.Equal("Botov", dto["last_name"]);
        Assert.Equal("mybot", dto["username"]);
        Assert.Equal("ru", dto["language_code"]);
        // 12 обязательных boolean — все true
        Assert.True((bool)dto["can_join_groups"]); // bot_nochats не выставлен → можно добавлять в группы
        Assert.True((bool)dto["can_read_all_group_messages"]);
        Assert.True((bool)dto["supports_inline_queries"]);
        Assert.True((bool)dto["can_connect_to_business"]);
        Assert.True((bool)dto["has_main_web_app"]);
        Assert.True((bool)dto["has_topics_enabled"]);
        Assert.True((bool)dto["allows_users_to_create_topics"]);
        Assert.True((bool)dto["can_manage_bots"]);
        Assert.True((bool)dto["supports_guest_queries"]);
        Assert.True((bool)dto["needs_inline_query_location"]);
        Assert.False((bool)dto["supports_join_request_queries"]);
        Assert.False((bool)dto["has_topic_sidebar"]);
        // Опциональные — только активные usernames и непустой placeholder
        Assert.Equal(new[] { "mybot", "mybot_alt" }, Assert.IsType<string[]>(dto["active_usernames"]));
        Assert.Equal("search…", dto["inline_query_placeholder"]);
    }

    [Fact]
    public void BuildBotFullInfo_InvertsBotNochats_AndLeavesOtherFlagsFalse()
    {
        var user = new MtProtoTL.User { id = 7, flags = MtProtoTL.User.Flags.bot_nochats };
        var dto = BotGateway.BuildBotFullInfo(user);

        // is_bot в BotFullInfo имеет тип True — true, даже если флаг bot не выставлен
        Assert.Equal(7L, dto["id"]);
        Assert.Equal(true, dto["is_bot"]);
        Assert.False((bool)dto["can_join_groups"]);
        Assert.False((bool)dto["can_read_all_group_messages"]);
        Assert.False((bool)dto["supports_inline_queries"]);
        Assert.False((bool)dto["can_connect_to_business"]);
        Assert.False((bool)dto["has_main_web_app"]);
        Assert.False((bool)dto["has_topics_enabled"]);
        Assert.False((bool)dto["allows_users_to_create_topics"]);
        Assert.False((bool)dto["can_manage_bots"]);
        Assert.False((bool)dto["supports_guest_queries"]);
        Assert.False((bool)dto["needs_inline_query_location"]);
        Assert.False((bool)dto["supports_join_request_queries"]);
        Assert.False((bool)dto["has_topic_sidebar"]);
        // Опциональные при отсутствии данных не эмитятся вообще
        Assert.False(dto.ContainsKey("active_usernames"));
        Assert.False(dto.ContainsKey("inline_query_placeholder"));
    }

    [Fact]
    public void BuildBotFullInfo_EmitsRequiredBooleans_EvenWithoutOptionalData()
    {
        var user = new MtProtoTL.User
        {
            id = 9,
            flags = MtProtoTL.User.Flags.has_bot_inline_placeholder,
            flags2 = MtProtoTL.User.Flags2.has_usernames,
            bot_inline_placeholder = "",
            usernames = Array.Empty<MtProtoTL.Username>(),
        };

        var dto = BotGateway.BuildBotFullInfo(user);

        // 12 обязательных boolean всё ещё на месте (is_bot считается отдельно)
        var requiredBooleans = new[]
        {
            "can_join_groups", "can_read_all_group_messages", "supports_inline_queries",
            "can_connect_to_business", "has_main_web_app", "has_topics_enabled",
            "allows_users_to_create_topics", "can_manage_bots", "supports_guest_queries",
            "needs_inline_query_location", "supports_join_request_queries", "has_topic_sidebar",
        };
        Assert.All(requiredBooleans, key => Assert.IsType<bool>(dto[key]));
        Assert.False((bool)dto["supports_join_request_queries"]);
        Assert.False((bool)dto["has_topic_sidebar"]);
        // Пустые/отсутствующие данные → ключей нет
        Assert.False(dto.ContainsKey("active_usernames"));
        Assert.False(dto.ContainsKey("inline_query_placeholder"));
    }

    [Fact]
    public void BuildBotFullInfo_OmitsActiveUsernames_WhenNoneActive()
    {
        var user = new MtProtoTL.User
        {
            id = 11,
            flags = MtProtoTL.User.Flags.bot,
            flags2 = MtProtoTL.User.Flags2.has_usernames,
            usernames =
            [
                new MtProtoTL.Username { flags = MtProtoTL.Username.Flags.editable, username = "first" },
                new MtProtoTL.Username { flags = 0, username = "second" },
            ],
        };

        var dto = BotGateway.BuildBotFullInfo(user);

        Assert.False(dto.ContainsKey("active_usernames"));
        Assert.False(dto.ContainsKey("inline_query_placeholder"));
    }

    [Fact]
    public void BuildBotFullInfo_MapsEachNewLayer229FlagToItsOwnField()
    {
        var cases = new (MtProtoTL.User.Flags2 flag2, string field)[]
        {
            (MtProtoTL.User.Flags2.bot_forum_view, "has_topics_enabled"),
            (MtProtoTL.User.Flags2.bot_forum_can_manage_topics, "allows_users_to_create_topics"),
            (MtProtoTL.User.Flags2.bot_can_manage_bots, "can_manage_bots"),
            (MtProtoTL.User.Flags2.bot_guestchat, "supports_guest_queries"),
        };

        foreach (var (flag2, field) in cases)
        {
            var dto = BotGateway.BuildBotFullInfo(new MtProtoTL.User
            {
                id = 1,
                flags = MtProtoTL.User.Flags.bot,
                flags2 = flag2,
            });

            Assert.True((bool)dto[field], $"{flag2} должен маппиться в {field}");
            foreach (var other in cases.Where(x => x.field != field))
            {
                Assert.False((bool)dto[other.field], $"{flag2} не должен влиять на {other.field}");
            }
        }

        // needs_inline_query_location берётся из flags.bot_inline_geo
        var geo = BotGateway.BuildBotFullInfo(new MtProtoTL.User
        {
            id = 1,
            flags = MtProtoTL.User.Flags.bot | MtProtoTL.User.Flags.bot_inline_geo,
        });
        Assert.True((bool)geo["needs_inline_query_location"]);
    }

    [Fact]
    public void BuildChatPermissions_NoRights_AllowsEverything()
    {
        var permissions = BotGateway.BuildChatPermissions(new MtProtoTL.ChatBannedRights { flags = 0 });

        Assert.All(permissions.Values, value => Assert.True((bool)value));
        Assert.Equal(14, permissions.Count);
    }

    [Fact]
    public void BuildChatPermissions_LegacySendMedia_BlocksAllMediaOnly()
    {
        var permissions = BotGateway.BuildChatPermissions(
            new MtProtoTL.ChatBannedRights { flags = MtProtoTL.ChatBannedRights.Flags.send_media });

        Assert.True((bool)permissions["can_send_messages"]);
        Assert.False((bool)permissions["can_send_audios"]);
        Assert.False((bool)permissions["can_send_documents"]);
        Assert.False((bool)permissions["can_send_photos"]);
        Assert.False((bool)permissions["can_send_videos"]);
        Assert.False((bool)permissions["can_send_video_notes"]);
        Assert.False((bool)permissions["can_send_voice_notes"]);
        Assert.False((bool)permissions["can_send_polls"]);
        Assert.False((bool)permissions["can_send_other_messages"]);
        Assert.True((bool)permissions["can_change_info"]);
    }

    [Fact]
    public void BuildChatPermissions_TextAndAdminRights_AreInverted()
    {
        var flags = MtProtoTL.ChatBannedRights.Flags.send_messages
            | MtProtoTL.ChatBannedRights.Flags.embed_links
            | MtProtoTL.ChatBannedRights.Flags.change_info
            | MtProtoTL.ChatBannedRights.Flags.invite_users
            | MtProtoTL.ChatBannedRights.Flags.pin_messages
            | MtProtoTL.ChatBannedRights.Flags.manage_topics;

        var permissions = BotGateway.BuildChatPermissions(new MtProtoTL.ChatBannedRights { flags = flags });

        Assert.False((bool)permissions["can_send_messages"]);
        Assert.False((bool)permissions["can_add_web_page_previews"]);
        Assert.False((bool)permissions["can_change_info"]);
        Assert.False((bool)permissions["can_invite_users"]);
        Assert.False((bool)permissions["can_pin_messages"]);
        Assert.False((bool)permissions["can_manage_topics"]);
        // send_messages — легаси-бит «любые сообщения»; отдельные медиа-типы он в Bot API не трогает
        Assert.True((bool)permissions["can_send_photos"]);
    }

    [Fact]
    public void MergeChannelFullInfo_FillsOptionalChatFullInfoFields()
    {
        var dto = new Dictionary<string, object> { ["id"] = -1000000001234L, ["type"] = "supergroup" };
        var channel = new MtProtoTL.Channel
        {
            id = 1234,
            title = "su",
            flags = MtProtoTL.Channel.Flags.megagroup
                | MtProtoTL.Channel.Flags.forum
                | MtProtoTL.Channel.Flags.noforwards,
            default_banned_rights = new MtProtoTL.ChatBannedRights
            {
                flags = MtProtoTL.ChatBannedRights.Flags.send_polls,
            },
        };
        var full = new MtProtoTL.ChannelFull
        {
            id = 1234,
            about = "about text",
            flags = MtProtoTL.ChannelFull.Flags.has_slowmode_seconds
                | MtProtoTL.ChannelFull.Flags.has_linked_chat_id,
            flags2 = MtProtoTL.ChannelFull.Flags2.participants_hidden
                | MtProtoTL.ChannelFull.Flags2.antispam,
            slowmode_seconds = 30,
            linked_chat_id = 999,
            exported_invite = new MtProtoTL.ChatInviteExported { link = "https://t.me/+abc" },
        };

        BotGateway.MergeChannelFullInfo(dto, channel, full);

        Assert.Equal("about text", dto["description"]);
        Assert.Equal("https://t.me/+abc", dto["invite_link"]);
        Assert.True((bool)dto["is_forum"]);
        Assert.True((bool)dto["has_protected_content"]);
        Assert.True((bool)dto["has_hidden_members"]);
        Assert.True((bool)dto["has_aggressive_anti_spam_enabled"]);
        Assert.Equal(30, dto["slow_mode_delay"]);
        Assert.Equal(-1000000000000L - 999, dto["linked_chat_id"]);
        var permissions = Assert.IsType<Dictionary<string, object>>(dto["permissions"]);
        Assert.False((bool)permissions["can_send_polls"]);
        Assert.True((bool)permissions["can_send_messages"]);
    }

    [Fact]
    public void MergeChannelFullInfo_OmitsUnsetOptionalFields()
    {
        var dto = new Dictionary<string, object>();
        var channel = new MtProtoTL.Channel { id = 5, title = "plain", flags = MtProtoTL.Channel.Flags.megagroup };
        var full = new MtProtoTL.ChannelFull { id = 5 };

        BotGateway.MergeChannelFullInfo(dto, channel, full);

        Assert.False(dto.ContainsKey("description"));
        Assert.False(dto.ContainsKey("invite_link"));
        Assert.False(dto.ContainsKey("is_forum"));
        Assert.False(dto.ContainsKey("has_protected_content"));
        Assert.False(dto.ContainsKey("has_hidden_members"));
        Assert.False(dto.ContainsKey("has_aggressive_anti_spam_enabled"));
        Assert.False(dto.ContainsKey("slow_mode_delay"));
        Assert.False(dto.ContainsKey("linked_chat_id"));
    }

    [Fact]
    public void MergeChannelFullInfo_IgnoresMissingFullChat()
    {
        var dto = new Dictionary<string, object> { ["id"] = -1000000001234L };
        BotGateway.MergeChannelFullInfo(dto, new MtProtoTL.Channel { id = 1234 }, null);
        Assert.Single(dto);
    }

    [Fact]
    public void MergeChatFullInfo_FillsDescriptionInviteAndPermissions()
    {
        var dto = new Dictionary<string, object> { ["id"] = -55L, ["type"] = "group" };
        var chat = new MtProtoTL.Chat
        {
            id = 55,
            title = "grp",
            participants_count = 3,
            default_banned_rights = new MtProtoTL.ChatBannedRights
            {
                flags = MtProtoTL.ChatBannedRights.Flags.invite_users,
            },
        };
        var full = new MtProtoTL.ChatFull
        {
            id = 55,
            about = "group about",
            exported_invite = new MtProtoTL.ChatInviteExported { link = "https://t.me/+grp" },
        };

        BotGateway.MergeChatFullInfo(dto, chat, full);

        Assert.Equal("group about", dto["description"]);
        Assert.Equal("https://t.me/+grp", dto["invite_link"]);
        var permissions = Assert.IsType<Dictionary<string, object>>(dto["permissions"]);
        Assert.False((bool)permissions["can_invite_users"]);
        Assert.True((bool)permissions["can_send_messages"]);
    }

    [Fact]
    public void MergeChatFullInfo_SkipsInvite_WhenPublicJoinRequests()
    {
        var dto = new Dictionary<string, object>();
        var full = new MtProtoTL.ChatFull
        {
            id = 55,
            about = null,
            exported_invite = new MtProtoTL.ChatInvitePublicJoinRequests(),
        };

        BotGateway.MergeChatFullInfo(dto, null, full);

        Assert.Empty(dto);
    }
}
