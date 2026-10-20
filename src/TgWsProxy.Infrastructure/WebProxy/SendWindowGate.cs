namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Байт-гранулярный затвор окна отправки: писатель забирает кредит перед отправкой <c>Data</c>
/// и блокируется, пока противоположная сторона не вернёт кредит кадром <c>Window</c>.
/// Один писатель на поток; кредит добавляется из inbound-задачи мультиплексора.
/// </summary>
internal sealed class SendWindowGate
{
    private readonly object _sync = new();
    private long _available;
    private TaskCompletionSource? _waiter;

    public SendWindowGate(long initial) => _available = initial;

    /// <summary>Возвращает доступный кредит (&gt; 0), блокируясь до его появления.</summary>
    public async ValueTask<long> WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource waiter;
            lock (_sync)
            {
                if (_available > 0)
                {
                    return _available;
                }

                waiter = _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Забирает <paramref name="bytes"/> кредита (не более доступного).</summary>
    public void Consume(long bytes)
    {
        lock (_sync)
        {
            _available -= Math.Min(bytes, _available);
        }
    }

    /// <summary>Возвращает кредит (кадр <c>Window</c> от противоположной стороны).</summary>
    public void Add(long bytes)
    {
        TaskCompletionSource? wake = null;
        lock (_sync)
        {
            _available += bytes;
            if (_available > 0 && _waiter is not null)
            {
                wake = _waiter;
                _waiter = null;
            }
        }

        wake?.TrySetResult();
    }
}
