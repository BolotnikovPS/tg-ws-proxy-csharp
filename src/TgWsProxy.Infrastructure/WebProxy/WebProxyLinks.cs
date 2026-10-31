using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Адаптер прикладного порта <see cref="IWebProxyLinks"/> над статическим
/// <see cref="WebProxyLinkBuilder"/>: держит живую конфигурацию и не строит ссылки,
/// когда WEB proxy выключен (F-8.8).
/// </summary>
/// <param name="config">Живая конфигурация (singleton, видит горячие изменения).</param>
internal sealed class WebProxyLinks(Config config) : IWebProxyLinks
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public string? BuildShareLink(ProxyToken token)
        => _config.WebProxy.Enabled ? WebProxyLinkBuilder.BuildShareLink(_config, token) : null;

    /// <inheritdoc />
    public string? BuildDeepLink(ProxyToken token)
        => _config.WebProxy.Enabled ? WebProxyLinkBuilder.BuildDeepLink(_config, token) : null;

    /// <inheritdoc />
    public string? BuildBridgeUrl(ProxyToken token)
        => _config.WebProxy.Enabled ? WebProxyLinkBuilder.BuildBridgeUrl(_config, token) : null;
}