using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

public class BotGatewayTests
{
    private static BotGateway NewGateway(bool enabled = true)
        => new(new BotApiConfig { Enabled = enabled, DataDir = "./bot-sessions" }, NullLogger<BotGateway>.Instance);

    [Fact]
    public void ResolveSecret_ReturnsNull_WhenNullOrEmpty()
    {
        Assert.Null(BotGateway.ResolveSecret(null));
        Assert.Null(BotGateway.ResolveSecret(""));
        Assert.Null(BotGateway.ResolveSecret("   "));
    }

    [Fact]
    public void ResolveSecret_Accepts16Bytes()
    {
        var key = new byte[16];
        Array.Fill(key, (byte)0xAB);
        Assert.Equal(key, BotGateway.ResolveSecret(Convert.ToHexString(key)));
    }

    [Fact]
    public void ResolveSecret_StripsDdPrefix()
    {
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        Assert.Equal(key, BotGateway.ResolveSecret("dd" + Convert.ToHexString(key)));
    }

    [Fact]
    public void ResolveSecret_StripsEePrefix()
    {
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        Assert.Equal(key, BotGateway.ResolveSecret("ee" + Convert.ToHexString(key)));
    }

    [Fact]
    public void ResolveSecret_Throws_OnWrongLength() => Assert.Throws<FormatException>(() => BotGateway.ResolveSecret("abcd"));

    [Fact]
    public void BuildUser_MapsBotApiFields()
    {
        var u = new MtProtoTL.User
        {
            id = 123456789L,
            flags = MtProtoTL.User.Flags.bot
                  | MtProtoTL.User.Flags.has_first_name
                  | MtProtoTL.User.Flags.has_username
                  | MtProtoTL.User.Flags.has_lang_code,
            first_name = "MyBot",
            username = "mybot",
            lang_code = "en",
        };

        var dict = BotGateway.BuildUser(u);

        Assert.Equal(123456789L, dict["id"]);
        Assert.Equal(true, dict["is_bot"]);
        Assert.Equal("MyBot", dict["first_name"]);
        Assert.Equal("mybot", dict["username"]);
        Assert.Equal("en", dict["language_code"]);
        Assert.False(dict.ContainsKey("last_name"));
    }

    [Fact]
    public async Task HandleAsync_Returns403_WhenDisabled()
    {
        var gateway = NewGateway(enabled: false);
        var result = await gateway.HandleAsync("123:AAH", "getMe", new Dictionary<string, object>(), 1, "hash", default);
        Assert.False(result.Ok);
        Assert.Equal(403, result.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_Returns501_ForUnknownMethod()
    {
        var gateway = NewGateway();
        var result = await gateway.HandleAsync("123:AAH", "sendVenue", new Dictionary<string, object>(), 1, "hash", default);
        Assert.False(result.Ok);
        Assert.Equal(501, result.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_GetMe_Returns400_OnBadToken()
    {
        var gateway = NewGateway();
        var result = await gateway.HandleAsync("not-a-token", "getMe", new Dictionary<string, object>(), 1, "hash", default);
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_GetMe_Returns401_WhenMissingHeaders()
    {
        var gateway = NewGateway();
        var result = await gateway.HandleAsync("123:AAH", "getMe", new Dictionary<string, object>(), null, "", default);
        Assert.False(result.Ok);
        Assert.Equal(401, result.ErrorCode);
    }
}
