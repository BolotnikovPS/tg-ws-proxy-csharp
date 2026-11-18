using System.Collections.Generic;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Шлюз умеет отдавать апдейты только через getUpdates, поэтому webhook-методы должны
/// отвечать понятной ошибкой 400, а не общим 501 «method is not implemented».
/// </summary>
public class BotGatewayWebhookTests
{
    [Theory]
    [InlineData("setWebhook")]
    [InlineData("deleteWebhook")]
    [InlineData("getWebhookInfo")]
    public void ValidateArgsBeforeNetwork_WebhookMethods_FailWith400(string method)
    {
        var exception = Assert.Throws<BotGatewayException>(
            () => BotGateway.ValidateArgsBeforeNetwork(method, new Dictionary<string, object>()));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Contains("long-poll", exception.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateArgsBeforeNetwork_OtherMethods_AreNotAffected()
    {
        BotGateway.ValidateArgsBeforeNetwork("getMe", new Dictionary<string, object>());
        BotGateway.ValidateArgsBeforeNetwork("getUpdates", new Dictionary<string, object>());
    }
}
