using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Глобальный бюджет одновременных relay-потоков WEB proxy через все сессии
/// (<c>webProxy.maxStreams</c>, F-W.25). Значение читается из живого конфига на каждой попытке,
/// поэтому горячая правка лимита действует без перезапуска.
/// </summary>
public sealed class WebProxyStreamBudget(Config config)
{
    private int _active;

    /// <summary>Сколько потоков занято прямо сейчас (для метрик и диагностики).</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// Резервирует слот потока. Возвращает <see langword="false"/>, если бюджет исчерпан —
    /// вызывающий отклоняет OPEN кадром <c>CLOSE</c>.
    /// </summary>
    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            var limit = config.WebProxy.MaxStreams;
            if (limit > 0 && current >= limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Возвращает ранее зарезервированный слот.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _active) < 0)
        {
            // Парный Release без Acquire не должен «уводить» бюджет в минус.
            Interlocked.Increment(ref _active);
        }
    }
}
