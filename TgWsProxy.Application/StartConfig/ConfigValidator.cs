using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Проверка конфигурации перед стартом и перед каждой записью файла (F-2.5, F-2.8).
///
/// Сообщения ссылаются на ключи <c>config.json</c>, а не на прежние флаги CLI: с переходом
/// на режим «только веб» (F-1.10) флагов больше нет, и подсказка вида «--ws-timeout» уводила
/// бы пользователя в несуществующий интерфейс.
/// </summary>
public static class ConfigValidator
{
    public static void Validate(Config config)
    {
        if (config.Port is < 1 or > 65535)
        {
            throw new ArgumentException("port must be in range 1..65535");
        }

        if (string.IsNullOrWhiteSpace(config.Host))
        {
            throw new ArgumentException("host cannot be empty");
        }

        ValidatePublicHost(config.PublicHost);

        if (config.WsConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentException("wsConnectTimeoutSeconds must be in range 1..300");
        }

        if (config.SocketBufferKb is < 4 or > 8192)
        {
            throw new ArgumentException("socketBufferKb must be in range 4..8192");
        }

        if (config.WsPoolSize is < 0 or > 64)
        {
            throw new ArgumentException("wsPoolSize must be in range 0..64");
        }

        if (config.LogMaxMegabytes is < 0 or > 4096)
        {
            throw new ArgumentException("logMaxMegabytes must be in range 0..4096");
        }

        // Ротация по размеру требует минимум один бэкап, иначе размер файла не ограничен.
        if (config.LogRetainedFileCount is < 1 or > 1000)
        {
            throw new ArgumentException("logRetainedFileCount must be in range 1..1000");
        }

        if (config.WsMaxFrameBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentException("wsMaxFrameBytes must be in range 1024..67108864");
        }

        if (config.MaxConnections is < 0 or > 1_000_000)
        {
            throw new ArgumentException("maxConnections must be in range 0..1000000");
        }

        if (config.RateLimitPerIp is < 0 or > 1_000_000)
        {
            throw new ArgumentException("rateLimitPerIp must be in range 0..1000000");
        }

        if (config.RateLimitWindowSeconds is < 1 or > 86_400)
        {
            throw new ArgumentException("rateLimitWindowSeconds must be in range 1..86400");
        }

        // F-1.4: веб-сервер запускается всегда, поэтому «выключить его нулём» больше нельзя.
        if (config.MetricsPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                "metricsPort must be in range 1..65535: the web server always runs in this edition, " +
                "so it cannot be disabled");
        }

        if (string.IsNullOrWhiteSpace(config.MetricsHost))
        {
            throw new ArgumentException("metricsHost cannot be empty");
        }

        if (config.PerIpStatsTtlSeconds is < 0 or > 604_800)
        {
            throw new ArgumentException("perIpStatsTtlSeconds must be in range 0..604800");
        }

        if (config.PerIpStatsMaxPeers is < 1 or > 10_000_000)
        {
            throw new ArgumentException("perIpStatsMaxPeers must be in range 1..10000000");
        }

        if (config.PrometheusTopPeers is < 0 or > 10_000)
        {
            throw new ArgumentException("prometheusTopPeers must be in range 0..10000");
        }

        if (config.TimeseriesWindowSeconds is < 60 or > 86_400)
        {
            throw new ArgumentException("timeseriesWindowSeconds must be in range 60..86400");
        }

        if (config.TimeseriesStepSeconds is < 1 or > 3600)
        {
            throw new ArgumentException("timeseriesStepSeconds must be in range 1..3600");
        }

        if (config.TimeseriesStepSeconds > config.TimeseriesWindowSeconds)
        {
            throw new ArgumentException("timeseriesStepSeconds cannot exceed timeseriesWindowSeconds");
        }

        ValidateTokens(config.Tokens);

        foreach (var entry in config.AllowIp)
        {
            if (!PeerAccessControl.TryParseEntry(entry, out _))
            {
                throw new ArgumentException($"Invalid allowIp entry: {entry}");
            }
        }

        foreach (var entry in config.DenyIp)
        {
            if (!PeerAccessControl.TryParseEntry(entry, out _))
            {
                throw new ArgumentException($"Invalid denyIp entry: {entry}");
            }
        }
    }

    /// <summary>Максимальное число токенов доступа (F-8.2).</summary>
    public const int MaxTokens = 64;

    /// <summary>Максимальная длина имени токена (F-8.1).</summary>
    public const int MaxTokenNameLength = 64;

    /// <summary>
    /// Проверяет набор токенов доступа прокси (F-8.1, F-8.2): непустой список, не больше
    /// <see cref="MaxTokens"/> элементов, хотя бы один включён, уникальные идентификаторы,
    /// имена и секреты, секрет — ровно 32 hex-символа.
    ///
    /// Вынесено отдельным методом, чтобы те же правила применял Web API: иначе правка через
    /// панель могла бы создать конфигурацию, с которой прокси не перезапустится.
    /// </summary>
    /// <param name="tokens">Список токенов.</param>
    /// <exception cref="ArgumentException">Набор токенов недопустим.</exception>
    public static void ValidateTokens(IReadOnlyList<ProxyToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            throw new ArgumentException("tokens must contain at least one token");
        }

        if (tokens.Count > MaxTokens)
        {
            throw new ArgumentException($"tokens cannot contain more than {MaxTokens} entries (got {tokens.Count})");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabled = 0;

        foreach (var token in tokens)
        {
            if (token is null)
            {
                throw new ArgumentException("tokens cannot contain null entries");
            }

            if (string.IsNullOrWhiteSpace(token.Id))
            {
                throw new ArgumentException("token id must not be empty");
            }

            if (!ids.Add(token.Id))
            {
                throw new ArgumentException($"Duplicate token id: {token.Id}");
            }

            var name = token.Name?.Trim() ?? "";
            if (name.Length is < 1 or > MaxTokenNameLength)
            {
                throw new ArgumentException(
                    $"token name must be 1..{MaxTokenNameLength} characters (token {token.Id})");
            }

            if (!names.Add(name))
            {
                throw new ArgumentException($"Duplicate token name: {name}");
            }

            var secret = token.Secret?.Trim() ?? "";
            if (secret.Length != 32)
            {
                throw new ArgumentException(
                    $"token secret must be exactly 32 hex characters (token {token.Id}, got {secret.Length})");
            }

            try
            {
                Convert.FromHexString(secret);
            }
            catch (FormatException)
            {
                throw new ArgumentException($"token secret must be valid hex (token {token.Id})");
            }

            if (!secrets.Add(secret))
            {
                throw new ArgumentException($"Duplicate token secret (token {token.Id})");
            }

            if (token.Enabled)
            {
                enabled++;
            }
        }

        if (enabled == 0)
        {
            throw new ArgumentException("at least one token must be enabled");
        }
    }

    /// <summary>
    /// <c>publicHost</c> — внешний адрес для ссылок <c>t.me/proxy</c>. Пусто разрешено:
    /// тогда берётся <c>host</c>. Служебные адреса запрещены — по ним ссылка нерабочая.
    /// </summary>
    private static void ValidatePublicHost(string value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 255)
        {
            throw new ArgumentException("publicHost must be 255 characters or fewer");
        }

        if (text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("publicHost must not contain whitespace");
        }

        if (PublicEndpoint.IsWildcard(text))
        {
            throw new ArgumentException(
                "publicHost must be the external address clients connect to, not a wildcard (0.0.0.0, ::)");
        }

        if (!PublicEndpoint.TryParse(text, 1443, out _))
        {
            throw new ArgumentException(
                "publicHost must be a host, host:port or [IPv6]:port with a port in range 1..65535");
        }
    }
}
