using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="WebProxyStreamBudget"/>: резерв/возврат слотов, соблюдение живого лимита
/// <c>webProxy.maxStreams</c> и защита от «ухода в минус» при парном Release (F-W.25).
/// </summary>
public class WebProxyStreamBudgetTests
{
    [Fact]
    public void TryAcquire_RespectsLimit()
    {
        var config = new Config { WebProxy = new WebProxyConfig { MaxStreams = 2 } };
        var budget = new WebProxyStreamBudget(config);

        Assert.True(budget.TryAcquire());
        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());
        Assert.Equal(2, budget.Active);
    }

    [Fact]
    public void TryAcquire_Unlimited_WhenLimitIsZero()
    {
        var config = new Config { WebProxy = new WebProxyConfig { MaxStreams = 0 } };
        var budget = new WebProxyStreamBudget(config);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(budget.TryAcquire());
        }

        Assert.Equal(100, budget.Active);
    }

    [Fact]
    public void Release_FreesSlotForNextAcquire()
    {
        var config = new Config { WebProxy = new WebProxyConfig { MaxStreams = 1 } };
        var budget = new WebProxyStreamBudget(config);

        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());

        budget.Release();
        Assert.Equal(0, budget.Active);
        Assert.True(budget.TryAcquire());
    }

    [Fact]
    public void Release_WithoutAcquire_DoesNotGoNegative()
    {
        var budget = new WebProxyStreamBudget(new Config { WebProxy = new WebProxyConfig { MaxStreams = 4 } });

        budget.Release();
        budget.Release();

        Assert.Equal(0, budget.Active);
    }

    [Fact]
    public void TryAcquire_PicksUpHotConfigChange()
    {
        var config = new Config { WebProxy = new WebProxyConfig { MaxStreams = 2 } };
        var budget = new WebProxyStreamBudget(config);

        Assert.True(budget.TryAcquire());
        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());

        // Горячая правка лимита действует без пересоздания бюджета.
        config.WebProxy.MaxStreams = 3;
        Assert.True(budget.TryAcquire());
        Assert.False(budget.TryAcquire());
    }
}
