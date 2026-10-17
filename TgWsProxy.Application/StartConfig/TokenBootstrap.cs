#nullable enable

using System.Security.Cryptography;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Нормализация списка токенов при старте (F-8.3). Отсутствие токенов или пустой секрет —
/// не столько ошибка конфигурации, сколько состояние, из которого прокси обязан подняться:
/// недостающее генерируется, а вызывающая сторона по возвращённому признаку понимает, что
/// файл нужно перезаписать.
///
/// Проверки «не меньше одного включённого», уникальности имён и формата секрета остаются за
/// <see cref="ConfigValidator"/>: молча включать отключённый пользователем токен нельзя.
/// </summary>
public static class TokenBootstrap
{
    /// <summary>Идентификатор токена, созданного миграцией прежнего единственного секрета (F-8.3).</summary>
    public const string DefaultTokenId = "default";

    /// <summary>Имя токена, созданного миграцией прежнего единственного секрета.</summary>
    public const string DefaultTokenName = "default";

    /// <summary>
    /// Дополняет конфигурацию до работоспособного набора токенов: заполняет пропущенные
    /// идентификаторы, имена, секреты и время создания, а при пустом списке создаёт токен
    /// по умолчанию.
    /// </summary>
    /// <param name="config">Конфигурация, которая будет изменена на месте.</param>
    /// <returns><see langword="true"/>, если конфигурация изменилась и её нужно сохранить.</returns>
    public static bool EnsureTokens(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var changed = false;

        for (var i = 0; i < config.Tokens.Count; i++)
        {
            var token = config.Tokens[i];

            if (string.IsNullOrWhiteSpace(token.Id))
            {
                token.Id = NewId();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(token.Name))
            {
                token.Name = token.Id;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(token.Secret))
            {
                token.Secret = GenerateSecret();
                changed = true;
            }

            if (token.CreatedAt is null)
            {
                token.CreatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        if (config.Tokens.Count == 0)
        {
            config.Tokens.Add(new ProxyToken
            {
                Id = DefaultTokenId,
                Name = DefaultTokenName,
                Secret = GenerateSecret(),
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Создаёт токен для миграции прежнего единственного ключа <c>secret</c> (F-8.3):
    /// тот же секрет, имя и идентификатор <c>default</c>, включён.
    /// </summary>
    /// <param name="secret">Прежний секрет (32 hex-символа); пустое значение заменит генератор.</param>
    public static ProxyToken CreateDefaultToken(string? secret) => new()
    {
        Id = DefaultTokenId,
        Name = DefaultTokenName,
        Secret = string.IsNullOrWhiteSpace(secret) ? GenerateSecret() : secret.Trim().ToLowerInvariant(),
        Enabled = true,
    };

    /// <summary>Генерирует секрет доступа прокси: 16 байт → 32 hex-символа.</summary>
    public static string GenerateSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Генерирует идентификатор нового токена.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
