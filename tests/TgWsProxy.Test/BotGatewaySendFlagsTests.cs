using System;
using System.Collections.Generic;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// disable_notification / protect_content / link_preview_options и until_date раньше молча
/// игнорировались: сообщения уходили со звуком, контент можно было пересылать, превью ссылок
/// не отключалось, а временный бан превращался в бессрочный.
/// </summary>
public class BotGatewaySendFlagsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ReadSilentFlag_ReadsDisableNotification(object value, bool expected)
    {
        var args = new Dictionary<string, object> { ["disable_notification"] = value };

        Assert.Equal(expected, BotGateway.ReadSilentFlag(args));
    }

    [Fact]
    public void ReadSilentFlag_MissingArgument_IsFalse()
        => Assert.False(BotGateway.ReadSilentFlag(new Dictionary<string, object>()));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReadNoForwardsFlag_ReadsProtectContent(object value, bool expected)
    {
        var args = new Dictionary<string, object> { ["protect_content"] = value };

        Assert.Equal(expected, BotGateway.ReadNoForwardsFlag(args));
    }

    [Fact]
    public void ReadNoForwardsFlag_MissingArgument_IsFalse()
        => Assert.False(BotGateway.ReadNoForwardsFlag(new Dictionary<string, object>()));

    [Theory]
    [InlineData("""{"is_disabled":true}""", true)]
    [InlineData("""{"is_disabled":false}""", false)]
    public void ReadNoWebpageFlag_ReadsLinkPreviewOptions(string json, bool expected)
    {
        var args = new Dictionary<string, object>
        {
            ["link_preview_options"] = JsonDocument.Parse(json).RootElement.Clone(),
        };

        Assert.Equal(expected, BotGateway.ReadNoWebpageFlag(args));
    }

    [Fact]
    public void ReadNoWebpageFlag_AcceptsJsonString()
    {
        var args = new Dictionary<string, object> { ["link_preview_options"] = """{"is_disabled":true}""" };

        Assert.True(BotGateway.ReadNoWebpageFlag(args));
    }

    [Fact]
    public void ReadNoWebpageFlag_FallsBackToLegacyArgument()
    {
        var args = new Dictionary<string, object> { ["disable_web_page_preview"] = true };

        Assert.True(BotGateway.ReadNoWebpageFlag(args));
    }

    [Fact]
    public void ReadNoWebpageFlag_LinkPreviewOptionsWinOverLegacyArgument()
    {
        var args = new Dictionary<string, object>
        {
            ["link_preview_options"] = JsonDocument.Parse("""{"is_disabled":false}""").RootElement.Clone(),
            ["disable_web_page_preview"] = true,
        };

        Assert.False(BotGateway.ReadNoWebpageFlag(args));
    }

    [Fact]
    public void ReadNoWebpageFlag_MissingArguments_IsFalse()
        => Assert.False(BotGateway.ReadNoWebpageFlag(new Dictionary<string, object>()));

    [Fact]
    public void ToBanUntilDate_ConvertsUnixSeconds()
    {
        var until = BotGateway.ToBanUntilDate(1_700_000_000);

        Assert.Equal(DateTimeKind.Utc, until.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime, until);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ToBanUntilDate_NonPositive_MeansForever(long untilDate)
        => Assert.Equal(default, BotGateway.ToBanUntilDate(untilDate));
}
