using System;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Web;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// 429 без <c>parameters.retry_after</c> ломал клиентов официального Bot API (и TBotPlatform,
/// см. <c>docs/prompts/REQUIREMENTS-BOT-API.md</c>): они ждут паузу в теле ответа, а не только
/// HTTP-статус. Здесь проверяются обе ветки — собственный ограничитель шлюза и флуд-лимит Telegram.
/// </summary>
public class BotApiRetryAfterTests
{
    [Theory]
    [InlineData("FLOOD_WAIT_42", 42)]
    [InlineData("FLOOD_WAIT_1", 1)]
    [InlineData("FLOOD_WAIT_0", 1)]
    [InlineData("FLOOD_WAIT_", 1)]
    [InlineData("FLOOD_WAIT_abc", 1)]
    [InlineData("FLOOD_PREMIUM_WAIT_5", 1)]
    [InlineData("SOMETHING_ELSE", 1)]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    public void ParseFloodWaitSeconds_ReadsSeconds_AndFallsBackToOne(string? message, int expected)
    {
        Assert.Equal(expected, BotGateway.ParseFloodWaitSeconds(message!));
    }

    [Fact]
    public void TooManyRequests_FillsRetryAfter_AndDescription()
    {
        var result = BotApiResult.TooManyRequests(7);

        Assert.False(result.Ok);
        Assert.Equal(429, result.ErrorCode);
        Assert.Equal(7, result.RetryAfter);
        Assert.Contains("retry after 7", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_LeavesRetryAfterEmpty()
    {
        var result = BotApiResult.Fail(400, "Bad Request: text is required for sendMessage");

        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public void GetRetryAfterSeconds_IsOne_AtWindowStart()
    {
        var limiter = new BotApiRateLimiter(static () => 100.0);

        Assert.Equal(1, limiter.GetRetryAfterSeconds());
    }

    [Fact]
    public void GetRetryAfterSeconds_IsOne_UntilWindowEnds()
    {
        var now = 100.0;
        var limiter = new BotApiRateLimiter(() => now);

        now = 100.5;
        Assert.Equal(1, limiter.GetRetryAfterSeconds());

        now = 100.999;
        Assert.Equal(1, limiter.GetRetryAfterSeconds());
    }

    [Fact]
    public void GetRetryAfterSeconds_IsAtLeastOne_WhenWindowAlreadyElapsed()
    {
        var now = 100.0;
        var limiter = new BotApiRateLimiter(() => now);

        // Часы ушли вперёд, окно не сброшено вызовом TryAcquire: остаток отрицательный,
        // но клиенту всё равно нельзя отдавать 0 — минимум одна секунда.
        now = 105.0;
        Assert.Equal(1, limiter.GetRetryAfterSeconds());
    }

    [Fact]
    public void TryAcquire_RejectsAboveLimit_AndRetryAfterStaysOne()
    {
        var limiter = new BotApiRateLimiter(static () => 100.0);

        Assert.True(limiter.TryAcquire(42, 1));
        Assert.False(limiter.TryAcquire(42, 1));
        Assert.Equal(1, limiter.GetRetryAfterSeconds());
    }
}
