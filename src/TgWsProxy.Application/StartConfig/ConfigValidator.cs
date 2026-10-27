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
        if (config.MtProto.Port is < 1 or > 65535)
        {
            throw new ArgumentException("mtproto.port must be in range 1..65535");
        }

        ValidatePublicHost(config.MtProto.PublicHost);

        if (config.MtProto.WsConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentException("mtproto.wsConnectTimeoutSeconds must be in range 1..300");
        }

        if (config.MtProto.SocketBufferKb is < 4 or > 8192)
        {
            throw new ArgumentException("mtproto.socketBufferKb must be in range 4..8192");
        }

        if (config.MtProto.WsPoolSize is < 0 or > 64)
        {
            throw new ArgumentException("mtproto.wsPoolSize must be in range 0..64");
        }

        if (config.Logs.MaxMegabytes is < 0 or > 4096)
        {
            throw new ArgumentException("logs.maxMegabytes must be in range 0..4096");
        }

        // Ротация по размеру требует минимум один бэкап, иначе размер файла не ограничен.
        if (config.Logs.RetainedFileCount is < 1 or > 1000)
        {
            throw new ArgumentException("logs.retainedFileCount must be in range 1..1000");
        }

        if (config.MtProto.WsMaxFrameBytes is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentException("mtproto.wsMaxFrameBytes must be in range 1024..67108864");
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

        // F-1.4: веб-сервер (метрики, панель, API) запускается вместе с приложением, всегда на 0.0.0.0:80.

        if (config.Stats.TtlSeconds is < 0 or > 604_800)
        {
            throw new ArgumentException("stats.ttlSeconds must be in range 0..604800");
        }

        if (config.Stats.MaxPeers is < 1 or > 10_000_000)
        {
            throw new ArgumentException("stats.maxPeers must be in range 1..10000000");
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

        ValidateWebProxy(config);

        ValidateOpenTelemetry(config);

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

    /// <summary>Максимальная длина <c>webProxy.basePath</c> (совпадает с MaxBasePathLength в verifier).</summary>
    public const int MaxWebProxyBasePathLength = 128;

    /// <summary>
    /// Проверяет конфигурацию WEB proxy (browser-relay) bridge-листенера (F-W.1, F-W.5):
    /// диапазон порта, отсутствие конфликта с MTProxy-портом и портом панели, обязательность
    /// сертификата при включении, а также формат basePath и внешнего домена.
    /// </summary>
    private static void ValidateWebProxy(Config config)
    {
        if (config.WebProxy.Port is < 1 or > 65535)
        {
            throw new ArgumentException("webProxy.port must be in range 1..65535");
        }

        if (config.WebProxy.Enabled)
        {
            if (config.WebProxy.Port == config.MtProto.Port)
            {
                throw new ArgumentException("webProxy.port must not equal mtproto.port (MTProxy listener)");
            }

            if (string.IsNullOrWhiteSpace(config.WebProxy.CertPath))
            {
                throw new ArgumentException("webProxy.certPath is required when webProxy.enabled");
            }
        }

        // HMAC v2-контекст включает basePath: голый слаг без ведущего/хвостового '/', сегменты
        // [A-Za-z0-9][A-Za-z0-9_-]*, без точек, ≤128 символов (N-W.3, BASE_PATH.md).
        var basePath = config.WebProxy.BasePath?.Trim() ?? "";
        if (basePath.Length > MaxWebProxyBasePathLength)
        {
            throw new ArgumentException($"webProxy.basePath must be {MaxWebProxyBasePathLength} characters or fewer");
        }

        if (basePath.Length > 0 && !IsValidWebProxyBasePath(basePath))
        {
            throw new ArgumentException(
                "webProxy.basePath must be a bare slug: segments of [A-Za-z0-9][A-Za-z0-9_-]*, no leading/trailing slash, no dots");
        }

        ValidateWebProxyPublicHost(config.WebProxy.PublicHost);
        ValidateWebProxyCarrierMode(config.WebProxy.CarrierMode);
        ValidateWebProxyLimits(config);
    }

    /// <summary>Допустимые значения <c>webProxy.carrierMode</c> (F-W.24).</summary>
    public static readonly string[] WebProxyCarrierModes = ["https", "websocket"];

    /// <summary>
    /// Носитель bridge-сессии: только <c>https</c> (long-poll) либо <c>websocket</c>
    /// (один WS на сессию). Прочие режимы (<c>https-lanes</c>, <c>websocket-lanes</c>) не
    /// поддержаны сервером и отклоняются, чтобы клиент не получил неизвестный ему режим.
    /// </summary>
    private static void ValidateWebProxyCarrierMode(string value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ArgumentException("webProxy.carrierMode cannot be empty");
        }

        if (!WebProxyCarrierModes.Contains(text, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"webProxy.carrierMode must be one of: {string.Join(", ", WebProxyCarrierModes)}");
        }
    }

    /// <summary>Предел объёма исходящей очереди одной сессии (верхняя граница — 64 МиБ).</summary>
    public const int MaxWebProxyPendingBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Лимиты ресурсов WEB proxy (F-W.25): сессии, потоки, исходящая очередь. Значения должны
    /// быть положительными; <c>webProxy.maxSessionsPerIp = 0</c> — без ограничения. Глобальные
    /// пределы не могут быть меньше соответствующих локальных, иначе локальные недостижимы.
    /// </summary>
    private static void ValidateWebProxyLimits(Config config)
    {
        if (config.WebProxy.MaxSessions < 1)
        {
            throw new ArgumentException("webProxy.maxSessions must be greater than 0");
        }

        if (config.WebProxy.MaxSessionsPerIp < 0)
        {
            throw new ArgumentException("webProxy.maxSessionsPerIp must be 0 (unlimited) or greater");
        }

        if (config.WebProxy.MaxStreamsPerSession < 1)
        {
            throw new ArgumentException("webProxy.maxStreamsPerSession must be greater than 0");
        }

        if (config.WebProxy.MaxStreams < 1)
        {
            throw new ArgumentException("webProxy.maxStreams must be greater than 0");
        }

        if (config.WebProxy.MaxStreams < config.WebProxy.MaxStreamsPerSession)
        {
            throw new ArgumentException(
                "webProxy.maxStreams must be greater than or equal to webProxy.maxStreamsPerSession");
        }

        if (config.WebProxy.MaxPendingBytesPerSession < 1
            || config.WebProxy.MaxPendingBytesPerSession > MaxWebProxyPendingBytes)
        {
            throw new ArgumentException(
                $"webProxy.maxPendingBytesPerSession must be in range 1..{MaxWebProxyPendingBytes}");
        }

        if (config.WebProxy.MaxPendingItemsPerSession < 1)
        {
            throw new ArgumentException("webProxy.maxPendingItemsPerSession must be greater than 0");
        }
    }

    /// <summary>
    /// Голый слаг base-path: без ведущего/хвостового <c>/</c>, сегменты
    /// <c>[A-Za-z0-9][A-Za-z0-9_-]*</c>, без точек (точка запрещена — dot-сегменты невозможны).
    /// </summary>
    private static bool IsValidWebProxyBasePath(string basePath)
    {
        if (basePath[0] == '/' || basePath[^1] == '/')
        {
            return false;
        }

        foreach (var segment in basePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (!char.IsAsciiLetterOrDigit(segment[0]))
            {
                return false;
            }

            for (var i = 1; i < segment.Length; i++)
            {
                var c = segment[i];
                if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// <c>webProxy.publicHost</c> — внешний домен для bridge-ссылки за reverse-proxy. Пусто
    /// разрешено: тогда bridge-ссылка не строится (адрес привязки всегда все интерфейсы).
    /// Это домен без порта (bridge всегда на 443).
    /// </summary>
    private static void ValidateWebProxyPublicHost(string value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 255)
        {
            throw new ArgumentException("webProxy.publicHost must be 255 characters or fewer");
        }

        if (text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("webProxy.publicHost must not contain whitespace");
        }

        if (PublicEndpoint.IsWildcard(text))
        {
            throw new ArgumentException(
                "webProxy.publicHost must be the external domain clients connect to, not a wildcard (0.0.0.0, ::)");
        }
    }

    /// <summary>
    /// <c>mtproto.publicHost</c> — внешний адрес для ссылок <c>t.me/proxy</c>. Пусто разрешено:
    /// тогда ссылка не строится (слушатель всегда привязан ко всем интерфейсам, внешний адрес
    /// неизвестен). Служебные адреса запрещены — по ним ссылка нерабочая.
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
            throw new ArgumentException("mtproto.publicHost must be 255 characters or fewer");
        }

        if (text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("mtproto.publicHost must not contain whitespace");
        }

        if (PublicEndpoint.IsWildcard(text))
        {
            throw new ArgumentException(
                "mtproto.publicHost must be the external address clients connect to, not a wildcard (0.0.0.0, ::)");
        }

        if (!PublicEndpoint.TryParse(text, 1443, out _))
        {
            throw new ArgumentException(
                "mtproto.publicHost must be a host, host:port or [IPv6]:port with a port in range 1..65535");
        }
    }

    /// <summary>Допустимые значения <c>otel.protocol</c> (пусто — автоопределение).</summary>
    public static readonly string[] OtelProtocols = ["grpc", "http"];

    /// <summary>
    /// Проверяет настройки OpenTelemetry: протокол из фиксированного набора и формат endpoint.
    /// Пустые значения допустимы — тогда SDK/Serilog используют default либо переменные OTEL_*.
    /// </summary>
    private static void ValidateOpenTelemetry(Config config)
    {
        var otel = config.Otel;
        var protocol = otel.Protocol.Trim();
        if (protocol.Length > 0 && !OtelProtocols.Contains(protocol, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"otel.protocol must be one of: {string.Join(", ", OtelProtocols)}, or empty for auto-detection");
        }

        var endpoint = otel.Endpoint.Trim();
        if (endpoint.Length > 0
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("otel.endpoint must be an absolute URL (http:// or https://)");
        }
    }
}
