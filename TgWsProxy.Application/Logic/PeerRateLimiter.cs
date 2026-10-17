#nullable enable

using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Ограничитель частоты новых подключений с одного IP (fixed window). Защищает от флуда и
/// медленных переборов: при превышении лимита соединение отклоняется до рукопожатия.
/// В upstream ограничения нет, это расширение порта.
/// </summary>
public sealed class PeerRateLimiter
{
    /// <summary>Период между проходами очистки устаревших окон (секунды).</summary>
    private const double SweepIntervalSeconds = 30.0;

    /// <summary>Порог числа отслеживаемых адресов, после которого очистка запускается принудительно.</summary>
    private const int MaxTrackedPeers = 100_000;

    private volatile int _limit;
    private volatile int _windowSeconds;
    private readonly Func<double> _clock;
    private readonly Dictionary<IPAddress, Counter> _counters = [];
    private readonly object _sync = new();
    private double _lastSweep;

    public PeerRateLimiter(Config config)
        : this(config.RateLimitPerIp, config.RateLimitWindowSeconds)
    {
    }

    internal PeerRateLimiter(int limit, int windowSeconds)
        : this(limit, windowSeconds, static () => Environment.TickCount64 / 1000.0)
    {
    }

    /// <summary>
    /// Создаёт ограничитель с явными параметрами и часами (для тестов).
    /// </summary>
    /// <param name="limit">Максимум подключений за окно; 0 или меньше — ограничение выключено.</param>
    /// <param name="windowSeconds">Размер окна в секундах.</param>
    /// <param name="clock">Источник монотонного времени в секундах.</param>
    internal PeerRateLimiter(int limit, int windowSeconds, Func<double> clock)
    {
        _limit = limit;
        _windowSeconds = windowSeconds;
        _clock = clock;
        _lastSweep = clock();
    }

    /// <summary>
    /// Ноль в лимите или окне означает, что ограничение выключено и <see cref="TryAcquire"/> всегда разрешает.
    /// </summary>
    public bool IsEnabled => _limit > 0 && _windowSeconds > 0;

    /// <summary>Максимум подключений за окно.</summary>
    public int Limit => _limit;

    /// <summary>Размер окна в секундах.</summary>
    public int WindowSeconds => _windowSeconds;

    /// <summary>
    /// Меняет лимит и окно на лету (F-2.11: <c>rateLimitPerIp</c> и
    /// <c>rateLimitWindowSeconds</c> применяются без рестарта).
    /// </summary>
    /// <param name="limit">Новый лимит; 0 или меньше — ограничение выключено.</param>
    /// <param name="windowSeconds">Новое окно в секундах; 0 или меньше — ограничение выключено.</param>
    public void UpdateLimits(int limit, int windowSeconds)
    {
        _limit = limit;
        _windowSeconds = windowSeconds;
    }

    /// <summary>Число адресов, по которым сейчас ведётся учёт.</summary>
    public int TrackedPeers
    {
        get
        {
            lock (_sync)
            {
                return _counters.Count;
            }
        }
    }

    /// <summary>
    /// Проверяет, можно ли принять ещё одно подключение с адреса <paramref name="ip"/>.
    /// Возвращает <c>false</c>, если лимит за текущее окно исчерпан.
    /// </summary>
    public bool TryAcquire(IPAddress ip)
    {
        if (!IsEnabled)
        {
            return true;
        }

        // IPv4-mapped IPv6 приводим к IPv4, иначе один клиент считается за два разных адреса.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var now = _clock();

        lock (_sync)
        {
            if (now - _lastSweep >= SweepIntervalSeconds || _counters.Count > MaxTrackedPeers)
            {
                Sweep(now);
            }

            if (_counters.TryGetValue(ip, out var counter) && now - counter.WindowStart < _windowSeconds)
            {
                if (counter.Count >= _limit)
                {
                    return false;
                }

                counter.Count++;
                return true;
            }

            _counters[ip] = new Counter(now);
            return true;
        }
    }

    /// <summary>Удаляет записи, окно которых истекло. Вызывается под <see cref="_sync"/>.</summary>
    private void Sweep(double now)
    {
        _lastSweep = now;

        List<IPAddress>? expired = null;
        foreach (var (ip, counter) in _counters)
        {
            if (now - counter.WindowStart < _windowSeconds)
            {
                continue;
            }

            expired ??= [];
            expired.Add(ip);
        }

        if (expired is null)
        {
            return;
        }

        foreach (var ip in expired)
        {
            _counters.Remove(ip);
        }
    }

    /// <summary>Счётчик одного адреса в пределах текущего окна.</summary>
    private sealed class Counter(double windowStart)
    {
        public double WindowStart { get; } = windowStart;

        public int Count { get; set; } = 1;
    }
}
