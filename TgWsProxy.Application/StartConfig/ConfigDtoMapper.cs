using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Маппинг между транспортной моделью <see cref="ConfigDto"/> и живой конфигурацией
/// <see cref="Config"/>. Вынесен из DTO, чтобы транспортная модель оставалась только данными:
/// поведение (наложение значений, снятие полного представления) живёт здесь отдельно.
/// </summary>
public static class ConfigDtoMapper
{
    /// <summary>
    /// Накладывает заданные в DTO значения на существующую конфигурацию.
    /// Отсутствующие (<c>null</c>) поля не изменяются; коллекции заменяются целиком.
    /// </summary>
    public static void Apply(ConfigDto dto, Config target)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(target);

        if (dto.Port is { } port) target.Port = port;
        if (dto.Host is { } host) target.Host = host;
        if (dto.PublicHost is { } publicHost) target.PublicHost = publicHost.Trim();
        if (dto.Verbose is { } verbose) target.Verbose = verbose;
        if (dto.LogPath is { } logPath) target.LogPath = logPath;
        if (dto.AllowInvalidCertificates is { } allowInvalid) target.AllowInvalidCertificates = allowInvalid;
        if (dto.WsMaxFrameBytes is { } wsMaxFrameBytes) target.WsMaxFrameBytes = wsMaxFrameBytes;
        if (dto.WsConnectTimeoutSeconds is { } wsTimeout) target.WsConnectTimeoutSeconds = wsTimeout;
        if (dto.SocketBufferKb is { } socketBufferKb) target.SocketBufferKb = socketBufferKb;
        if (dto.WsPoolSize is { } wsPoolSize) target.WsPoolSize = wsPoolSize;
        if (dto.MaxConnections is { } maxConnections) target.MaxConnections = maxConnections;
        if (dto.RateLimitPerIp is { } rateLimitPerIp) target.RateLimitPerIp = rateLimitPerIp;
        if (dto.RateLimitWindowSeconds is { } rateLimitWindow) target.RateLimitWindowSeconds = rateLimitWindow;
        if (dto.MetricsPort is { } metricsPort) target.MetricsPort = metricsPort;
        if (dto.MetricsHost is { } metricsHost) target.MetricsHost = metricsHost;
        if (dto.WebApiToken is { } webApiToken) target.WebApiToken = webApiToken;
        if (dto.EnablePerIpStats is { } enablePerIpStats) target.EnablePerIpStats = enablePerIpStats;
        if (dto.PerIpStatsTtlSeconds is { } perIpTtl) target.PerIpStatsTtlSeconds = perIpTtl;
        if (dto.PerIpStatsMaxPeers is { } perIpMaxPeers) target.PerIpStatsMaxPeers = perIpMaxPeers;
        if (dto.PrometheusTopPeers is { } prometheusTopPeers) target.PrometheusTopPeers = prometheusTopPeers;

        if (dto.EnableSwagger is { } enableSwagger) target.EnableSwagger = enableSwagger;
        if (dto.TimeseriesWindowSeconds is { } tsWindow) target.TimeseriesWindowSeconds = tsWindow;
        if (dto.TimeseriesStepSeconds is { } tsStep) target.TimeseriesStepSeconds = tsStep;
        if (dto.LogMaxMegabytes is { } logMaxMegabytes) target.LogMaxMegabytes = logMaxMegabytes;
        if (dto.LogRetainedFileCount is { } logRetainedFileCount) target.LogRetainedFileCount = logRetainedFileCount;
        if (dto.CfProxyEnabled is { } cfProxyEnabled) target.CfProxyEnabled = cfProxyEnabled;
        if (dto.CfProxyH2Media is { } cfProxyH2Media) target.CfProxyH2Media = cfProxyH2Media;
        if (dto.DisableSecure is { } disableSecure) target.DisableSecure = disableSecure;
        if (dto.FakeTlsDomain is { } fakeTlsDomain) target.FakeTlsDomain = fakeTlsDomain;
        if (dto.ProxyProtocol is { } proxyProtocol) target.ProxyProtocol = proxyProtocol;
        if (dto.ForceTestDc is { } forceTestDc) target.ForceTestDc = forceTestDc;

        if (dto.DcIp is { } dcIp) Replace(target.DcIp, dcIp);
        if (dto.AllowIp is { } allowIp) Replace(target.AllowIp, allowIp);
        if (dto.DenyIp is { } denyIp) Replace(target.DenyIp, denyIp);
        if (dto.CfProxyDomains is { } cfProxyDomains) Replace(target.CfProxyDomains, cfProxyDomains);
        if (dto.CfProxyWorkerDomains is { } cfWorkerDomains) Replace(target.CfProxyWorkerDomains, cfWorkerDomains);
        if (dto.Tokens is { } tokens) ReplaceTokens(target.Tokens, tokens);
    }

    /// <summary>
    /// Снимает полное представление конфигурации (все ключи со значениями).
    /// Используется при записи файла и в ответе <c>GET /api/config</c>.
    /// </summary>
    public static ConfigDto FromConfig(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ConfigDto
        {
            Port = config.Port,
            Host = config.Host,
            PublicHost = config.PublicHost,
            DcIp = [.. config.DcIp],
            AllowIp = [.. config.AllowIp],
            DenyIp = [.. config.DenyIp],
            Verbose = config.Verbose,
            LogPath = config.LogPath,
            AllowInvalidCertificates = config.AllowInvalidCertificates,
            WsMaxFrameBytes = config.WsMaxFrameBytes,
            WsConnectTimeoutSeconds = config.WsConnectTimeoutSeconds,
            SocketBufferKb = config.SocketBufferKb,
            WsPoolSize = config.WsPoolSize,
            MaxConnections = config.MaxConnections,
            RateLimitPerIp = config.RateLimitPerIp,
            RateLimitWindowSeconds = config.RateLimitWindowSeconds,
            MetricsPort = config.MetricsPort,
            MetricsHost = config.MetricsHost,
            WebApiToken = config.WebApiToken,
            EnablePerIpStats = config.EnablePerIpStats,
            PerIpStatsTtlSeconds = config.PerIpStatsTtlSeconds,
            PerIpStatsMaxPeers = config.PerIpStatsMaxPeers,
            PrometheusTopPeers = config.PrometheusTopPeers,
            EnableSwagger = config.EnableSwagger,
            TimeseriesWindowSeconds = config.TimeseriesWindowSeconds,
            TimeseriesStepSeconds = config.TimeseriesStepSeconds,
            LogMaxMegabytes = config.LogMaxMegabytes,
            LogRetainedFileCount = config.LogRetainedFileCount,
            CfProxyEnabled = config.CfProxyEnabled,
            CfProxyDomains = [.. config.CfProxyDomains],
            CfProxyWorkerDomains = [.. config.CfProxyWorkerDomains],
            CfProxyH2Media = config.CfProxyH2Media,
            DisableSecure = config.DisableSecure,
            FakeTlsDomain = config.FakeTlsDomain,
            ProxyProtocol = config.ProxyProtocol,
            ForceTestDc = config.ForceTestDc,
            Tokens = [.. config.Tokens.Select(static token => token.Clone())],
        };
    }

    /// <summary>Заменяет содержимое целевого списка элементами источника.</summary>
    private static void Replace(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                target.Add(item.Trim());
            }
        }
    }

    /// <summary>
    /// Заменяет набор токенов целиком, копируя элементы: DTO живёт недолго, а конфигурация
    /// должна оставаться независимой от объекта, пришедшего из файла или из тела запроса.
    /// </summary>
    private static void ReplaceTokens(List<ProxyToken> target, IEnumerable<ProxyToken> source)
    {
        target.Clear();
        foreach (var token in source)
        {
            if (token is not null)
            {
                target.Add(token.Clone());
            }
        }
    }
}
