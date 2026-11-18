using System.Collections.Generic;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Входная сторона reply: <c>reply_to_message_id</c>, объект <c>reply_parameters</c>
/// (message_id / quote / quote_entities / quote_position) и <c>message_thread_id</c>.
/// </summary>
public class BotGatewayReplyParametersTests
{
    private static Dictionary<string, object> Args(params (string Key, object Value)[] items)
    {
        var args = new Dictionary<string, object>();
        foreach (var (key, value) in items)
        {
            args[key] = value;
        }

        return args;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadReplyToArg_ReturnsNull_WhenNoReplyArgs()
    {
        Assert.Null(BotGateway.ReadReplyToArg(Args()));
        Assert.Null(BotGateway.ReadReplyToArg(null));
    }

    [Fact]
    public void ReadReplyToArg_MapsLegacyReplyToMessageId()
    {
        var reply = BotGateway.ReadReplyToArg(Args(("reply_to_message_id", 42)));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(42, message.reply_to_msg_id);
        Assert.Equal(0, message.top_msg_id);
        Assert.False(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_top_msg_id));
    }

    [Fact]
    public void ReadReplyToArg_MapsReplyParametersObject()
    {
        var reply = BotGateway.ReadReplyToArg(Args((
            "reply_parameters",
            Json("""{"message_id": 77}"""))));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(77, message.reply_to_msg_id);
    }

    [Fact]
    public void ReadReplyToArg_AcceptsReplyParametersAsJsonString()
    {
        var reply = BotGateway.ReadReplyToArg(Args((
            "reply_parameters",
            """{"message_id": 88}""")));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(88, message.reply_to_msg_id);
    }

    [Fact]
    public void ReadReplyToArg_MapsQuoteAndEntities()
    {
        var reply = BotGateway.ReadReplyToArg(Args((
            "reply_parameters",
            Json("""
                 {"message_id": 5, "quote": "hello", "quote_entities": [{"type": "bold", "offset": 0, "length": 5}]}
                 """))));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(5, message.reply_to_msg_id);
        Assert.Equal("hello", message.quote_text);
        Assert.True(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_quote_text));
        Assert.True(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_quote_entities));
        var entity = Assert.Single(message.quote_entities);
        Assert.IsType<MtProtoTL.MessageEntityBold>(entity);
    }

    [Fact]
    public void ReadReplyToArg_MapsQuotePosition()
    {
        var reply = BotGateway.ReadReplyToArg(Args((
            "reply_parameters",
            Json("""{"message_id": 5, "quote": "hi", "quote_position": 3}"""))));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.True(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_quote_offset));
        Assert.Equal(3, message.quote_offset);
    }

    [Fact]
    public void ReadReplyToArg_IgnoresQuotePosition_WhenQuoteMissing()
    {
        var reply = BotGateway.ReadReplyToArg(Args((
            "reply_parameters",
            Json("""{"message_id": 5, "quote_position": 3}"""))));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.False(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_quote_offset));
        Assert.Equal(0, message.quote_offset);
    }

    [Fact]
    public void ReadReplyToArg_UsesThreadIdAsReplyTarget_ForTopicWithoutReply()
    {
        var reply = BotGateway.ReadReplyToArg(Args(("message_thread_id", 900)));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(900, message.reply_to_msg_id);
        Assert.False(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_top_msg_id));
    }

    [Fact]
    public void ReadReplyToArg_SetsTopMsgId_WhenThreadDiffersFromReply()
    {
        var reply = BotGateway.ReadReplyToArg(Args(
            ("reply_to_message_id", 10),
            ("message_thread_id", 900)));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(10, message.reply_to_msg_id);
        Assert.Equal(900, message.top_msg_id);
        Assert.True(message.flags.HasFlag(MtProtoTL.InputReplyToMessage.Flags.has_top_msg_id));
    }

    [Fact]
    public void ReadReplyToArg_IgnoresGeneralTopicThread()
    {
        var reply = BotGateway.ReadReplyToArg(Args(("message_thread_id", 1)));

        Assert.Null(reply);
    }

    [Fact]
    public void ReadReplyToArg_PrefersReplyParametersOverLegacyId()
    {
        var reply = BotGateway.ReadReplyToArg(Args(
            ("reply_to_message_id", 10),
            ("reply_parameters", Json("""{"message_id": 20}"""))));

        var message = Assert.IsType<MtProtoTL.InputReplyToMessage>(reply);
        Assert.Equal(20, message.reply_to_msg_id);
    }
}
