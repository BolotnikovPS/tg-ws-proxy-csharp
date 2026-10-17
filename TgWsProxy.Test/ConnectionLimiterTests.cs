using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты ограничителя одновременных сессий (--max-connections): семантика нулевого лимита,
/// удержание и освобождение слотов, устойчивость к повторному освобождению.
/// </summary>
public class ConnectionLimiterTests
{
    [Fact]
    public void Disabled_When_Limit_Is_Zero()
    {
        var limiter = new ConnectionLimiter(new Config());

        Assert.False(limiter.IsEnabled);

        // Выключенный лимитер не отклоняет даже сверхбольшое число попыток.
        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire());
        }
        Assert.Equal(100, limiter.Active);
    }

    [Fact]
    public void Rejects_Connection_Above_Limit()
    {
        var limiter = new ConnectionLimiter(2);

        Assert.True(limiter.IsEnabled);
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());

        // Третий слот не выдаётся, пока один из двух не освобождён.
        Assert.False(limiter.TryAcquire());
        Assert.Equal(2, limiter.Active);

        limiter.Release();
        Assert.True(limiter.TryAcquire());
        Assert.Equal(2, limiter.Active);
    }

    [Fact]
    public void Release_Returns_Slot_To_Pool()
    {
        var limiter = new ConnectionLimiter(1);

        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        Assert.Equal(1, limiter.Active);

        limiter.Release();
        Assert.Equal(0, limiter.Active);
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void Release_Below_Zero_Is_Ignored()
    {
        var limiter = new ConnectionLimiter(1);

        // Лишние освобождения (например, из finally при отказе) не уводят счётчик в минус.
        limiter.Release();
        limiter.Release();

        Assert.Equal(0, limiter.Active);
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void Limit_Is_Exposed_For_Logging()
    {
        Assert.Equal(7, new ConnectionLimiter(7).Limit);
        Assert.Equal(0, new ConnectionLimiter(new Config()).Limit);
    }

    [Fact]
    public void Concurrent_Acquire_Never_Exceeds_Limit()
    {
        const int limit = 8;
        const int workers = 64;
        var limiter = new ConnectionLimiter(limit);
        var granted = 0;

        Parallel.For(0, workers, _ =>
        {
            if (limiter.TryAcquire())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(limit, granted);
        Assert.Equal(limit, limiter.Active);
    }
}
