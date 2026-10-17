#nullable enable

using System.Net;

namespace TgWsProxy.Application;

/// <summary>
/// Контекст одного принятого клиентского соединения.
/// </summary>
/// <param name="Scope">Метка сессии для логов (<c>peer|connectionId</c>).</param>
/// <param name="Peer">Адрес клиента в виде строки (с портом) — как в логах upstream.</param>
/// <param name="ConnectionId">Короткий идентификатор соединения.</param>
/// <param name="Address">Адрес клиента без порта — ключ статистики по IP (может быть <see langword="null"/>).</param>
/// <param name="TokenId">
/// Идентификатор токена доступа, выбранного на handshake (F-8.4, F-9.2). До рукопожатия —
/// <see langword="null"/>: токен неизвестен на этапе accept. Обработчик сессии создаёт копию
/// контекста через <c>with</c>, как только токен определён.
/// </param>
public sealed record ClientContext(
    string Scope,
    string Peer,
    string ConnectionId,
    IPAddress? Address = null,
    string? TokenId = null);
