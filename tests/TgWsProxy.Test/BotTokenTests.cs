using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class BotTokenTests
{
    [Fact]
    public void BotIdFromToken_ParsesNumericPrefix() => Assert.Equal(123456789L, MtProtoClient.BotIdFromToken("123456789:AAHxyz"));

    [Fact]
    public void BotIdFromToken_Throws_OnMissingColon() => Assert.Throws<FormatException>(() => MtProtoClient.BotIdFromToken("123456789"));

    [Fact]
    public void BotIdFromToken_Throws_OnEmptySecret() => Assert.Throws<FormatException>(() => MtProtoClient.BotIdFromToken("123456789:"));

    [Fact]
    public void BotIdFromToken_Throws_OnNonNumericId() => Assert.Throws<FormatException>(() => MtProtoClient.BotIdFromToken("abc:secret"));

    [Fact]
    public void BotIdFromToken_Throws_OnNull() => Assert.Throws<ArgumentNullException>(() => MtProtoClient.BotIdFromToken(null!));
}
