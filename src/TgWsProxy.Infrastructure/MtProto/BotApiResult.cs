#nullable disable
using System;
using System.IO;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Результат вызова метода Bot API. При <see cref="Ok"/>=true заполнен <see cref="Result"/>;
/// иначе — <see cref="ErrorCode"/> и <see cref="Description"/>. Соглашение «{ok, result}» /
/// «{ok:false, error_code, description}» соблюдает HTTP-слой (<c>TgWsProxy.Web.BotApiEndpoints</c>).
/// </summary>
public sealed class BotApiResult
{
    public bool Ok { get; init; }
    public object Result { get; init; }
    public int? ErrorCode { get; init; }
    public string Description { get; init; }

    /// <summary>
    /// Значение <c>parameters.retry_after</c> для 429. Клиенты официального Bot API
    /// (и TBotPlatform, см. <c>docs/prompts/REQUIREMENTS-BOT-API.md</c>) читают паузу именно
    /// из этого поля, поэтому одного HTTP-статуса недостаточно.
    /// </summary>
    public int? RetryAfter { get; init; }

    public static BotApiResult Success(object result) => new() { Ok = true, Result = result };

    public static BotApiResult Fail(int errorCode, string description)
        => new() { Ok = false, ErrorCode = errorCode, Description = description };

    /// <summary>
    /// 429 «Too Many Requests» с обязательным <c>parameters.retry_after</c>: и собственный
    /// ограничитель шлюза, и флуд-лимит Telegram отдаются клиенту одинаково.
    /// </summary>
    /// <param name="retryAfter">Пауза в секундах до следующей попытки.</param>
    public static BotApiResult TooManyRequests(int retryAfter)
        => new()
        {
            Ok = false,
            ErrorCode = 429,
            Description = $"Too Many Requests: retry after {retryAfter}",
            RetryAfter = retryAfter,
        };
}

/// <summary>Ошибка шлюза с кодом ошибки Bot API (401/403/...); не является ошибкой Telegram-RPC.</summary>
public sealed class BotGatewayException(int errorCode, string description) : Exception(description)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// Медиа-файл multipart-запроса Bot API в «инфраструктурном» виде (без ASP.NET-типов):
/// имя файла, MIME-тип и читаемый поток. Экземпляры создаёт Web-слой при разборе
/// <c>multipart/form-data</c> и передаёт сюда в словаре <c>args</c>.
/// </summary>
#nullable enable
public sealed record BotUploadFile(string FileName, string? MimeType, Stream Stream);
#nullable disable
