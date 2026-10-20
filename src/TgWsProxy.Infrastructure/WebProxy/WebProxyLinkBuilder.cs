using System.Globalization;
using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Сборка ссылок подключения WEB proxy (browser-relay) для токена: ссылка-приглашение
/// Telegram, deep-link для клиента и прямой bridge-URL с HMAC capability.
/// Секрет токена переиспользуется тот же, что и для MTProxy (16 байт, 32 hex): отдельного
/// «web-токена» не требуется.
///
/// Канонический шаблон ссылки (tproxy-server README §6):
/// <c>https://t.me/webproxy?server=&lt;host&gt;[/&lt;basePath&gt;]&amp;secret=&lt;secret&gt;</c>,
/// где <c>secret</c> — обычный 32-hex для корневого пути и
/// <c>base64url(0x70 ‖ secret)</c> без паддинга при заданном <c>basePath</c>.
/// Порт в ссылке не участвует: HTTPS и 443 зафиксированы типом прокси.
/// </summary>
public static class WebProxyLinkBuilder
{
    /// <summary>Схема ссылки-приглашения, которую понимает Telegram (SPEC §6).</summary>
    public const string ShareLinkPrefix = "https://t.me/webproxy";

    /// <summary>Эквивалентная схема deep-link для клиента (не зарегистрирована в t.me).</summary>
    public const string DeepLinkPrefix = "tg://webproxy";

    /// <summary>Максимальная длина имени хоста (как в <c>NormalizeWebProxyHost</c> клиента).</summary>
    public const int MaxHostLength = 253;

    /// <summary>
    /// Внешний домен для ссылок Telegram: <see cref="Config.WebProxyPublicHost"/>, если задан,
    /// иначе <see cref="Config.WebProxyHost"/>, приведённый к канонической форме
    /// (<see cref="NormalizeHost"/>). Служебные адреса (<c>0.0.0.0</c>, <c>::</c>, …) и
    /// IP-литералы снаружи недостижимы для WEB proxy — возвращается <see langword="null"/>.
    /// </summary>
    public static string? ResolveHost(Config cfg)
        => NormalizeHost(ResolveConfiguredHost(cfg));

    /// <summary>
    /// Хост для bridge-URL и <c>relayBase</c> bridge-страницы: без требования «доменное имя»,
    /// потому что по IP (<c>127.0.0.1</c>, LAN-адрес) идёт локальная отладка и смоук.
    /// Значение приводится к нижнему регистру: от него же считается capability, а клиент
    /// присылает в заголовке <c>Host</c> ровно этот адрес.
    /// </summary>
    public static string? ResolveBridgeHost(Config cfg)
        => ResolveConfiguredHost(cfg)?.ToLowerInvariant();

    private static string? ResolveConfiguredHost(Config cfg)
    {
        var publicHost = cfg.WebProxyPublicHost?.Trim() ?? "";
        var source = publicHost.Length > 0 ? publicHost : cfg.WebProxyHost;
        return PublicEndpoint.TryParse(source, cfg.WebProxyPort, out var endpoint)
            ? endpoint.Host
            : null;
    }

    /// <summary>
    /// Приводит хост к канонической форме клиента (<c>NormalizeWebProxyHost</c> в tdesktop):
    /// IDN → ACE (<c>xn--…</c>), нижний регистр, обязательная точка, метки ≤63 символов из
    /// <c>[a-z0-9-]</c> без ведущего/хвостового дефиса, длина ≤253. IP-литералы и
    /// «числовые» хосты (<c>127.0.0.1</c>, <c>127.1</c>, <c>0x7f.1</c>) отвергаются: тип
    /// WEB proxy работает только с доменом, а capability считается от ACE-формы.
    /// </summary>
    /// <returns>Канонический хост или <see langword="null"/>, если адрес непригоден.</returns>
    public static string? NormalizeHost(string? value)
    {
        var input = value?.Trim() ?? "";
        if (input.Length == 0
            || input.Contains(':')
            || input.Contains('/')
            || input.Contains('?')
            || input.Contains('#')
            || input.Contains('@')
            || input.EndsWith('.'))
        {
            return null;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(input).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Символы, которые IDNA не отображает в ASCII (пробелы, управляющие, эмодзи).
            return null;
        }

        if (ascii.Length == 0 || ascii.Length > MaxHostLength || !ascii.Contains('.'))
        {
            return null;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-')
            {
                return null;
            }

            foreach (var ch in label)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                {
                    return null;
                }
            }
        }

        // Правило WHATWG «хост заканчивается числом»: последняя метка из цифр или 0x-hex —
        // это IPv4 (в том числе сокращённый 127.1), а не домен.
        if (LastLabelIsNumeric(ascii) || IPAddress.TryParse(ascii, out _))
        {
            return null;
        }

        return ascii;
    }

    /// <summary>
    /// Секрет для ссылки WEB proxy. Корневой путь — обычный MTProxy-секрет (32 hex);
    /// с <c>basePath</c> — <c>base64url(0x70 + secret)</c> без <c>=</c>-паддинга (маркер
    /// <c>kWebProxyLinkSecretMarker</c> = 0x70). Немаркированный секрет при наличии пути
    /// клиент отвергает, поэтому форма выбирается по <paramref name="basePath"/>.
    /// </summary>
    public static string BuildMarkerSecret(string secretHex, string? basePath)
    {
        var normalized = BridgeCapabilityVerifier.NormalizeBasePath(basePath);
        if (normalized.Length == 0)
        {
            return secretHex.ToLowerInvariant();
        }

        var secret = Convert.FromHexString(secretHex);
        Span<byte> marked = stackalloc byte[secret.Length + 1];
        marked[0] = RelayFrameCodec.WebProxyLinkSecretMarker;
        secret.CopyTo(marked[1..]);
        return BridgeCapabilityVerifier.Base64Url.Encode(marked);
    }

    /// <summary>
    /// Ссылка-приглашение Telegram:
    /// <c>https://t.me/webproxy?server=&lt;host&gt;[/&lt;basePath&gt;]&amp;secret=&lt;secret&gt;</c>.
    /// <c>server</c> percent-кодируется целиком, поэтому <c>/</c> пути уходит как <c>%2F</c>.
    /// <see langword="null"/> — внешний адрес не определён (wildcard/IP).
    /// </summary>
    public static string? BuildShareLink(Config cfg, ProxyToken token)
        => BuildLink(ShareLinkPrefix, cfg, token);

    /// <summary>
    /// Эквивалентный deep-link <c>tg://webproxy?…</c>: открывается сразу в WEB-совместимом
    /// клиенте, минуя сайт t.me (маршрут <c>t.me/webproxy</c> ещё не зарегистрирован).
    /// <see langword="null"/> — внешний адрес не определён.
    /// </summary>
    public static string? BuildDeepLink(Config cfg, ProxyToken token)
        => BuildLink(DeepLinkPrefix, cfg, token);

    /// <summary>
    /// Прямой bridge-URL: <c>https://host/basePath/?bridge=&lt;capability&gt;</c>. Порт всегда 443
    /// и в URL не передаётся (DESIGN-WEB-PROXY §2.3). Это адрес страницы-носителя, а не
    /// ссылка для Telegram: нужен для отладки и смоук-клиента, поэтому IP-адрес допустим.
    /// <see langword="null"/> — внешний адрес не определён (wildcard).
    /// </summary>
    public static string? BuildBridgeUrl(Config cfg, ProxyToken token)
    {
        if (ResolveBridgeHost(cfg) is not { } host)
        {
            return null;
        }

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(token.Secret), host, basePath);
        var prefix = basePath.Length == 0 ? "" : "/" + basePath;
        return $"https://{host}{prefix}/?bridge={capability}";
    }

    private static string? BuildLink(string prefix, Config cfg, ProxyToken token)
    {
        if (ResolveHost(cfg) is not { } host)
        {
            return null;
        }

        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        var server = basePath.Length == 0 ? host : $"{host}/{basePath}";
        var secret = BuildMarkerSecret(token.Secret, basePath);
        return $"{prefix}?server={Uri.EscapeDataString(server)}&secret={secret}";
    }

    private static bool LastLabelIsNumeric(string host)
    {
        var label = host[(host.LastIndexOf('.') + 1)..];
        if (label.Length == 0)
        {
            return false;
        }

        var hex = label.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hex ? label[2..] : label;
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var ch in digits)
        {
            var decimalDigit = char.IsAsciiDigit(ch);
            var hexDigit = char.IsAsciiHexDigit(ch);
            if (!decimalDigit && !(hex && hexDigit))
            {
                return false;
            }
        }

        return true;
    }
}
