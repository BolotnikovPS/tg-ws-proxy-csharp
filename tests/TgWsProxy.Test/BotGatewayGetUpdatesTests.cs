using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

public class BotGatewayGetUpdatesTests
{
    [Fact]
    public void BuildChat_MapsPeerTypes()
    {
        var priv = BotGateway.BuildChat(new MtProtoTL.PeerUser { user_id = 111 }, null);
        Assert.Equal(111L, (long)priv!["id"]);
        Assert.Equal("private", (string)priv["type"]);

        var group = BotGateway.BuildChat(new MtProtoTL.PeerChat { chat_id = 222 }, null);
        Assert.Equal(222L, (long)group!["id"]);
        Assert.Equal("group", (string)group["type"]);

        var channel = BotGateway.BuildChat(new MtProtoTL.PeerChannel { channel_id = 333 }, null);
        Assert.Equal(333L, (long)channel!["id"]);
        Assert.Equal("channel", (string)channel["type"]);

        Assert.Null(BotGateway.BuildChat(null!, null));
    }

    [Fact]
    public void BuildChat_DistinguishesSupergroupFromChannel()
    {
        var supergroup = new MtProtoTL.Channel
        {
            id = 333,
            flags = MtProtoTL.Channel.Flags.megagroup,
        };
        var chats = new Dictionary<long, MtProtoTL.ChatBase> { [333] = supergroup };
        var peer = new MtProtoTL.PeerChannel { channel_id = 333 };

        var built = BotGateway.BuildChat(peer, chats);
        Assert.Equal("supergroup", (string)built!["type"]);
    }

    [Fact]
    public void BuildChat_ChannelWithoutMegagroupFlag_IsChannel()
    {
        var channel = new MtProtoTL.Channel { id = 444, flags = MtProtoTL.Channel.Flags.broadcast };
        var chats = new Dictionary<long, MtProtoTL.ChatBase> { [444] = channel };
        var peer = new MtProtoTL.PeerChannel { channel_id = 444 };

        var built = BotGateway.BuildChat(peer, chats);
        Assert.Equal("channel", (string)built!["type"]);
    }

    [Fact]
    public void BuildMessage_MapsFromAndChat()
    {
        var sender = new MtProtoTL.User
        {
            id = 22,
            flags = MtProtoTL.User.Flags.bot | MtProtoTL.User.Flags.has_first_name,
            first_name = "Alice",
        };
        var users = new Dictionary<long, MtProtoTL.User> { [22] = sender };

        var msg = new MtProtoTL.Message
        {
            id = 5,
            flags = MtProtoTL.Message.Flags.has_from_id,
            from_id = new MtProtoTL.PeerUser { user_id = 22 },
            peer_id = new MtProtoTL.PeerUser { user_id = 111 },
            date = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            message = "hello",
        };

        var built = BotGateway.BuildMessage(msg, users, null);

        Assert.Equal(5, (int)built["message_id"]);
        Assert.Equal("hello", (string)built["text"]);
        Assert.Equal(1704164645L, (long)built["date"]);

        var from = (Dictionary<string, object>)built["from"];
        Assert.Equal(22L, (long)from["id"]);
        Assert.True((bool)from["is_bot"]);
        Assert.Equal("Alice", (string)from["first_name"]);

        var chat = (Dictionary<string, object>)built["chat"];
        Assert.Equal(111L, (long)chat["id"]);
        Assert.Equal("private", (string)chat["type"]);
    }

    [Fact]
    public void BuildMessage_OmitsFrom_WhenSenderUnknown()
    {
        var msg = new MtProtoTL.Message
        {
            id = 9,
            flags = MtProtoTL.Message.Flags.has_from_id,
            from_id = new MtProtoTL.PeerUser { user_id = 999 },
            peer_id = new MtProtoTL.PeerUser { user_id = 111 },
            date = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            message = "hi",
        };

        var built = BotGateway.BuildMessage(msg, [], null);
        Assert.False(built.ContainsKey("from"));
        Assert.True(built.ContainsKey("chat"));
    }

    [Fact]
    public void BuildUpdates_FiltersByOffset_SkipsEmpty_AndSorts()
    {
        var m2 = new MtProtoTL.Message { id = 2, date = DateTime.UnixEpoch, message = "two", peer_id = null };
        var m5 = new MtProtoTL.Message { id = 5, date = DateTime.UnixEpoch, message = "five", peer_id = null };
        var empty = new MtProtoTL.MessageEmpty { id = 1 };

        var result = BotGateway.BuildUpdates(
            [m5, empty, m2],
            [],
            null,
            offset: 3);

        Assert.Single(result);
        Assert.Equal(5, (int)result[0]["update_id"]);

        var message = (Dictionary<string, object>)result[0]["message"];
        Assert.Equal("five", (string)message["text"]);
    }

    [Fact]
    public void ReadIntArg_ParsesJsonStringAndInt()
    {
        Assert.Equal(0L, BotGateway.ReadIntArg(new Dictionary<string, object>(), "x", 0));

        var json = new Dictionary<string, object> { ["limit"] = JsonSerializer.SerializeToElement(42) };
        Assert.Equal(42L, BotGateway.ReadIntArg(json, "limit", 0));

        var str = new Dictionary<string, object> { ["offset"] = "7" };
        Assert.Equal(7L, BotGateway.ReadIntArg(str, "offset", 0));

        var i = new Dictionary<string, object> { ["limit"] = 9 };
        Assert.Equal(9L, BotGateway.ReadIntArg(i, "limit", 0));

        var junk = new Dictionary<string, object> { ["limit"] = "not-a-number" };
        Assert.Equal(100L, BotGateway.ReadIntArg(junk, "limit", 100));
    }
}
