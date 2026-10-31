using TgWsProxy.Application.ReadModels;

namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Запрос списка токенов доступа для <c>GET /api/tokens</c> (F-8.8). Секреты всегда маскированы (N-8).
/// </summary>
public sealed record GetTokensQuery;

/// <summary>
/// Команда создания токена (F-8.8): пустой секрет означает серверную генерацию.
/// </summary>
/// <param name="Name">Имя токена (1…64 символа после обрезки пробелов).</param>
/// <param name="Secret">Секрет в 32 hex-символа либо <see langword="null"/> для генерации.</param>
/// <param name="Enabled">Признак включённости; <see langword="null"/> — включён.</param>
public sealed record CreateTokenCommand(string? Name, string? Secret, bool? Enabled);

/// <summary>Ответ на создание токена: секрет выдаётся открыто один раз.</summary>
/// <param name="Token">Созданный токен с открытым секретом.</param>
public sealed record CreateTokenResult(TokenDto Token);

/// <summary>
/// Команда изменения токена (F-8.8, F-8.9). Поле со значением <see langword="null"/> не меняется,
/// а маскированный секрет означает «оставить прежний» — его восстанавливает сервис конфигурации.
/// </summary>
/// <param name="Id">Идентификатор токена.</param>
/// <param name="Name">Новое имя либо <see langword="null"/>.</param>
/// <param name="Secret">Новый или маскированный секрет либо <see langword="null"/>.</param>
/// <param name="Enabled">Новое значение признака включённости либо <see langword="null"/>.</param>
public sealed record UpdateTokenCommand(string Id, string? Name, string? Secret, bool? Enabled);

/// <summary>Результат изменения токена: обновлённая запись и число разорванных сессий (F-8.6).</summary>
/// <param name="Token">Токен после сохранения (секрет маскирован).</param>
/// <param name="ClosedSessions">Сколько сессий закрыто из-за отключения токена.</param>
public sealed record UpdateTokenResult(TokenDto Token, int ClosedSessions);

/// <summary>Команда удаления токена (F-8.8, F-8.2).</summary>
/// <param name="Id">Идентификатор токена.</param>
public sealed record DeleteTokenCommand(string Id);

/// <summary>Результат удаления: сколько сессий закрыто вместе с токеном.</summary>
/// <param name="ClosedSessions">Сколько сессий закрыто.</param>
public sealed record DeleteTokenResult(int ClosedSessions);

/// <summary>Команда ротации секрета токена (F-8.8). Живые сессии продолжают работать (F-8.6).</summary>
/// <param name="Id">Идентификатор токена.</param>
public sealed record RotateTokenCommand(string Id);

/// <summary>Результат ротации: токен с новым открытым секретом.</summary>
/// <param name="Token">Токен с новым секретом.</param>
public sealed record RotateTokenResult(TokenDto Token);

/// <summary>Запрос полной ссылки и секрета токена для панели (F-8.8, исключение из маскирования).</summary>
/// <param name="Id">Идентификатор токена.</param>
public sealed record GetTokenLinkQuery(string Id);

/// <summary>Токен с запрошенным идентификатором отсутствует в конфигурации.</summary>
/// <param name="tokenId">Искомый идентификатор.</param>
public sealed class TokenNotFoundException(string tokenId)
    : Exception($"No token with id '{tokenId}'.")
{
    /// <summary>Искомый идентификатор токена.</summary>
    public string TokenId { get; } = tokenId;
}

/// <summary>
/// Конфликт при изменении набора токенов: повторяющееся имя или превышен предел количества.
/// HTTP-слой маппит это исключение в ProblemDetails 409 — Application не знает деталей HTTP.
/// </summary>
public sealed class TokenConflictException(string message) : Exception(message)
{
    /// <summary>Конфликтующее имя токена (только при конфликте имени).</summary>
    public string? ConflictName { get; init; }
}