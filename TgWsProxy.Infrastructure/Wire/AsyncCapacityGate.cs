#nullable enable

namespace TgWsProxy.Infrastructure.Wire;

/// <summary>
/// Асинхронный ограничитель пропускной способности: следит за числом одновременных запросов и
/// суммарным объёмом данных «в полёте» (порт условий
/// <c>_has_capacity</c>/<c>_reserve</c>/<c>_release</c> из upstream <c>proxy/cf_h2.py</c>).
/// </summary>
/// <param name="maxRequests">Максимум одновременных запросов.</param>
/// <param name="maxBytes">Максимум суммарного объёма зарезервированных байт.</param>
public sealed class AsyncCapacityGate(int maxRequests, long maxBytes)
{
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = [];
    private int _requests;
    private long _bytes;
    private bool _closed;

    /// <summary>Признак того, что ограничитель закрыт и новые резервы невозможны.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>Текущее число зарезервированных запросов.</summary>
    public int ReservedRequests
    {
        get
        {
            lock (_sync)
            {
                return _requests;
            }
        }
    }

    /// <summary>Текущий объём зарезервированных байт.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_sync)
            {
                return _bytes;
            }
        }
    }

    /// <summary>
    /// Проверяет, поместится ли запрос указанного размера без ожидания.
    /// </summary>
    /// <param name="length">Размер запроса в байтах.</param>
    public bool HasCapacity(int length)
    {
        lock (_sync)
        {
            return !_closed && _requests < maxRequests && _bytes + length <= maxBytes;
        }
    }

    /// <summary>
    /// Пытается зарезервировать место под запрос без ожидания.
    /// </summary>
    /// <param name="length">Размер запроса в байтах.</param>
    /// <returns><see langword="true"/>, если место зарезервировано.</returns>
    public bool TryReserve(int length)
    {
        lock (_sync)
        {
            if (_closed || _requests >= maxRequests || _bytes + length > maxBytes)
            {
                return false;
            }

            _requests++;
            _bytes += length;
            return true;
        }
    }

    /// <summary>
    /// Резервирует место под запрос.
    /// </summary>
    /// <param name="length">Размер запроса в байтах.</param>
    /// <param name="wait">Ожидать освобождения места или сразу вернуть <see langword="false"/>.</param>
    /// <param name="cancellationToken">Токен отмены (в upstream — REQUEST_TIMEOUT).</param>
    /// <returns><see langword="true"/>, если место зарезервировано.</returns>
    /// <exception cref="IOException">Ограничитель закрыт.</exception>
    public async Task<bool> ReserveAsync(int length, bool wait, CancellationToken cancellationToken)
    {
        TaskCompletionSource? completion = null;
        lock (_sync)
        {
            if (_closed)
            {
                throw new IOException("H2 capacity gate closed");
            }

            if (_requests < maxRequests && _bytes + length <= maxBytes)
            {
                _requests++;
                _bytes += length;
                return true;
            }

            if (!wait)
            {
                return false;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new Waiter(length, completion));
        }

        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            lock (_sync)
            {
                _waiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion));
            }

            throw;
        }
    }

    /// <summary>
    /// Освобождает ранее зарезервированное место и будит ожидающих.
    /// </summary>
    /// <param name="length">Размер завершённого запроса в байтах.</param>
    public void Release(int length)
    {
        lock (_sync)
        {
            _requests--;
            _bytes -= length;
            WakeLocked();
        }
    }

    /// <summary>
    /// Закрывает ограничитель: ожидающие резервы завершаются ошибкой.
    /// </summary>
    public void Close()
    {
        List<Waiter> waiters;
        lock (_sync)
        {
            _closed = true;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new IOException("H2 capacity gate closed"));
        }
    }

    private void WakeLocked()
    {
        for (var i = 0; i < _waiters.Count;)
        {
            var waiter = _waiters[i];
            if (_requests < maxRequests && _bytes + waiter.Length <= maxBytes)
            {
                _requests++;
                _bytes += waiter.Length;
                _waiters.RemoveAt(i);
                waiter.Completion.TrySetResult();
            }
            else
            {
                i++;
            }
        }
    }

    private sealed record Waiter(int Length, TaskCompletionSource Completion);
}
