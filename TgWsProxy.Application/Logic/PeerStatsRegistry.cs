using System.Collections.Concurrent;
using System.Net;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Учёт статистики по парам «IP-адрес, токен доступа» (требования F-4 и F-9). Устроен как
/// <see cref="PeerRateLimiter"/>, но горячий путь (пересылка байт) не берёт блокировку:
/// ключи лежат в <see cref="ConcurrentDictionary{TKey,TValue}"/>, а счётчики меняются
/// через <see cref="Interlocked"/>. Фоновая очистка удаляет пары без активных соединений,
/// у которых истёк TTL, и удерживает размер реестра в пределах <see cref="MaxTrackedPeers"/>.
/// </summary>
public sealed class PeerStatsRegistry : IPeerStatsRegistry
{
    /// <summary>Предел по умолчанию для числа отслеживаемых пар (ключ <c>perIpStatsMaxPeers</c>).</summary>
    public const int DefaultMaxTrackedPeers = 100_000;

    /// <summary>Предел числа отслеживаемых пар: защита памяти от сканирования портов.</summary>
    public const int MaxTrackedPeers = DefaultMaxTrackedPeers;

    /// <summary>Период между проходами очистки устаревших пар (секунды).</summary>
    public const double SweepIntervalSeconds = 30.0;
    private readonly double _ttlSeconds;
    private readonly Func<double> _clock;
    private readonly Func<DateTimeOffset> _wallClock;
    private readonly IProxyTokenRegistry? _tokens;
    private readonly ConcurrentDictionary<PeerKey, PeerCounter> _counters = new();
    private readonly object _sweepSync = new();

    private long _tracked;
    private long _overflow;
    private double _lastSweep;

    /// <summary>Создаёт реестр по конфигурации.</summary>
    /// <param name="config">Конфигурация: включённость учёта, TTL и предел числа пар.</param>
    /// <param name="tokens">
    /// Реестр токенов для подстановки имени в снимок (F-9.6: в метрики и API попадает имя,
    /// никогда не секрет). Может быть <see langword="null"/> — тогда имя не заполняется.
    /// </param>
    public PeerStatsRegistry(Config config, IProxyTokenRegistry? tokens = null)
        : this(
            config.EnablePerIpStats,
            config.PerIpStatsTtlSeconds,
            config.PerIpStatsMaxPeers,
            static () => Environment.TickCount64 / 1000.0,
            static () => DateTimeOffset.UtcNow,
            tokens)
    {
    }

    internal PeerStatsRegistry(bool enabled, int ttlSeconds, int maxTrackedPeers = DefaultMaxTrackedPeers)
        : this(
            enabled,
            ttlSeconds,
            maxTrackedPeers,
            static () => Environment.TickCount64 / 1000.0,
            static () => DateTimeOffset.UtcNow,
            null)
    {
    }

    /// <summary>
    /// Создаёт реестр с явными параметрами и часами (для тестов).
    /// </summary>
    /// <param name="enabled">Включён ли учёт.</param>
    /// <param name="ttlSeconds">TTL записи без активных соединений; 0 — записи не удаляются.</param>
    /// <param name="clock">Источник монотонного времени в секундах.</param>
    /// <param name="wallClock">Источник календарного времени для <c>firstSeen</c>/<c>lastSeen</c>.</param>
    internal PeerStatsRegistry(bool enabled, int ttlSeconds, Func<double> clock, Func<DateTimeOffset> wallClock)
        : this(enabled, ttlSeconds, DefaultMaxTrackedPeers, clock, wallClock, null)
    {
    }

    /// <summary>
    /// Создаёт реестр с явными параметрами, пределом пар и часами (для тестов).
    /// </summary>
    /// <param name="enabled">Включён ли учёт.</param>
    /// <param name="ttlSeconds">TTL записи без активных соединений; 0 — записи не удаляются.</param>
    /// <param name="maxTrackedPeers">Предел числа отслеживаемых пар.</param>
    /// <param name="clock">Источник монотонного времени в секундах.</param>
    /// <param name="wallClock">Источник календарного времени для <c>firstSeen</c>/<c>lastSeen</c>.</param>
    /// <param name="tokens">Реестр токенов для подстановки имени в снимок.</param>
    internal PeerStatsRegistry(
        bool enabled,
        int ttlSeconds,
        int maxTrackedPeers,
        Func<double> clock,
        Func<DateTimeOffset> wallClock,
        IProxyTokenRegistry? tokens)
    {
        IsEnabled = enabled;
        _ttlSeconds = ttlSeconds;
        MaxPeers = maxTrackedPeers > 0 ? maxTrackedPeers : DefaultMaxTrackedPeers;
        _clock = clock;
        _wallClock = wallClock;
        _tokens = tokens;
        _lastSweep = clock();
    }

    /// <inheritdoc />
    public bool IsEnabled { get; }

    /// <summary>Действующий предел числа отслеживаемых пар.</summary>
    public int MaxPeers { get; }

    /// <inheritdoc />
    public long TrackedPeers => Interlocked.Read(ref _tracked);

    /// <inheritdoc />
    public long OverflowCount => Interlocked.Read(ref _overflow);

    /// <inheritdoc />
    public void ConnectionOpened(IPAddress peer)
    {
        if (!IsEnabled)
        {
            return;
        }

        var counter = GetOrCreate(peer, null);
        if (counter is null)
        {
            return;
        }

        Interlocked.Increment(ref counter.Total);
        Interlocked.Increment(ref counter.Active);
        Touch(counter);
    }

    /// <inheritdoc />
    public void BindToken(IPAddress? peer, string? tokenId)
    {
        if (!IsEnabled || peer is null || string.IsNullOrEmpty(tokenId))
        {
            return;
        }

        var ip = Normalize(peer);
        if (!_counters.TryGetValue(new PeerKey(ip, null), out var source))
        {
            // Открытие не учитывалось (переполнение реестра либо учёт включили позже).
            return;
        }

        var target = GetOrCreate(ip, tokenId);
        if (target is null)
        {
            return;
        }

        // Одно открытие и одно активное соединение переезжают в пару с токеном (F-9.1):
        // сумма по IP при этом не меняется.
        Decrement(ref source.Total);
        Decrement(ref source.Active);
        Interlocked.Increment(ref target.Total);
        Interlocked.Increment(ref target.Active);
        Touch(target);
    }

    /// <inheritdoc />
    public void ConnectionClosed(IPAddress? peer, string? tokenId)
    {
        if (!IsEnabled || peer is null || !TryGet(peer, tokenId, out var counter))
        {
            return;
        }

        Decrement(ref counter.Active);
        Touch(counter);
    }

    /// <inheritdoc />
    public void RecordBlocked(IPAddress peer)
    {
        if (GetOrCreate(peer, null) is { } counter)
        {
            Interlocked.Increment(ref counter.Blocked);
            Touch(counter);
        }
    }

    /// <inheritdoc />
    public void RecordLimited(IPAddress peer)
    {
        if (GetOrCreate(peer, null) is { } counter)
        {
            Interlocked.Increment(ref counter.Limited);
            Touch(counter);
        }
    }

    /// <inheritdoc />
    public void RecordRateLimited(IPAddress peer)
    {
        if (GetOrCreate(peer, null) is { } counter)
        {
            Interlocked.Increment(ref counter.RateLimited);
            Touch(counter);
        }
    }

    /// <inheritdoc />
    public void AddBytesUp(IPAddress? peer, string? tokenId, long bytes)
    {
        // Горячий путь: без блокировок и без обновления lastSeen (активная сессия и так
        // защищена от вытеснения счётчиком Active, а закрытие сессии обновит метку).
        if (bytes > 0 && TryGet(peer, tokenId, out var counter))
        {
            Interlocked.Add(ref counter.BytesUp, bytes);
        }
    }

    /// <inheritdoc />
    public void AddBytesDown(IPAddress? peer, string? tokenId, long bytes)
    {
        if (bytes > 0 && TryGet(peer, tokenId, out var counter))
        {
            Interlocked.Add(ref counter.BytesDown, bytes);
        }
    }

    /// <inheritdoc />
    public void RecordWsError(IPAddress? peer, string? tokenId)
    {
        if (TryGet(peer, tokenId, out var counter))
        {
            Interlocked.Increment(ref counter.WsErrors);
            Touch(counter);
        }
    }

    /// <inheritdoc />
    public void RecordPoolHit(IPAddress? peer, string? tokenId)
    {
        if (TryGet(peer, tokenId, out var counter))
        {
            Interlocked.Increment(ref counter.PoolHits);
        }
    }

    /// <inheritdoc />
    public void RecordPoolMiss(IPAddress? peer, string? tokenId)
    {
        if (TryGet(peer, tokenId, out var counter))
        {
            Interlocked.Increment(ref counter.PoolMisses);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PeerStatsSnapshot> Snapshot()
    {
        if (!IsEnabled)
        {
            return [];
        }

        var nowMono = _clock();
        var nowWall = _wallClock();
        var result = new List<PeerStatsSnapshot>(_counters.Count);

        // Имена токенов разрешаются один раз на снимок, а не на каждую пару (P0-1):
        // линейный Find по массиву токенов на сотни тысяч пар давал миллионы сравнений
        // строк на каждый сбор метрик.
        var tokenNames = BuildTokenNames();

        foreach (var (key, counter) in _counters)
        {
            var snapshot = ToSnapshot(key, counter, nowMono, nowWall, tokenNames);

            // После BindToken запись (peer, null) остаётся в словаре с нулевыми счётчиками
            // (F-9.1): счётчики переехали в пару с токеном. Такая запись не несёт информации
            // и засоряла бы ответы /api/per-ip и /metrics, поэтому в снимок не попадает.
            if (!IsEmpty(snapshot))
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    /// <summary>Запись без единого события: ни соединений, ни байт, ни отказов.</summary>
    private static bool IsEmpty(PeerStatsSnapshot snapshot) =>
        snapshot.ActiveConnections == 0
        && snapshot.TotalConnections == 0
        && snapshot.BlockedConnections == 0
        && snapshot.LimitedConnections == 0
        && snapshot.RateLimitedConnections == 0
        && snapshot.BytesUp == 0
        && snapshot.BytesDown == 0
        && snapshot.WsErrors == 0
        && snapshot.PoolHits == 0
        && snapshot.PoolMisses == 0;

    /// <summary>Собирает снимок одной пары с согласованным чтением счётчиков.</summary>
    private PeerStatsSnapshot ToSnapshot(
        PeerKey key,
        PeerCounter counter,
        double nowMono,
        DateTimeOffset nowWall,
        Dictionary<string, string>? tokenNames)
    {
        var lastSeenMono = Interlocked.CompareExchange(ref counter.LastSeenMono, 0, 0);
        var firstSeenMono = Interlocked.CompareExchange(ref counter.FirstSeenMono, 0, 0);

        // Оба поля монотонные: переводим в календарное время через текущую точку отсчёта,
        // чтобы перевод часов не давал «записей из будущего».
        var lastSeen = nowWall - TimeSpan.FromSeconds(Math.Max(0, nowMono - lastSeenMono));
        var firstSeen = nowWall - TimeSpan.FromSeconds(Math.Max(0, nowMono - firstSeenMono));

        return new PeerStatsSnapshot(
            key.Ip.ToString(),
            key.TokenId,
            ResolveTokenName(key.TokenId, tokenNames),
            Interlocked.Read(ref counter.Active),
            Interlocked.Read(ref counter.Total),
            Interlocked.Read(ref counter.Blocked),
            Interlocked.Read(ref counter.Limited),
            Interlocked.Read(ref counter.RateLimited),
            Interlocked.Read(ref counter.BytesUp),
            Interlocked.Read(ref counter.BytesDown),
            Interlocked.Read(ref counter.WsErrors),
            Interlocked.Read(ref counter.PoolHits),
            Interlocked.Read(ref counter.PoolMisses),
            firstSeen,
            lastSeen);
    }

    /// <summary>Строит карту <c>id → имя</c> один раз на снимок (P0-1).</summary>
    private Dictionary<string, string>? BuildTokenNames()
    {
        if (_tokens is null)
        {
            return null;
        }

        var tokens = _tokens.All;
        if (tokens.Count == 0)
        {
            return null;
        }

        var names = new Dictionary<string, string>(tokens.Count, StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            names[token.Id] = token.Name;
        }

        return names;
    }

    /// <summary>Имя токена для отображения; <see langword="null"/> для записей без токена и удалённых токенов.</summary>
    private static string? ResolveTokenName(string? tokenId, Dictionary<string, string>? tokenNames)
    {
        if (string.IsNullOrEmpty(tokenId) || tokenNames is null)
        {
            return null;
        }

        return tokenNames.TryGetValue(tokenId, out var name) ? name : null;
    }

    private bool TryGet(
        IPAddress? peer,
        string? tokenId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PeerCounter? counter)
    {
        counter = null;
        if (!IsEnabled || peer is null)
        {
            return false;
        }

        return _counters.TryGetValue(new PeerKey(Normalize(peer), tokenId), out counter);
    }

    /// <summary>
    /// Возвращает запись пары, создавая её при необходимости. Возвращает <see langword="null"/>,
    /// если реестр переполнен: новые пары не учитываются, растёт <see cref="OverflowCount"/>.
    /// </summary>
    private PeerCounter? GetOrCreate(IPAddress peer, string? tokenId)
    {
        var key = new PeerKey(Normalize(peer), tokenId);
        if (_counters.TryGetValue(key, out var existing))
        {
            return existing;
        }

        MaybeSweep();

        if (Interlocked.Read(ref _tracked) >= MaxPeers)
        {
            Interlocked.Increment(ref _overflow);
            return null;
        }

        var created = new PeerCounter(_clock());
        var actual = _counters.GetOrAdd(key, created);
        if (ReferenceEquals(actual, created))
        {
            Interlocked.Increment(ref _tracked);
        }

        return actual;
    }

    /// <summary>Обновляет метку активности пары (монотонное время).</summary>
    private void Touch(PeerCounter counter)
    {
        Interlocked.Exchange(ref counter.LastSeenMono, _clock());
        MaybeSweep();
    }

    /// <summary>Атомарно уменьшает счётчик, не позволяя ему уйти в минус.</summary>
    private static void Decrement(ref long field)
    {
        while (true)
        {
            var current = Interlocked.Read(ref field);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref field, current - 1, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Запускает очистку не чаще <see cref="SweepIntervalSeconds"/>; один проход за раз.</summary>
    private void MaybeSweep()
    {
        if (_ttlSeconds <= 0)
        {
            return;
        }

        var now = _clock();
        if (now - Volatile.Read(ref _lastSweep) < SweepIntervalSeconds)
        {
            return;
        }

        // Быстрая проверка снаружи, чтобы не брать блокировку на каждом байте.
        if (!Monitor.TryEnter(_sweepSync))
        {
            return;
        }

        try
        {
            if (now - _lastSweep < SweepIntervalSeconds)
            {
                return;
            }

            _lastSweep = now;
            Sweep(now);
        }
        finally
        {
            Monitor.Exit(_sweepSync);
        }
    }

    /// <summary>Удаляет пары без активных соединений, у которых истёк TTL. Вызывается под блокировкой.</summary>
    private void Sweep(double now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (Interlocked.Read(ref counter.Active) > 0)
            {
                continue;
            }

            var lastSeen = Interlocked.CompareExchange(ref counter.LastSeenMono, 0, 0);
            if (now - lastSeen < _ttlSeconds)
            {
                continue;
            }

            if (_counters.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _tracked);
            }
        }
    }

    /// <summary>IPv4-mapped IPv6 приводим к IPv4, иначе один клиент считается за два адреса.</summary>
    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Ключ учёта: пара «адрес, токен» (F-9.1); токен <see langword="null"/> до handshake.</summary>
    private readonly record struct PeerKey(IPAddress Ip, string? TokenId);

    /// <summary>Счётчики одной пары. Поля меняются только через <see cref="Interlocked"/>.</summary>
    private sealed class PeerCounter(double firstSeenMono)
    {
        public double FirstSeenMono = firstSeenMono;
        public double LastSeenMono = firstSeenMono;
        public long Active;
        public long Total;
        public long Blocked;
        public long Limited;
        public long RateLimited;
        public long BytesUp;
        public long BytesDown;
        public long WsErrors;
        public long PoolHits;
        public long PoolMisses;
    }
}
