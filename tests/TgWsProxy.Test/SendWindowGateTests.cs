using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>Тесты байт-гранулярного затвора окна отправки (flow-control).</summary>
public class SendWindowGateTests
{
    [Fact]
    public async Task WaitAsync_ReturnsInitialCredit()
    {
        var gate = new SendWindowGate(100);

        var credit = await gate.WaitAsync(CancellationToken.None);

        Assert.Equal(100, credit);
    }

    [Fact]
    public async Task Consume_ReducesCredit()
    {
        var gate = new SendWindowGate(100);
        gate.Consume(30);
        gate.Consume(70);

        var remaining = gate.WaitAsync(CancellationToken.None).AsTask();
        Assert.False(remaining.IsCompleted); // 0 осталось — блокируется
        gate.Add(1);
        Assert.Equal(1, await remaining);
    }

    [Fact]
    public async Task WaitAsync_BlocksUntilCreditAdded()
    {
        var gate = new SendWindowGate(0);
        var waiter = gate.WaitAsync(CancellationToken.None).AsTask();

        Assert.False(waiter.IsCompleted);

        gate.Add(10);
        var credit = await waiter;

        Assert.Equal(10, credit);
    }

    [Fact]
    public async Task Consume_CapsAtAvailable()
    {
        var gate = new SendWindowGate(5);
        gate.Consume(10); // доступно только 5

        var waiter = gate.WaitAsync(CancellationToken.None).AsTask();
        Assert.False(waiter.IsCompleted);

        gate.Add(2);
        Assert.Equal(2, await waiter);
    }

    [Fact]
    public async Task WaitAsync_HonorsCancellation()
    {
        var gate = new SendWindowGate(0);
        using var cts = new CancellationTokenSource();

        var waiter = gate.WaitAsync(cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }
}
