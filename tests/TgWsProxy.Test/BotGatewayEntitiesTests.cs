using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Маппинг MessageEntity в обе стороны (Bot API ↔ MTProto) и разбор аргумента rich_message.
/// </summary>
public class BotGatewayEntitiesTests
{
    private static MtProtoTL.Message MessageWithEntities(MtProtoTL.MessageEntity[] entities) => new()
    {
        id = 7,
        flags = MtProtoTL.Message.Flags.has_entities,
        peer_id = new MtProtoTL.PeerUser { user_id = 111 },
        date = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        message = "text",
        entities = entities,
    };

    [Fact]
    public void BuildMessage_MapsEntities()
    {
        var built = BotGateway.BuildMessage(
            MessageWithEntities([new MtProtoTL.MessageEntityBold { offset = 0, length = 4 }]), [], null);

        Assert.Equal("text", (string)built["text"]);
        var entities = Assert.IsType<List<Dictionary<string, object>>>(built["entities"]);
        var entity = Assert.Single(entities);
        Assert.Equal("bold", (string)entity["type"]);
        Assert.Equal(0, (int)entity["offset"]);
        Assert.Equal(4, (int)entity["length"]);
    }

    [Fact]
    public void BuildMessage_MapsBankCardEntity()
    {
        var built = BotGateway.BuildMessage(
            MessageWithEntities([new MtProtoTL.MessageEntityBankCard { offset = 5, length = 19 }]), [], null);

        var entity = Assert.Single(Assert.IsType<List<Dictionary<string, object>>>(built["entities"]));
        Assert.Equal("bank_card_number", (string)entity["type"]);
        Assert.Equal(5, (int)entity["offset"]);
        Assert.Equal(19, (int)entity["length"]);
    }

    [Fact]
    public void BuildMessage_MapsTextLinkWithUrl()
    {
        var built = BotGateway.BuildMessage(
            MessageWithEntities([new MtProtoTL.MessageEntityTextUrl { offset = 0, length = 3, url = "https://e.x" }]),
            [], null);

        var entity = Assert.Single(Assert.IsType<List<Dictionary<string, object>>>(built["entities"]));
        Assert.Equal("text_link", (string)entity["type"]);
        Assert.Equal("https://e.x", (string)entity["url"]);
    }

    [Fact]
    public void BuildMessage_SkipsEntitiesWithoutBotApiType()
    {
        var built = BotGateway.BuildMessage(
            MessageWithEntities([new MtProtoTL.MessageEntityUnknown { offset = 0, length = 1 }]), [], null);

        Assert.False(built.ContainsKey("entities"));
    }

    [Fact]
    public void BuildEntities_OmitsMentionName_WhenUserUnknown()
    {
        var entities = BotGateway.BuildEntities(
            [new MtProtoTL.MessageEntityMentionName { offset = 0, length = 2, user_id = 999 }], []);

        Assert.Empty(entities);
    }

    [Fact]
    public void BuildEntities_MapsMentionName_WhenUserKnown()
    {
        var users = new Dictionary<long, MtProtoTL.User>
        {
            [42] = new()
            {
                id = 42,
                flags = MtProtoTL.User.Flags.has_first_name,
                first_name = "Bob",
            },
        };

        var entities = BotGateway.BuildEntities(
            [new MtProtoTL.MessageEntityMentionName { offset = 0, length = 2, user_id = 42 }], users);

        var entity = Assert.Single(entities);
        Assert.Equal("text_mention", (string)entity["type"]);
        var user = Assert.IsType<Dictionary<string, object>>(entity["user"]);
        Assert.Equal(42L, (long)user["id"]);
    }

    [Fact]
    public void BuildBotApiEntity_MapsBankCardNumber()
    {
        using var doc = JsonDocument.Parse("""{"type":"bank_card_number","offset":3,"length":16}""");
        var entity = BotGateway.BuildBotApiEntity(doc.RootElement);

        var bankCard = Assert.IsType<MtProtoTL.MessageEntityBankCard>(entity);
        Assert.Equal(3, bankCard.offset);
        Assert.Equal(16, bankCard.length);
    }

    [Theory]
    [InlineData("""{"html":"<b>hi</b>"}""", typeof(MtProtoTL.InputRichMessageHTML))]
    [InlineData("""{"markdown":"**hi**"}""", typeof(MtProtoTL.InputRichMessageMarkdown))]
    [InlineData("""{"html":"<b>hi</b>","markdown":"**hi**"}""", typeof(MtProtoTL.InputRichMessageHTML))]
    public void ReadRichMessageArg_PicksHtmlThenMarkdown(string json, Type expected)
    {
        using var doc = JsonDocument.Parse(json);
        var richMessage = BotGateway.ReadRichMessageArg(
            new Dictionary<string, object> { ["rich_message"] = doc.RootElement.Clone() });

        Assert.IsType(expected, richMessage);
    }

    [Fact]
    public void ReadRichMessageArg_AppliesRtlFlag()
    {
        using var doc = JsonDocument.Parse("""{"html":"<b>hi</b>","rtl":true}""");
        var richMessage = Assert.IsType<MtProtoTL.InputRichMessageHTML>(
            BotGateway.ReadRichMessageArg(new Dictionary<string, object> { ["rich_message"] = doc.RootElement.Clone() }));

        Assert.Equal(MtProtoTL.InputRichMessageHTML.Flags.rtl, richMessage.flags & MtProtoTL.InputRichMessageHTML.Flags.rtl);
    }

    [Fact]
    public void ReadRichMessageArg_ParsesJsonString()
    {
        var richMessage = Assert.IsType<MtProtoTL.InputRichMessageHTML>(
            BotGateway.ReadRichMessageArg(
                new Dictionary<string, object> { ["rich_message"] = """{"html":"<i>x</i>"}""" }));

        Assert.Equal("<i>x</i>", richMessage.html);
    }

    [Fact]
    public void ReadRichMessageArg_ReturnsNull_WhenNoContentOrMissing()
    {
        Assert.Null(BotGateway.ReadRichMessageArg(new Dictionary<string, object>()));
        Assert.Null(BotGateway.ReadRichMessageArg(new Dictionary<string, object> { ["rich_message"] = "{}" }));
        Assert.Null(BotGateway.ReadRichMessageArg(new Dictionary<string, object> { ["rich_message"] = "not json" }));
    }

    [Fact]
    public void ValidateArgsBeforeNetwork_SendRichMessageRequiresChatId()
    {
        var ex = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork("sendRichMessage", new Dictionary<string, object>()));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void ValidateArgsBeforeNetwork_SendRichMessageWithChatId_Passes()
    {
        BotGateway.ValidateArgsBeforeNetwork(
            "sendRichMessage", new Dictionary<string, object> { ["chat_id"] = "42" });
    }
}
