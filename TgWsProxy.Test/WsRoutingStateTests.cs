#nullable enable

using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

public class WsRoutingStateTests
{
    [Fact]
    public void Cooldown_IsTrackedPerDcKey()
    {
        var state = new WsRoutingState();
        var key = (Dc: 2, IsMedia: true);
        var now = DateTimeOffset.UtcNow;

        state.SetFailCooldown(key, now.AddSeconds(5));

        Assert.True(state.IsInFailCooldown(key, now));
        Assert.False(state.IsInFailCooldown(key, now.AddSeconds(6)));
        state.ClearFailCooldown(key);
        Assert.False(state.IsInFailCooldown(key, now));
    }

    [Fact]
    public void Blacklist_IsTrackedPerDcKey()
    {
        var state = new WsRoutingState();
        var key = (Dc: 4, IsMedia: false);

        Assert.False(state.IsBlacklisted(key));
        state.AddBlacklist(key);
        Assert.True(state.IsBlacklisted(key));
    }

    [Fact]
    public void Pool_ReturnsFreshSocket_AndSkipsStale()
    {
        var state = new WsRoutingState();
        var key = (Dc: 1, IsMedia: false);
        var now = DateTimeOffset.UtcNow;
        var stale = new DummyWs();
        var fresh = new DummyWs();

        state.AddToPool(key, stale, now.AddMinutes(-10), 4);
        state.AddToPool(key, fresh, now, 4);

        var fromPool = state.TryTakePooledWs(key, now, TimeSpan.FromMinutes(2));

        Assert.Same(fresh, fromPool);
    }

    [Fact]
    public void Pool_EvictsOldest_WhenCapacityExceeded()
    {
        var state = new WsRoutingState();
        var key = (Dc: 3, IsMedia: true);
        var now = DateTimeOffset.UtcNow;
        var first = new DummyWs();
        var second = new DummyWs();
        var third = new DummyWs();

        state.AddToPool(key, first, now.AddSeconds(-3), 2);
        state.AddToPool(key, second, now.AddSeconds(-2), 2);
        var evicted = state.AddToPool(key, third, now.AddSeconds(-1), 2);

        Assert.Single(evicted);
        Assert.Same(first, evicted[0]);
    }

    [Fact]
    public void BlacklistSummary_IsNone_OrSortedKeys()
    {
        var state = new WsRoutingState();
        Assert.Equal("none", state.FormatBlacklistSummary());
        state.AddBlacklist((2, true));
        state.AddBlacklist((1, false));
        Assert.Equal("DC1, DC2m", state.FormatBlacklistSummary());
    }

    [Fact]
    public void Refill_Flag_IsMutuallyExclusive()
    {
        var state = new WsRoutingState();
        var key = (Dc: 5, IsMedia: false);

        Assert.True(state.TryBeginRefill(key));
        Assert.False(state.TryBeginRefill(key));
        state.EndRefill(key);
        Assert.True(state.TryBeginRefill(key));
    }

    private sealed class DummyWs : IRawWebSocket
    {
        public Task Send(byte[] data, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendBatch(IReadOnlyList<byte[]> parts, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]?> Recv(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task Close(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
