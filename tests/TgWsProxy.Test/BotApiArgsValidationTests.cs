using System.Collections.Generic;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Предвалидация аргументов Bot API до сетевых вызовов: отсутствие <c>chat_id</c> или
/// неверный <c>parse_mode</c> обязаны давать 400 ещё до <c>auth.importBotAuthorization</c>,
/// иначе клиент получал 401 про заголовки <c>X-Telegram-Api-Id</c>/<c>X-Telegram-Api-Hash</c>
/// и начинал переспрашивать учётные данные приложения вместо исправления параметров.
/// </summary>
public class BotApiArgsValidationTests
{
    private static Dictionary<string, object> Args(params (string Key, object Value)[] pairs)
    {
        var args = new Dictionary<string, object>();
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    [Theory]
    [InlineData("sendMessage")]
    [InlineData("sendPhoto")]
    [InlineData("sendDocument")]
    [InlineData("sendMediaGroup")]
    [InlineData("getChat")]
    [InlineData("deleteMessage")]
    [InlineData("forwardMessage")]
    [InlineData("sendChatAction")]
    public void MissingChatId_Throws400(string method)
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork(method, Args()));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal($"chat_id is required for {method}", exception.Message);
    }

    [Fact]
    public void ChatIdPresent_Passes()
    {
        BotGateway.ValidateArgsBeforeNetwork("sendMessage", Args(("chat_id", "-1001234567890")));
    }

    [Fact]
    public void RawPeer_InsteadOfChatId_Passes()
    {
        BotGateway.ValidateArgsBeforeNetwork("sendMessage", Args(("peer", "{}")));
    }

    [Fact]
    public void ChatIdIsNotRequired_ForMethodsWithoutTarget()
    {
        // getMe/getFile/getUpdates/… не адресуются в чат — предвалидация их не трогает.
        BotGateway.ValidateArgsBeforeNetwork("getMe", Args());
        BotGateway.ValidateArgsBeforeNetwork("getUpdates", Args());
        BotGateway.ValidateArgsBeforeNetwork("getFile", Args());
        BotGateway.ValidateArgsBeforeNetwork("setMyCommands", Args());
        BotGateway.ValidateArgsBeforeNetwork("unknownMethod", Args());
    }

    [Theory]
    [InlineData("Markdown")]
    [InlineData("MarkdownV2")]
    public void UnsupportedParseMode_Throws400(string parseMode)
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork("sendMessage", Args(("parse_mode", parseMode))));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal($"parse_mode '{parseMode}' is not implemented; pass 'entities' instead", exception.Message);
    }

    [Fact]
    public void UnknownParseMode_Throws400()
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork("sendMessage", Args(("parse_mode", "Nope"))));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal("unknown parse_mode 'Nope'", exception.Message);
    }

    [Theory]
    [InlineData("HTML")]
    [InlineData("")]
    public void SupportedParseMode_Passes(string parseMode)
    {
        BotGateway.ValidateArgsBeforeNetwork("sendMessage", Args(("chat_id", "42"), ("parse_mode", parseMode)));
    }

    [Fact]
    public void EditWithoutTarget_Throws400()
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork("editMessageText", Args()));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal("chat_id or inline_message_id is required for editMessageText", exception.Message);
    }

    [Fact]
    public void EditWithBothTargets_Throws400()
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork(
                "editMessageCaption",
                Args(("chat_id", "42"), ("inline_message_id", "abc"))));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal("chat_id and inline_message_id are mutually exclusive for editMessageCaption", exception.Message);
    }

    [Fact]
    public void EditWithChatId_Passes()
    {
        BotGateway.ValidateArgsBeforeNetwork("editMessageText", Args(("chat_id", "42")));
    }
}
