using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

public class BotGatewaySendMessageTests
{
    private static MtProtoTL.User Self() => new()
    {
        id = 10,
        flags = MtProtoTL.User.Flags.bot | MtProtoTL.User.Flags.has_first_name,
        first_name = "Bot",
    };

    [Fact]
    public void ResolvePeer_MapsUser()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = 123L, ["access_hash"] = 999L };
        var u = Assert.IsType<MtProtoTL.InputPeerUser>(BotGateway.ResolvePeer(args, 1));
        Assert.Equal(123L, u.user_id);
        Assert.Equal(999L, u.access_hash);
    }

    [Fact]
    public void ResolvePeer_MapsSelf()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = 5L };
        Assert.IsType<MtProtoTL.InputPeerSelf>(BotGateway.ResolvePeer(args, 5));
    }

    [Fact]
    public void ResolvePeer_MapsBasicGroup()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = -456L };
        var c = Assert.IsType<MtProtoTL.InputPeerChat>(BotGateway.ResolvePeer(args, 1));
        Assert.Equal(456L, c.chat_id);
    }

    [Fact]
    public void ResolvePeer_MapsChannel()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = -1001234567890L, ["access_hash"] = 777L };
        var ch = Assert.IsType<MtProtoTL.InputPeerChannel>(BotGateway.ResolvePeer(args, 1));
        Assert.Equal(1234567890L, ch.channel_id);
        Assert.Equal(777L, ch.access_hash);
    }

    [Fact]
    public void ResolvePeer_UserWithoutAccessHash_Throws400()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = 123L };
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.ResolvePeer(args, 1));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void ResolvePeer_ChannelWithoutAccessHash_Throws400()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = -1001234567890L };
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.ResolvePeer(args, 1));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void ResolvePeer_NoChatId_Throws400()
    {
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.ResolvePeer(new Dictionary<string, object>(), 1));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void ResolvePeer_ParsesRawPeerJson()
    {
        var peerJson = JsonSerializer.SerializeToElement(new { _ = "inputPeerUser", user_id = 123L, access_hash = 456L });
        var args = new Dictionary<string, object> { ["peer"] = peerJson };

        var u = Assert.IsType<MtProtoTL.InputPeerUser>(BotGateway.ResolvePeer(args, 1));
        Assert.Equal(123L, u.user_id);
        Assert.Equal(456L, u.access_hash);
    }

    [Fact]
    public void BuildSentMessage_ShortSentMessage_UsesTextAndTargetChat()
    {
        var result = new MtProtoTL.UpdateShortSentMessage
        {
            id = 77,
            date = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        var target = new MtProtoTL.InputPeerUser { user_id = 555, access_hash = 1 };

        var msg = BotGateway.BuildSentMessage(result, target, Self(), "hello world");

        Assert.Equal(77, (int)msg["message_id"]);
        Assert.Equal("hello world", (string)msg["text"]);
        Assert.Equal(1704164645L, (long)msg["date"]);

        Assert.Equal(10L, (long)((Dictionary<string, object>)msg["from"])["id"]);
        var chat = (Dictionary<string, object>)msg["chat"];
        Assert.Equal(555L, (long)chat["id"]);
        Assert.Equal("private", (string)chat["type"]);
    }

    [Fact]
    public void BuildSentMessage_ShortMessage_UsesMessageAndPrivateChat()
    {
        var result = new MtProtoTL.UpdateShortMessage
        {
            id = 88,
            user_id = 999,
            message = "hi",
            date = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };

        var msg = BotGateway.BuildSentMessage(result, new MtProtoTL.InputPeerUser { user_id = 999, access_hash = 1 }, Self(), "ignored");

        Assert.Equal(88, (int)msg["message_id"]);
        Assert.Equal("hi", (string)msg["text"]);
        var chat = (Dictionary<string, object>)msg["chat"];
        Assert.Equal(999L, (long)chat["id"]);
        Assert.Equal("private", (string)chat["type"]);
    }

    [Fact]
    public void BuildSentMessage_Updates_ExtractsNewMessage_ForcesFromSelf()
    {
        var sent = new MtProtoTL.Message
        {
            id = 99,
            peer_id = new MtProtoTL.PeerUser { user_id = 111 },
            date = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            message = "sent",
        };
        var result = new MtProtoTL.Updates
        {
            updates = [new MtProtoTL.UpdateNewMessage { message = sent }],
            users = [],
            chats = [],
        };

        var built = BotGateway.BuildSentMessage(result, new MtProtoTL.InputPeerUser { user_id = 111, access_hash = 1 }, Self(), "sent");

        Assert.Equal(99, (int)built["message_id"]);
        Assert.Equal("sent", (string)built["text"]);
        Assert.Equal(10L, (long)((Dictionary<string, object>)built["from"])["id"]);
        var chat = (Dictionary<string, object>)built["chat"];
        Assert.Equal(111L, (long)chat["id"]);
        Assert.Equal("private", (string)chat["type"]);
    }

    [Fact]
    public void BuildSentMessage_UnsupportedResult_Throws500()
    {
        var result = new MtProtoTL.UpdateShort
        {
            update = new MtProtoTL.UpdateUserTyping(),
            date = DateTime.UtcNow,
        };

        var ex = Assert.Throws<BotGatewayException>(() =>
            BotGateway.BuildSentMessage(result, new MtProtoTL.InputPeerUser { user_id = 1, access_hash = 1 }, null, "x"));
        Assert.Equal(500, ex.ErrorCode);
    }

    [Fact]
    public void FilterAllowedUpdates_KeepsMessageWhenAllowed()
    {
        var updates = new List<Dictionary<string, object>>
        {
            new() { ["update_id"] = 1, ["message"] = new Dictionary<string, object>() },
        };

        Assert.Single(BotGateway.FilterAllowedUpdates(updates, ["message"]));
        Assert.Empty(BotGateway.FilterAllowedUpdates(updates, ["edited_message"]));
        Assert.Single(BotGateway.FilterAllowedUpdates(updates, null));
        Assert.Single(BotGateway.FilterAllowedUpdates(updates, []));
    }

    [Fact]
    public void ReadStringListArg_ParsesJsonArrayAndCsv()
    {
        Assert.Null(BotGateway.ReadStringListArg(new Dictionary<string, object>(), "allowed_updates"));

        var json = JsonSerializer.SerializeToElement(new[] { "message", "edited_message" });
        var arr = BotGateway.ReadStringListArg(
            new Dictionary<string, object> { ["allowed_updates"] = json }, "allowed_updates");
        Assert.Equal(new[] { "message", "edited_message" }, arr);

        var csv = BotGateway.ReadStringListArg(
            new Dictionary<string, object> { ["allowed_updates"] = "message, channel_post" }, "allowed_updates");
        Assert.Equal(new[] { "message", "channel_post" }, csv);
    }
}
