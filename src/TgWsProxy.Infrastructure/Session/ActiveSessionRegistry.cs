using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.Session;

/// <summary>
/// Реестр живых сессий (F-7.4). Сессии хранятся в <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// а разрыв выполняется закрытием клиентского сокета либо вызовом действия отмены (WEB proxy
/// поверх WebSocket): обработчик сессии в этот момент ждёт данных в <c>ReceiveAsync</c> и выходит
/// по исключению сам, без отдельного механизма отмены.
/// </summary>
public sealed class ActiveSessionRegistry(ILogger<ActiveSessionRegistry> logger) : IActiveSessionRegistry
{
    private readonly ConcurrentDictionary<long, SessionEntry> _sessions = new();
    private long _seq;

    /// <inheritdoc />
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public ISessionLease Register(Socket client, string scope)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Register(() => AbortSocket(client), scope);
    }

    /// <inheritdoc />
    public ISessionLease Register(Action abort, string scope)
    {
        ArgumentNullException.ThrowIfNull(abort);

        var id = Interlocked.Increment(ref _seq);
        var entry = new SessionEntry(abort, scope);
        _sessions[id] = entry;
        return new SessionLease(this, id, entry);
    }

    /// <inheritdoc />
    public int CloseAll() => CloseWhere(null);

    /// <inheritdoc />
    public int CloseByToken(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        return CloseWhere(tokenId);
    }

    /// <inheritdoc />
    public int CountByToken(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        var count = 0;
        foreach (var entry in _sessions.Values)
        {
            if (string.Equals(entry.TokenId, tokenId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Разрывает подходящие сессии. Сначала запись удаляется из словаря (чтобы счётчик
    /// не «протекал» при повторном вызове), затем вызывается действие разрыва.
    /// </summary>
    private int CloseWhere(string? tokenId)
    {
        var closed = 0;

        foreach (var (id, entry) in _sessions)
        {
            if (tokenId is not null && !string.Equals(entry.TokenId, tokenId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_sessions.TryRemove(id, out var removed))
            {
                continue;
            }

            Abort(removed);
            closed++;
        }

        if (closed > 0)
        {
            logger.LogInformation(
                "{Closed} active session(s) closed ({Reason})",
                closed,
                tokenId is null ? "listener restart" : $"token {tokenId} revoked");
        }

        return closed;
    }

    /// <summary>Снимает сессию с учёта без разрыва (штатное завершение).</summary>
    private void Unregister(long id) => _sessions.TryRemove(id, out _);

    private static void Abort(SessionEntry entry)
    {
        try
        {
            entry.AbortAction();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Гонка между штатным завершением (освобождением CTS) и принудительным разрывом:
            // действие уже не может выполниться, что эквивалентно уже разорванной сессии.
        }
    }

    private static void AbortSocket(Socket client)
    {
        try
        {
            // Shutdown переводит сокет в состояние, при котором ожидающее чтение завершается
            // сразу, а не после таймаута.
            client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Сокет уже закрыт клиентом или обработчиком — это нормальный сценарий гонки.
        }

        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Повторное закрытие безопасно.
        }
    }

    private sealed class SessionEntry(Action abortAction, string scope)
    {
        /// <summary>Действие, разрывающее сессию (закрытие сокета либо отмена relay-потока).</summary>
        public Action AbortAction { get; } = abortAction;

        public string Scope { get; } = scope;

        /// <summary>Токен доступа прокси, которым прошло рукопожатие (F-8.7).</summary>
        public volatile string? TokenId;
    }

    private sealed class SessionLease(ActiveSessionRegistry owner, long id, SessionEntry entry) : ISessionLease
    {
        private int _released;

        public string? TokenId => entry.TokenId;

        public void AssignToken(string? tokenId) => entry.TokenId = tokenId;

        public void Dispose()
        {
            // Освобождение идемпотентно: сессию могли разорвать через CloseAll.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Unregister(id);
            }
        }
    }
}
