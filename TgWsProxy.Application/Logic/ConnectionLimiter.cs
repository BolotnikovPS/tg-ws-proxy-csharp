#nullable enable

using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Глобальный ограничитель одновременных клиентских сессий. Защищает от исчерпания сокетов и
/// дескрипторов при наплыве подключений: лимит проверяется до рукопожатия, а слот удерживается
/// всю сессию (в upstream ограничения нет, это расширение порта).
/// </summary>
public sealed class ConnectionLimiter
{
    private long _active;
    private volatile int _limit;

    public ConnectionLimiter(Config config)
        : this(config.MaxConnections)
    {
    }

    internal ConnectionLimiter(int limit) => _limit = limit;

    /// <summary>
    /// Ноль означает отсутствие ограничения: слоты всё равно учитываются, но не отклоняются.
    /// </summary>
    public bool IsEnabled => Limit > 0;

    /// <summary>
    /// Текущее число занятых слотов (активные клиентские сессии, прошедшие ограничитель).
    /// </summary>
    public long Active => Interlocked.Read(ref _active);

    /// <summary>
    /// Максимальное число одновременно удерживаемых слотов.
    /// </summary>
    public int Limit => _limit;

    /// <summary>
    /// Меняет лимит на лету (F-2.11: <c>maxConnections</c> применяется без рестарта).
    /// Уже занятые слоты не освобождаются — новый лимит действует для новых сессий.
    /// </summary>
    /// <param name="limit">Новый лимит; 0 — без ограничения.</param>
    public void UpdateLimit(int limit) => _limit = limit;

    /// <summary>
    /// Пытается занять слот под новую сессию. Возвращает <c>false</c>, если лимит исчерпан;
    /// в этом случае вызывающая сторона не должна освобождать слот через <see cref="Release"/>.
    /// </summary>
    public bool TryAcquire()
    {
        if (Limit <= 0)
        {
            Interlocked.Increment(ref _active);
            return true;
        }

        while (true)
        {
            var current = Interlocked.Read(ref _active);
            if (current >= Limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Освобождает ранее занятый слот. Повторный вызов для одной сессии безопасен и не уводит
    /// счётчик в минус.
    /// </summary>
    public void Release()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _active);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _active, current - 1, current) == current)
            {
                return;
            }
        }
    }
}
