using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class BotTier34Tests
{
    private const string Token = "123:AAH";

    private static BotGateway NewGateway(bool enabled = true)
        => new(new BotApiConfig { Enabled = enabled, DataDir = "./bot-sessions" }, NullLogger<BotGateway>.Instance);

    private static Task<BotApiResult> CallAsync(BotGateway gateway, string method, IReadOnlyDictionary<string, object> args)
        => gateway.HandleAsync(Token, method, args, 1, "hash", default);

    [Fact]
    public async Task GetChat_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "getChat", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task GetChatAdministrators_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "getChatAdministrators", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task GetChatMember_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "getChatMember", new Dictionary<string, object> { ["user_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task GetChatMember_Returns400_WhenUserIdMissing()
    {
        var result = await CallAsync(NewGateway(), "getChatMember", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task GetChatMemberCount_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "getChatMemberCount", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetChatTitle_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "setChatTitle", new Dictionary<string, object> { ["title"] = "x" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetChatTitle_Returns400_WhenTitleMissing()
    {
        var result = await CallAsync(NewGateway(), "setChatTitle", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetChatDescription_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "setChatDescription", new Dictionary<string, object> { ["description"] = "x" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetChatPhoto_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "setChatPhoto", new Dictionary<string, object> { ["photo"] = "AQADAgAD..." });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetChatPhoto_Returns400_WhenPhotoMissing()
    {
        var result = await CallAsync(NewGateway(), "setChatPhoto", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task LeaveChat_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "leaveChat", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task BanChatMember_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "banChatMember", new Dictionary<string, object> { ["user_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task BanChatMember_Returns400_WhenUserIdMissing()
    {
        var result = await CallAsync(NewGateway(), "banChatMember", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SendChatAction_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "sendChatAction", new Dictionary<string, object> { ["action"] = "typing" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SendChatAction_Returns400_WhenActionMissing()
    {
        var result = await CallAsync(NewGateway(), "sendChatAction", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SendChatAction_Returns400_WhenActionUnknown()
    {
        var result = await CallAsync(
            NewGateway(), "sendChatAction", new Dictionary<string, object> { ["chat_id"] = -456L, ["action"] = "nope" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task SetMyCommands_Returns400_WhenCommandsMissing()
    {
        var result = await CallAsync(NewGateway(), "setMyCommands", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task AnswerInlineQuery_Returns400_WhenInlineQueryIdMissing()
    {
        var result = await CallAsync(NewGateway(), "answerInlineQuery", new Dictionary<string, object> { ["results"] = "[]" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task AnswerInlineQuery_Returns400_WhenResultsMissing()
    {
        var result = await CallAsync(
            NewGateway(), "answerInlineQuery", new Dictionary<string, object> { ["inline_query_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task AnswerInlineQuery_Returns400_WhenUnsupportedResultType()
    {
        const string json = "[{\"type\":\"photo\",\"id\":\"1\",\"photo_url\":\"https://x/y.jpg\",\"thumbnail_url\":\"https://x/y.jpg\"}]";
        var result = await CallAsync(
            NewGateway(), "answerInlineQuery",
            new Dictionary<string, object> { ["inline_query_id"] = 5L, ["results"] = JsonSerializer.SerializeToElement(json) });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }
}