using System.Globalization;
using Microsoft.Extensions.Logging;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Session;

/// <summary>
/// Структурированная корреляция прокси-сессий. Основана на <see cref="ILogger.BeginScope"/>:
/// Serilog раскладывает scoped-свойства в поля события, поэтому слой Infrastructure не зависит
/// от Serilog напрямую и сохраняет чистоту слоёв. Имена свойств — единые константы
/// <see cref="LoggingConstants"/>.
/// </summary>
internal static class SessionLogScope
{
    /// <summary>
    /// Открывает scope со свойствами, известными до handshake: идентификатор соединения и адрес
    /// клиента. Токен добавляется, только если он уже определён в контексте.
    /// </summary>
    public static IDisposable? Begin(ILogger logger, ClientContext context)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);

        var state = new Dictionary<string, object>
        {
            [AppConstants.Logging.ConnectionId] = context.ConnectionId,
            [AppConstants.Logging.Peer] = context.Peer,
        };

        if (!string.IsNullOrEmpty(context.TokenId))
        {
            state[AppConstants.Logging.TokenId] = context.TokenId;
        }

        return logger.BeginScope(state);
    }

    /// <summary>
    /// Дополняет scope свойствами, известными после handshake: токен, DC и признак media.
    /// </summary>
    public static IDisposable? BeginRoute(ILogger logger, string tokenId, int? dc, bool media)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return logger.BeginScope(new Dictionary<string, object>
        {
            [AppConstants.Logging.TokenId] = tokenId,
            [AppConstants.Logging.Dc] = dc?.ToString(CultureInfo.InvariantCulture) ?? "-",
            [AppConstants.Logging.Media] = media,
        });
    }
}
