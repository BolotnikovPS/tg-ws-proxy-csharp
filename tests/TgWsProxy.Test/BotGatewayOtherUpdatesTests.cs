using System.Text;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

public class BotGatewayOtherUpdatesTests
{
    private static MtProtoTL.User User(long id, string firstName) => new()
    {
        id = id,
        flags = MtProtoTL.User.Flags.bot | MtProtoTL.User.Flags.has_first_name,
        first_name = firstName,
    };

    [Fact]
    public void BuildOtherUpdates_MapsCallbackQuery()
    {
        var cb = new MtProtoTL.UpdateBotCallbackQuery
        {
            query_id = 9876543210123,
            user_id = 10,
            peer = new MtProtoTL.PeerUser { user_id = 10 },
            msg_id = 77,
            chat_instance = -555,
            flags = MtProtoTL.UpdateBotCallbackQuery.Flags.has_data,
            data = Encoding.UTF8.GetBytes("btn"),
        };

        var updates = BotGateway.BuildOtherUpdates([cb], null, null);

        var query = Assert.Single(updates)["callback_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.Equal("9876543210123", (string)query["id"]);
        Assert.Equal("-555", (string)query["chat_instance"]);
        Assert.Equal("btn", (string)query["data"]);

        var message = query["message"] as Dictionary<string, object>;
        Assert.NotNull(message);
        Assert.Equal(77, (int)message["message_id"]);
        Assert.Equal(10L, (long)((Dictionary<string, object>)message["chat"])["id"]);
    }

    [Fact]
    public void BuildOtherUpdates_KeepsCallbackWithoutDataWhenFlagUnset()
    {
        var cb = new MtProtoTL.UpdateBotCallbackQuery
        {
            query_id = 1,
            user_id = 10,
            peer = new MtProtoTL.PeerChannel { channel_id = 42 },
            msg_id = 5,
            chat_instance = 0,
        };

        var updates = BotGateway.BuildOtherUpdates([cb], null, null);

        var query = Assert.Single(updates)["callback_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.False(query.ContainsKey("data"));
        Assert.False(query.ContainsKey("game_short_name"));
    }

    [Fact]
    public void BuildOtherUpdates_OmitsMessageForUnresolvablePeer()
    {
        // Кнопка на inline-сообщении: пир не разрешается, но Bot API допускает callback_query без message.
        var cb = new MtProtoTL.UpdateBotCallbackQuery
        {
            query_id = 1,
            user_id = 10,
            peer = null!,
            msg_id = 5,
            chat_instance = 0,
        };

        var updates = BotGateway.BuildOtherUpdates([cb], null, null);

        var query = Assert.Single(updates)["callback_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.False(query.ContainsKey("message"));
    }

    [Fact]
    public void BuildOtherUpdates_MapsInlineQuery()
    {
        var iq = new MtProtoTL.UpdateBotInlineQuery
        {
            query_id = 42,
            user_id = 10,
            query = "cats",
            offset = "20",
        };
        var users = new Dictionary<long, MtProtoTL.User> { [10] = User(10, "bot") };

        var updates = BotGateway.BuildOtherUpdates([iq], users, null);

        var query = Assert.Single(updates)["inline_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.Equal("42", (string)query["id"]);
        Assert.Equal("cats", (string)query["query"]);
        Assert.Equal("20", (string)query["offset"]);
        Assert.Equal(10L, (long)((Dictionary<string, object>)query["from"])["id"]);
    }

    [Fact]
    public void BuildOtherUpdates_MapsInlineQueryLocation()
    {
        var iq = new MtProtoTL.UpdateBotInlineQuery
        {
            query_id = 42,
            user_id = 10,
            query = "pizza",
            offset = string.Empty,
            flags = MtProtoTL.UpdateBotInlineQuery.Flags.has_geo,
            geo = new MtProtoTL.GeoPoint { lon = 30.5, lat = 50.25 },
        };

        var updates = BotGateway.BuildOtherUpdates([iq], null, null);

        var query = Assert.Single(updates)["inline_query"] as Dictionary<string, object>;
        var location = query!["location"] as Dictionary<string, object>;
        Assert.NotNull(location);
        Assert.Equal(30.5, (double)location["longitude"]);
        Assert.Equal(50.25, (double)location["latitude"]);
    }

    [Fact]
    public void BuildOtherUpdates_MapsChosenInlineResult()
    {
        var chosen = new MtProtoTL.UpdateBotInlineSend
        {
            user_id = 10,
            query = "cats",
            id = "result-1",
        };

        var updates = BotGateway.BuildOtherUpdates([chosen], null, null);

        var result = Assert.Single(updates)["chosen_inline_result"] as Dictionary<string, object>;
        Assert.NotNull(result);
        Assert.Equal("result-1", (string)result["result_id"]);
        Assert.Equal("cats", (string)result["query"]);
    }

    [Fact]
    public void BuildOtherUpdates_SkipsUnrelatedUpdates()
    {
        var unrelated = new MtProtoTL.UpdateNewMessage
        {
            message = new MtProtoTL.Message { id = 1 },
            pts = 1,
            pts_count = 1,
        };

        var updates = BotGateway.BuildOtherUpdates([unrelated], null, null);

        Assert.Empty(updates);
    }

    [Fact]
    public void BuildOtherUpdates_ReturnsEmptyForNullInput()
    {
        Assert.Empty(BotGateway.BuildOtherUpdates(null!, null, null));
        Assert.Empty(BotGateway.BuildOtherUpdates([], null, null));
    }

    [Fact]
    public void BuildOtherUpdates_DoesNotThrow_WhenUserEntryIsNull()
    {
        // ReadTLDictionary кладёт null вместо userEmpty — такие записи не должны ронять getUpdates.
        var users = new Dictionary<long, MtProtoTL.User> { [10] = null! };
        var cb = new MtProtoTL.UpdateBotCallbackQuery
        {
            query_id = 1,
            user_id = 10,
            peer = new MtProtoTL.PeerUser { user_id = 10 },
            msg_id = 5,
            chat_instance = 0,
        };

        var updates = BotGateway.BuildOtherUpdates([cb], users, null);

        var query = Assert.Single(updates)["callback_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.False(query.ContainsKey("from"));
    }

    [Fact]
    public void BuildEntities_SkipsTextMention_WhenUserEntryIsNull()
    {
        // Та же защита на пути entities: null вместо userEmpty не должен давать NullReferenceException.
        var users = new Dictionary<long, MtProtoTL.User> { [10] = null! };
        var entities = new MtProtoTL.MessageEntity[]
        {
            new MtProtoTL.MessageEntityBold { offset = 0, length = 5 },
            new MtProtoTL.MessageEntityMentionName { offset = 6, length = 4, user_id = 10 },
        };

        var built = BotGateway.BuildEntities(entities, users);

        var entity = Assert.Single(built);
        Assert.Equal("bold", (string)entity["type"]);
    }

    [Fact]
    public void FilterAllowedUpdates_KeepsCallbackQuery_WhenAllowed()
    {
        var updates = new List<Dictionary<string, object>>
        {
            new() { ["update_id"] = 1L, ["message"] = new Dictionary<string, object>() },
            new() { ["update_id"] = 2L, ["callback_query"] = new Dictionary<string, object>() },
        };

        var filtered = BotGateway.FilterAllowedUpdates(updates, ["message", "callback_query"]);

        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void FilterAllowedUpdates_DropsCallbackQuery_WhenOnlyMessageAllowed()
    {
        var updates = new List<Dictionary<string, object>>
        {
            new() { ["update_id"] = 1L, ["message"] = new Dictionary<string, object>() },
            new() { ["update_id"] = 2L, ["callback_query"] = new Dictionary<string, object>() },
        };

        var filtered = BotGateway.FilterAllowedUpdates(updates, ["message"]);

        var single = Assert.Single(filtered);
        Assert.Equal(1L, (long)single["update_id"]);
    }

    [Fact]
    public void FilterAllowedUpdates_KeepsEverything_WhenAllowedIsNull()
    {
        var updates = new List<Dictionary<string, object>>
        {
            new() { ["update_id"] = 1L, ["message"] = new Dictionary<string, object>() },
            new() { ["update_id"] = 2L, ["inline_query"] = new Dictionary<string, object>() },
        };

        Assert.Equal(2, BotGateway.FilterAllowedUpdates(updates, null!).Count);
    }
}
