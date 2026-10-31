using TgWsProxy.Domain;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Ссылки подключения WEB proxy (browser-relay) для токена: приглашение Telegram,
/// deep-link и прямой bridge-URL с HMAC capability (F-8.8).
///
/// Прикладной слой не знает, как устроены маркер секрета и capability — это протокол
/// вместе с реализацией; здесь только порт, чтобы HTTP-слой и CQRS-обработчики не тянули
/// зависимость на Infrastructure.
/// </summary>
public interface IWebProxyLinks
{
    /// <summary>
    /// Ссылка-приглашение <c>https://t.me/webproxy?…</c> либо <see langword="null"/>,
    /// если WEB proxy выключен или внешний адрес не определён.
    /// </summary>
    /// <param name="token">Токен, для которого строится ссылка.</param>
    string? BuildShareLink(ProxyToken token);

    /// <summary>
    /// Deep-link <c>tg://webproxy?…</c> либо <see langword="null"/> в тех же случаях.
    /// </summary>
    /// <param name="token">Токен, для которого строится ссылка.</param>
    string? BuildDeepLink(ProxyToken token);

    /// <summary>
    /// Прямой bridge-URL <c>https://host/basePath/?bridge=…</c> либо <see langword="null"/>,
    /// если WEB proxy выключен или внешний адрес не определён.
    /// </summary>
    /// <param name="token">Токен, для которого строится ссылка.</param>
    string? BuildBridgeUrl(ProxyToken token);
}