using TgWsProxy.Web;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты по-ботового ограничителя Bot API (fixed window 1 с, botApi.requestsPerSecondPerToken).
/// </summary>
public class BotApiRateLimiterTests
{
    private static BotApiRateLimiter Create(Func<double> clock) => new(clock);

    [Fact]
    public void Disabled_When_Limit_Is_Zero_Or_Negative()
    {
        var limiter = Create(static () => 1_000.0);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire(42, 0));
        }

        Assert.True(limiter.TryAcquire(42, -1));
    }

    [Fact]
    public void Allows_Up_To_Limit_Then_Rejects_In_One_Second()
    {
        var limiter = Create(static () => 1_000.0);

        Assert.True(limiter.TryAcquire(42, 3));
        Assert.True(limiter.TryAcquire(42, 3));
        Assert.True(limiter.TryAcquire(42, 3));

        // Четвёртый запрос того же бота в той же секунде отклоняется.
        Assert.False(limiter.TryAcquire(42, 3));

        // Другой бот имеет собственное окно.
        Assert.True(limiter.TryAcquire(7, 3));
    }

    [Fact]
    public void Window_Resets_On_Second_Boundary()
    {
        var now = 1_000.0;
        var limiter = Create(() => now);

        Assert.True(limiter.TryAcquire(42, 1));
        Assert.False(limiter.TryAcquire(42, 1));

        // Та же секунда (дробная часть не влияет) — по-прежнему отклоняется.
        now = 1_000.9;
        Assert.False(limiter.TryAcquire(42, 1));

        // Следующая секунда — счётчик сброшен.
        now = 1_001.0;
        Assert.True(limiter.TryAcquire(42, 1));
    }
}