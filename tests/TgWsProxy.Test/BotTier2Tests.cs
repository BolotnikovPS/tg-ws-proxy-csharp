using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class BotTier2Tests
{
    private const string Token = "123:AAH";

    private static BotGateway NewGateway(bool enabled = true)
        => new(new BotApiConfig { Enabled = enabled, DataDir = "./bot-sessions" }, NullLogger<BotGateway>.Instance);

    private static Task<BotApiResult> CallAsync(BotGateway gateway, string method, IReadOnlyDictionary<string, object> args)
        => gateway.HandleAsync(Token, method, args, 1, "hash", default);

    [Fact]
    public async Task EditMessageText_Returns400_WhenTextMissing()
    {
        var result = await CallAsync(NewGateway(), "editMessageText", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task EditMessageText_Returns400_WhenNoChatOrInline()
    {
        var result = await CallAsync(NewGateway(), "editMessageText", new Dictionary<string, object> { ["text"] = "hello" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task EditMessageCaption_Returns400_WhenNoChatOrInline()
    {
        var result = await CallAsync(NewGateway(), "editMessageCaption", new Dictionary<string, object> { ["caption"] = "new caption" });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task EditMessageMedia_Returns400_WhenMediaMissing()
    {
        var result = await CallAsync(NewGateway(), "editMessageMedia", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task EditMessageMedia_Returns400_WhenNoChatOrInline()
    {
        var args = new Dictionary<string, object>
        {
            ["media"] = JsonSerializer.SerializeToElement(new { type = "photo", media = "AQADAgAD..." }),
        };
        var result = await CallAsync(NewGateway(), "editMessageMedia", args);
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task DeleteMessage_Returns400_WhenMessageIdMissing()
    {
        var result = await CallAsync(NewGateway(), "deleteMessage", new Dictionary<string, object> { ["chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task DeleteMessage_Returns400_WhenChatIdMissing()
    {
        var result = await CallAsync(NewGateway(), "deleteMessage", new Dictionary<string, object> { ["message_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task ForwardMessage_Returns400_WhenFromChatIdMissing()
    {
        var result = await CallAsync(
            NewGateway(), "forwardMessage", new Dictionary<string, object> { ["chat_id"] = -456L, ["message_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task ForwardMessage_Returns400_WhenMessageIdMissing()
    {
        var result = await CallAsync(
            NewGateway(), "forwardMessage", new Dictionary<string, object> { ["chat_id"] = -456L, ["from_chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task CopyMessage_Returns400_WhenFromChatIdMissing()
    {
        var result = await CallAsync(
            NewGateway(), "copyMessage", new Dictionary<string, object> { ["chat_id"] = -456L, ["message_id"] = 5L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task CopyMessage_Returns400_WhenMessageIdMissing()
    {
        var result = await CallAsync(
            NewGateway(), "copyMessage", new Dictionary<string, object> { ["chat_id"] = -456L, ["from_chat_id"] = -456L });
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task AnswerCallbackQuery_Returns400_WhenCallbackQueryIdMissing()
    {
        var result = await CallAsync(NewGateway(), "answerCallbackQuery", new Dictionary<string, object>());
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }
}