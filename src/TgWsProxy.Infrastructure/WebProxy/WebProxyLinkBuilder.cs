using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Сборка ссылок подключения WEB proxy (browser-relay) для токена: маркерный секрет для
/// <c>tg://proxy</c>-ссылки и прямой bridge-URL с HMAC capability (DESIGN-WEB-PROXY §2.2/§2.3).
/// Секрет токена переиспользуется тот же, что и для MTProxy (16 байт, 32 hex): отдельного
/// «web-токена» не требуется.
/// </summary>
public static class WebProxyLinkBuilder
{
    /// <summary>
    /// Внешний домен bridge: <see cref="Config.WebProxyPublicHost"/>, если задан, иначе
    /// <see cref="Config.WebProxyHost"/>. Служебные адреса (<c>0.0.0.0</c>, <c>::</c>, …)
    /// снаружи недостижимы — возвращается <see langword="null"/>, и ссылка не строится.
    /// </summary>
    public static string? ResolveHost(Config cfg)
    {
        var publicHost = cfg.WebProxyPublicHost?.Trim() ?? "";
        var source = publicHost.Length > 0 ? publicHost : cfg.WebProxyHost;
        return PublicEndpoint.TryParse(source, cfg.WebProxyPort, out var endpoint)
            ? endpoint.Host
            : null;
    }

    /// <summary>
    /// Маркерный секрет для <c>tg://proxy</c>-ссылки: маркер <c>0x70</c>
    /// (<c>kWebProxyLinkSecretMarker</c>) + секрет. Корневой путь — hex-форма <c>70</c> + 32 hex
    /// (аналог <c>dd</c>/<c>ee</c>); с <c>basePath</c> — <c>base64url(0x70 + secret)</c> без
    /// <c>=</c>-паддинга (DESIGN-WEB-PROXY §2.2).
    /// </summary>
    public static string BuildMarkerSecret(string secretHex, string? basePath)
    {
        var normalized = BridgeCapabilityVerifier.NormalizeBasePath(basePath);
        if (normalized.Length == 0)
        {
            return "70" + secretHex.ToLowerInvariant();
        }

        var secret = Convert.FromHexString(secretHex);
        Span<byte> marked = stackalloc byte[secret.Length + 1];
        marked[0] = RelayFrameCodec.WebProxyLinkSecretMarker;
        secret.CopyTo(marked[1..]);
        return BridgeCapabilityVerifier.Base64Url.Encode(marked);
    }

    /// <summary>
    /// Прямой bridge-URL: <c>https://host/basePath/?bridge=&lt;capability&gt;</c>. Порт всегда 443
    /// и в URL не передаётся (DESIGN-WEB-PROXY §2.3). <see langword="null"/> — внешний адрес
    /// не определён.
    /// </summary>
    public static string? BuildBridgeUrl(Config cfg, ProxyToken token)
    {
        if (ResolveHost(cfg) is not { } host)
        {
            return null;
        }

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(token.Secret), host, basePath);
        return $"https://{host}{basePath}/?bridge={capability}";
    }

    /// <summary>
    /// Ссылка <c>tg://proxy</c> для WEB proxy (порт 443, маркерный секрет §2.2).
    /// <see langword="null"/> — внешний адрес не определён.
    /// </summary>
    public static string? BuildTgLink(Config cfg, ProxyToken token)
    {
        if (ResolveHost(cfg) is not { } host)
        {
            return null;
        }

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        return $"tg://proxy?server={host}&port=443&secret={BuildMarkerSecret(token.Secret, basePath)}";
    }
}
