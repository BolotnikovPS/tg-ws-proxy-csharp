using System.Text;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Апдейты, которые сервер присылает не в <c>updates.difference</c>, а контейнером прямо в ответе
/// на RPC (<c>updates</c>/<c>updatesCombined</c>/<c>updateShort</c>). Именно так приходят нажатия
/// inline-кнопок: у <c>callback_query</c> нет pts, поэтому в разницу он не попадает.
/// </summary>
public class BotGatewayPushedUpdatesTests
{
    private static MtProtoTL.Message NewMessage(int id, string text) => new()
    {
        id = id,
        peer_id = new MtProtoTL.PeerUser { user_id = 10 },
        date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        message = text,
    };

    private static MtProtoTL.UpdateBotCallbackQuery NewCallback(byte[] data) => new()
    {
        query_id = 9876543210123,
        user_id = 10,
        peer = new MtProtoTL.PeerUser { user_id = 10 },
        msg_id = 77,
        chat_instance = -555,
        flags = MtProtoTL.UpdateBotCallbackQuery.Flags.has_data,
        data = data,
    };

    [Fact]
    public void BuildPushedList_MapsMessage()
    {
        var updates = BotGateway.BuildPushedList(
            [new MtProtoTL.UpdateNewMessage { message = NewMessage(42, "pushed text") }],
            null,
            null);

        var update = Assert.Single(updates);
        var message = update["message"] as Dictionary<string, object>;
        Assert.NotNull(message);
        Assert.Equal(42, (int)message["message_id"]);
        Assert.Equal("pushed text", (string)message["text"]);
    }

    [Fact]
    public void BuildPushedList_MapsCallbackQuery()
    {
        var updates = BotGateway.BuildPushedList([NewCallback(Encoding.UTF8.GetBytes("cb_live_A"))], null, null);

        var query = Assert.Single(updates)["callback_query"] as Dictionary<string, object>;
        Assert.NotNull(query);
        Assert.Equal("9876543210123", (string)query["id"]);
        Assert.Equal("-555", (string)query["chat_instance"]);
        Assert.Equal("cb_live_A", (string)query["data"]);
    }

    [Fact]
    public void BuildPushedList_KeepsBothMessageAndCallback()
    {
        // В одном контейнере может лежать и сообщение, и нажатие кнопки: оба обязаны дойти до бота.
        var updates = BotGateway.BuildPushedList(
            [
                new MtProtoTL.UpdateNewMessage { message = NewMessage(1, "text") },
                NewCallback(Encoding.UTF8.GetBytes("btn")),
            ],
            null,
            null);

        Assert.Equal(2, updates.Count);
        Assert.True(updates[0].ContainsKey("message"));
        Assert.True(updates[1].ContainsKey("callback_query"));
    }

    [Fact]
    public void BuildPushedList_SkipsEmptyMessage()
    {
        // messageEmpty (id == 0) — не Bot API сообщение, отдавать его нельзя.
        var updates = BotGateway.BuildPushedList(
            [new MtProtoTL.UpdateNewMessage { message = new MtProtoTL.Message { id = 0 } }],
            null,
            null);

        Assert.Empty(updates);
    }

    [Fact]
    public void BuildPushedList_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(BotGateway.BuildPushedList([], null, null));
        Assert.Empty(BotGateway.BuildPushedList(null!, null, null));
    }
}
