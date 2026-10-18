using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;

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
public readonly record struct WebProxyCreateResult(
    WebProxyCreateStatus Status,
    string? SessionToken,
    byte[]? Welcome);

/// <summary>
/// Менеджер relay-сессий WEB proxy: выпуск и погашение bootstrap-токенов (2 минуты),
/// создание/закрытие сессий, предел одновременных сессий. Токены — непрозрачные 32-байтные
/// значения в base64url (43 символа); клиент не интерпретирует их раскладку, поэтому
/// происхождение подтверждается хранением в памяти, а не HMAC-подписью.
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
    }

    private const int MaxConcurrentSessions = 4096;

    private static readonly TimeSpan BootstrapTtl = TimeSpan.FromMinutes(2);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BootstrapEntry> _bootstraps =
        new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionRecord> _sessions =
        new(StringComparer.Ordinal);

    private readonly IWebProxyRelaySession _relay;
    private readonly ILogger<WebProxySessionManager> _logger;
    private readonly TimeProvider _time;
    private int _sessionCount;

    public WebProxySessionManager(
        IWebProxyRelaySession relay,
        ILogger<WebProxySessionManager> logger,
        TimeProvider? time = null)
    {
        _relay = relay;
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
    /// Создаёт relay-сессию: погашает bootstrap, валидирует HELLO, резервирует слот и поднимает
    /// жизненный цикл (статистика/реестр) через <see cref="IWebProxyRelaySession"/>.
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
            return new WebProxyCreateResult(WebProxyCreateStatus.NotFound, null, null);
        }

        if (!TryParseHello(hello))
        {
            return new WebProxyCreateResult(WebProxyCreateStatus.NotFound, null, null);
        }

        if (Volatile.Read(ref _sessionCount) >= MaxConcurrentSessions)
        {
            _logger.LogWarning("[{Scope}] web-proxy session limit reached", scope);
            return new WebProxyCreateResult(WebProxyCreateStatus.Busy, null, null);
        }

        var lease = _relay.OpenSession(token, peer, scope);
        var session = new WebProxyHttpSession(lease.StartBridge);
        var id = NewToken();
        _sessions[id] = new SessionRecord { Session = session, Lease = lease };
        Interlocked.Increment(ref _sessionCount);

        // Внешний разрыв (отзыв токена / перезапуск слушателя) закрывает сессию.
        var sessionId = id;
        lease.LifetimeToken.Register(() => CloseSession(sessionId));

        return new WebProxyCreateResult(WebProxyCreateStatus.Ok, id, BuildWelcome());
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

        Interlocked.Decrement(ref _sessionCount);
        _ = record.Session.DisposeAsync().AsTask();
        record.Lease.Dispose();
        return true;
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
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }
}
