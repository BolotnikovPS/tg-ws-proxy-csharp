using System.Globalization;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.ReadModels;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using Wolverine.Attributes;

namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Обработчики команд и запросов по токенам доступа (F-8.x): HTTP-слой больше не решает,
/// какие имена допустимы, когда рвать сессии и как строить ссылки для клиента.
/// Ошибки пробрасываются исключениями — их перевод в коды ответов остаётся у API.
/// </summary>
/// <remarks>
/// Неизвестный идентификатор или занятое имя — штатный отказ (404/409), поэтому Wolverine
/// логирует неудачную обработку как Information; настоящие сбои (5xx) пишет
/// request-logging промежуточного слоя на уровне Error.
/// </remarks>
[WolverineLogging(FailureLogLevel = LogLevel.Information)]
public static class TokenHandler
{
    /// <summary>Отдаёт список токенов с маскированными секретами и числом живых сессий (F-8.8).</summary>
    /// <param name="query">Запрос списка.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="sessions">Реестр активных сессий.</param>
    public static TokenDto[] Handle(GetTokensQuery query, Config cfg, IActiveSessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(sessions);

        return [.. cfg.Tokens.Select(token => TokenDto.From(token, activeSessions: sessions.CountByToken(token.Id)))];
    }

    /// <summary>
    /// Создаёт токен: проверяет имя и предел количества, при необходимости генерирует секрет
    /// и сохраняет конфигурацию (F-8.8). Секрет возвращается открытым один раз.
    /// </summary>
    /// <param name="command">Параметры нового токена.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="configService">Сервис применения конфигурации.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <exception cref="ArgumentException">Имя пустое или длиннее предела.</exception>
    /// <exception cref="TokenConflictException">Имя занято или достигнут предел количества токенов.</exception>
    public static async Task<CreateTokenResult> HandleAsync(
        CreateTokenCommand command,
        Config cfg,
        ConfigUpdateService configService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(configService);

        var name = command.Name?.Trim() ?? "";
        if (name.Length is < 1 or > ConfigValidator.MaxTokenNameLength)
        {
            throw new ArgumentException(
                $"Token name must be 1..{ConfigValidator.MaxTokenNameLength} characters.",
                nameof(command));
        }

        if (cfg.Tokens.Any(existing => string.Equals(existing.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TokenConflictException($"A token named '{name}' already exists.")
            {
                ConflictName = name,
            };
        }

        if (cfg.Tokens.Count >= ConfigValidator.MaxTokens)
        {
            throw new TokenConflictException($"At most {ConfigValidator.MaxTokens} tokens are supported.");
        }

        var token = new ProxyToken
        {
            Id = TokenBootstrap.NewId(),
            Name = name,
            Secret = string.IsNullOrWhiteSpace(command.Secret)
                ? TokenBootstrap.GenerateSecret()
                : command.Secret.Trim(),
            Enabled = command.Enabled ?? true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
        list.Add(token);

        await configService
            .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
            .ConfigureAwait(false);

        return new CreateTokenResult(TokenDto.From(token, revealSecret: true));
    }

    /// <summary>
    /// Изменяет имя, секрет или признак включённости (F-8.8, F-8.9). Отключение токена
    /// разрывает только его сессии, остальные не затрагиваются (F-8.6).
    /// </summary>
    /// <param name="command">Параметры изменения.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="configService">Сервис применения конфигурации.</param>
    /// <param name="sessions">Реестр активных сессий.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <exception cref="TokenNotFoundException">Токен с таким идентификатором отсутствует.</exception>
    public static async Task<UpdateTokenResult> HandleAsync(
        UpdateTokenCommand command,
        Config cfg,
        ConfigUpdateService configService,
        IActiveSessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(sessions);

        var index = cfg.Tokens.FindIndex(existing => string.Equals(existing.Id, command.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new TokenNotFoundException(command.Id);
        }

        var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
        var target = list[index];

        if (command.Name is { } name)
        {
            target.Name = name.Trim();
        }

        if (command.Secret is { } secret)
        {
            // Маска остаётся маской: ApplyAsync восстановит настоящий секрет по id.
            target.Secret = secret.Trim();
        }

        if (command.Enabled is { } enabled)
        {
            target.Enabled = enabled;
        }

        await configService
            .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
            .ConfigureAwait(false);

        var closed = target.Enabled ? 0 : sessions.CloseByToken(target.Id);
        return new UpdateTokenResult(TokenDto.From(target), closed);
    }

    /// <summary>
    /// Удаляет токен вместе с его сессиями (F-8.8). Удаление последнего включённого токена
    /// отклоняет валидатор конфигурации — исключение уходит в HTTP-слой как 400 (F-8.2).
    /// </summary>
    /// <param name="command">Команда удаления.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="configService">Сервис применения конфигурации.</param>
    /// <param name="sessions">Реестр активных сессий.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <exception cref="TokenNotFoundException">Токен с таким идентификатором отсутствует.</exception>
    public static async Task<DeleteTokenResult> HandleAsync(
        DeleteTokenCommand command,
        Config cfg,
        ConfigUpdateService configService,
        IActiveSessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(sessions);

        var list = cfg.Tokens
            .Where(existing => !string.Equals(existing.Id, command.Id, StringComparison.Ordinal))
            .Select(static existing => existing.Clone())
            .ToList();

        if (list.Count == cfg.Tokens.Count)
        {
            throw new TokenNotFoundException(command.Id);
        }

        await configService
            .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
            .ConfigureAwait(false);

        return new DeleteTokenResult(sessions.CloseByToken(command.Id));
    }

    /// <summary>
    /// Ротирует секрет токена: новое значение выдаётся один раз, живые сессии продолжают
    /// работать, отметка последнего использования сбрасывается (F-8.8).
    /// </summary>
    /// <param name="command">Команда ротации.</param>
    /// <param name="cfg">Живая конфигурация.</param>
    /// <param name="configService">Сервис применения конфигурации.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    /// <exception cref="TokenNotFoundException">Токен с таким идентификатором отсутствует.</exception>
    public static async Task<RotateTokenResult> HandleAsync(
        RotateTokenCommand command,
        Config cfg,
        ConfigUpdateService configService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(configService);

        var index = cfg.Tokens.FindIndex(existing => string.Equals(existing.Id, command.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new TokenNotFoundException(command.Id);
        }

        var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
        var target = list[index];
        target.Secret = TokenBootstrap.GenerateSecret();
        target.LastUsedAt = null;

        await configService
            .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
            .ConfigureAwait(false);

        return new RotateTokenResult(TokenDto.From(target, revealSecret: true));
    }

    /// <summary>
    /// Отдаёт полную ссылку для Telegram и открытый секрет токена — единственное исключение
    /// из маскирования, нужное панели для копирования (F-8.8).
    /// </summary>
    /// <param name="query">Запрос ссылки.</param>
    /// <param name="cfg">Живая конфигурация: внешний адрес и порт прокси.</param>
    /// <param name="webProxyLinks">Порт сборки ссылок WEB proxy (browser-relay).</param>
    /// <exception cref="TokenNotFoundException">Токен с таким идентификатором отсутствует.</exception>
    public static TokenLinkDto Handle(GetTokenLinkQuery query, Config cfg, IWebProxyLinks webProxyLinks)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(webProxyLinks);

        var token = cfg.Tokens.FirstOrDefault(existing => string.Equals(existing.Id, query.Id, StringComparison.Ordinal))
            ?? throw new TokenNotFoundException(query.Id);

        return new TokenLinkDto(
            token.Id,
            token.Name,
            BuildTelegramLink(cfg, token),
            token.Secret,
            webProxyLinks.BuildShareLink(token),
            webProxyLinks.BuildDeepLink(token),
            webProxyLinks.BuildBridgeUrl(token));
    }

    /// <summary>
    /// Собирает ссылку <c>https://t.me/proxy</c> для подключения клиента Telegram (F-8.8).
    /// Секрет передаётся в формате fake-TLS (<c>dd</c> + 32 hex), который ожидает клиент.
    /// Адрес берётся из <c>mtproto.publicHost</c> — внешней точки доступа; если он не задан,
    /// ссылка не строится: панель предложит заполнить внешний адрес.
    /// </summary>
    /// <param name="cfg">Живая конфигурация: внешний адрес и порт прокси.</param>
    /// <param name="token">Токен, для которого строится ссылка.</param>
    /// <returns>Готовая ссылка или <see langword="null"/>, если внешний адрес не определён.</returns>
    private static string? BuildTelegramLink(Config cfg, ProxyToken token)
    {
        if (cfg.ResolvePublicEndpoint() is not { } endpoint)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"https://t.me/proxy?server={endpoint.Host}&port={endpoint.Port}&secret=dd{token.Secret}");
    }
}