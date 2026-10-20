using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>Исход создания relay-сессии: статус и, при успехе, токен и WELCOME-кадр.</summary>
public enum WebProxyCreateStatus
{
    /// <summary>Сессия создана.</summary>
    Ok,

    /// <summary>Неверный/просроченный bootstrap либо недопустимый HELLO (404).</summary>
    NotFound,

    /// <summary>Достигнут предел одновременных сессий (503 + Retry-After).</summary>
    Busy,
}

/// <summary>Результат <see cref="WebProxySessionManager.CreateSession"/>.</summary>
/// <param name="Status">Исход создания.</param>
/// <param name="SessionToken">Токен сессии (43 символа base64url) при успехе.</param>
/// <param name="Welcome">Готовый WELCOME-кадр при успехе.</param>
/// <param name="CarrierMode">
/// Носитель, зафиксированный за сессией: клиент обязан использовать именно его, значение
/// возвращается в заголовке <c>X-Carrier-Mode</c> (F-W.24).
/// </param>
public readonly record struct WebProxyCreateResult(
    WebProxyCreateStatus Status,
    string? SessionToken,
    byte[]? Welcome,
    string CarrierMode);

/// <summary>
/// Менеджер relay-сессий WEB proxy: выпуск и погашение bootstrap-токенов (2 минуты),
/// создание/закрытие сессий, соблюдение лимитов ресурсов (<c>webProxyMaxSessions</c>,
/// <c>webProxyMaxSessionsPerIp</c>, F-W.25). Токены — непрозрачные 32-байтные значения в
/// base64url (43 символа); клиент не интерпретирует их раскладку, поэтому происхождение
/// подтверждается хранением в памяти, а не HMAC-подписью.
/// </summary>
public sealed class WebProxySessionManager
{
    private sealed class BootstrapEntry
    {
        public required ProxyTokenEntry Token;
        public required long ExpiresAtTicks;
    }

    private sealed class SessionRecord
    {
        public required WebProxyHttpSession Session;
        public required WebProxyRelayLease Lease;
        public IPAddress? Peer;
    }

    private static readonly TimeSpan BootstrapTtl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, BootstrapEntry> _bootstraps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IPAddress, int> _sessionsPerIp = new();
    private readonly IWebProxyRelaySession _relay;
    private readonly WebProxyStreamBudget _budget;
    private readonly Config _config;
    private readonly ILogger<WebProxySessionManager> _logger;
    private readonly TimeProvider _time;
    private int _sessionCount;

    public WebProxySessionManager(
        IWebProxyRelaySession relay,
        WebProxyStreamBudget budget,
        Config config,
        ILogger<WebProxySessionManager> logger,
        TimeProvider? time = null)
    {
        _relay = relay;
        _budget = budget;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Число живых сессий (для тестов и диагностики).</summary>
    public int SessionCount => Volatile.Read(ref _sessionCount);

    /// <summary>Выпускает одноразовый bootstrap-токен для токена MTProxy (на отрисовку bridge-страницы).</summary>
    public string IssueBootstrap(ProxyTokenEntry token)
    {
        PruneExpiredBootstraps();
        var id = NewToken();
        _bootstraps[id] = new BootstrapEntry
        {
            Token = token,
            ExpiresAtTicks = _time.GetUtcNow().Add(BootstrapTtl).UtcTicks,
        };
        return id;
    }

    /// <summary>
    /// Создаёт relay-сессию: погашает bootstrap, валидирует HELLO, соблюдает лимиты сессий
    /// (глобальный и по IP) и поднимает жизненный цикл (статистика/реестр) через
    /// <see cref="IWebProxyRelaySession"/>.
    /// </summary>
    /// <param name="bootstrap">Одноразовый bootstrap-токен из bridge-страницы.</param>
    /// <param name="hello">Тело <c>POST /api/v1/session</c> — единственный HELLO-кадр.</param>
    /// <param name="peer">IP клиента (из X-Forwarded-For или loopback).</param>
    /// <param name="scope">Метка для логов/реестра.</param>
    public WebProxyCreateResult CreateSession(
        string bootstrap,
        ReadOnlyMemory<byte> hello,
        IPAddress? peer,
        string scope)
    {
        if (!TryConsumeBootstrap(bootstrap, out var token))
        {
            return new WebProxyCreateResult(WebProxyCreateStatus.NotFound, null, null, CurrentCarrierMode);
        }

        if (!TryParseHello(hello))
        {
            return new WebProxyCreateResult(WebProxyCreateStatus.NotFound, null, null, CurrentCarrierMode);
        }

        if (!TryReserveSession(peer, scope))
        {
            return new WebProxyCreateResult(WebProxyCreateStatus.Busy, null, null, CurrentCarrierMode);
        }

        var carrierMode = CurrentCarrierMode;

        try
        {
            var lease = _relay.OpenSession(token, peer, scope);
            var session = new WebProxyHttpSession(
                lease.StartBridge,
                _config.WebProxyMaxStreamsPerSession,
                _budget.TryAcquire,
                _budget.Release,
                _config.WebProxyMaxPendingBytesPerSession,
                _config.WebProxyMaxPendingItemsPerSession)
            {
                CarrierMode = carrierMode,
            };

            var id = NewToken();
            _sessions[id] = new SessionRecord { Session = session, Lease = lease, Peer = peer };

            // Внешний разрыв (отзыв токена / перезапуск слушателя) закрывает сессию.
            var sessionId = id;
            lease.LifetimeToken.Register(() => CloseSession(sessionId));

            return new WebProxyCreateResult(WebProxyCreateStatus.Ok, id, BuildWelcome(), carrierMode);
        }
        catch
        {
            ReleaseSessionSlot(peer);
            throw;
        }
    }

    /// <summary>Возвращает живую сессию по session-токену либо <see langword="null"/>.</summary>
    public WebProxyHttpSession? GetSession(string sessionToken)
        => _sessions.TryGetValue(sessionToken, out var record) ? record.Session : null;

    /// <summary>Закрывает сессию и возвращает признак того, что она существовала.</summary>
    public bool CloseSession(string sessionToken)
    {
        if (!_sessions.TryRemove(sessionToken, out var record))
        {
            return false;
        }

        ReleaseSessionSlot(record.Peer);
        _ = record.Session.DisposeAsync().AsTask();
        record.Lease.Dispose();
        return true;
    }

    /// <summary>
    /// Носитель для новых сессий в текущий момент. Значение фиксируется в сессии и в ответе
    /// <c>POST /api/v1/session</c>, поэтому горячая правка применяется к следующим сессиям.
    /// </summary>
    public string CurrentCarrierMode
    {
        get
        {
            var value = _config.WebProxyCarrierMode?.Trim();
            return string.IsNullOrEmpty(value) ? "https" : value;
        }
    }

    /// <summary>
    /// Резервирует слот сессии с учётом глобального предела и предела на IP. Счётчики
    /// увеличиваются до создания сессии и откатываются при отказе, чтобы параллельные запросы
    /// не проскочили лимит.
    /// </summary>
    private bool TryReserveSession(IPAddress? peer, string scope)
    {
        var limit = _config.WebProxyMaxSessions;
        var current = Interlocked.Increment(ref _sessionCount);
        if (limit > 0 && current > limit)
        {
            Interlocked.Decrement(ref _sessionCount);
            _logger.LogWarning("[{Scope}] web-proxy session limit reached ({Limit})", scope, limit);
            return false;
        }

        var perIpLimit = _config.WebProxyMaxSessionsPerIp;
        if (perIpLimit <= 0 || peer is null)
        {
            return true;
        }

        var count = _sessionsPerIp.AddOrUpdate(peer, 1, static (_, value) => value + 1);
        if (count > perIpLimit)
        {
            _sessionsPerIp.AddOrUpdate(peer, 0, static (_, value) => value - 1);
            Interlocked.Decrement(ref _sessionCount);
            _logger.LogWarning(
                "[{Scope}] web-proxy per-IP session limit reached for {Peer} ({Limit})",
                scope,
                peer,
                perIpLimit);
            return false;
        }

        return true;
    }

    private void ReleaseSessionSlot(IPAddress? peer)
    {
        Interlocked.Decrement(ref _sessionCount);

        if (peer is null)
        {
            return;
        }

        _sessionsPerIp.AddOrUpdate(peer, 0, static (_, value) => value - 1);
    }

    private bool TryConsumeBootstrap(string bootstrap, out ProxyTokenEntry token)
    {
        if (_bootstraps.TryRemove(bootstrap, out var entry)
            && entry.ExpiresAtTicks > _time.GetUtcNow().UtcTicks)
        {
            token = entry.Token;
            return true;
        }

        token = null!;
        return false;
    }

    private void PruneExpiredBootstraps()
    {
        var now = _time.GetUtcNow().UtcTicks;
        foreach (var pair in _bootstraps)
        {
            if (pair.Value.ExpiresAtTicks <= now)
            {
                _bootstraps.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool TryParseHello(ReadOnlyMemory<byte> hello)
    {
        // Единственный HELLO-кадр: stream 0, payload = [0x01] (версия протокола 1).
        if (hello.Length != RelayFrameCodec.FrameSize(1)
            || !RelayFrameCodec.TryParse(hello, out var frame, out _))
        {
            return false;
        }

        return frame.Type == RelayFrameType.Hello
            && frame.StreamId == 0
            && frame.Payload.Length == 1
            && frame.Payload.Span[0] == RelayFrameCodec.HelloProtocolVersion;
    }

    private static byte[] BuildWelcome()
    {
        var welcome = new byte[RelayFrameCodec.FrameHeaderSize];
        RelayFrameCodec.WriteHeader(welcome, RelayFrameType.Welcome, 0, 0);
        return welcome;
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
