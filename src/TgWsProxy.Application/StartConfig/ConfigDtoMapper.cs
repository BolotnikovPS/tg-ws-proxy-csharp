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

        if (dto.MtProto is { } mt)
        {
            if (mt.Port is { } port) target.MtProto.Port = port;
            if (mt.PublicHost is { } publicHost) target.MtProto.PublicHost = publicHost.Trim();
            if (mt.AllowInvalidCertificates is { } allowInvalid) target.MtProto.AllowInvalidCertificates = allowInvalid;
            if (mt.WsMaxFrameBytes is { } wsMaxFrameBytes) target.MtProto.WsMaxFrameBytes = wsMaxFrameBytes;
            if (mt.WsConnectTimeoutSeconds is { } wsTimeout) target.MtProto.WsConnectTimeoutSeconds = wsTimeout;
            if (mt.SocketBufferKb is { } socketBufferKb) target.MtProto.SocketBufferKb = socketBufferKb;
            if (mt.WsPoolSize is { } wsPoolSize) target.MtProto.WsPoolSize = wsPoolSize;
            if (mt.DcIp is { } dcIp) Replace(target.MtProto.DcIp, dcIp);
        }

        if (dto.Tokens is { } tokens) ReplaceTokens(target.Tokens, tokens);

        if (dto.Stats is { } stats)
        {
            if (stats.Enabled is { } enabled) target.Stats.Enabled = enabled;
            if (stats.TtlSeconds is { } ttl) target.Stats.TtlSeconds = ttl;
            if (stats.MaxPeers is { } maxPeers) target.Stats.MaxPeers = maxPeers;
        }

        if (dto.Verbose is { } verbose) target.Verbose = verbose;
        if (dto.MaxConnections is { } maxConnections) target.MaxConnections = maxConnections;
        if (dto.RateLimitPerIp is { } rateLimitPerIp) target.RateLimitPerIp = rateLimitPerIp;
        if (dto.RateLimitWindowSeconds is { } rateLimitWindow) target.RateLimitWindowSeconds = rateLimitWindow;
        if (dto.WebApiToken is { } webApiToken) target.WebApiToken = webApiToken;
        if (dto.PrometheusTopPeers is { } prometheusTopPeers) target.PrometheusTopPeers = prometheusTopPeers;

        if (dto.EnableSwagger is { } enableSwagger) target.EnableSwagger = enableSwagger;
        if (dto.TimeseriesWindowSeconds is { } tsWindow) target.TimeseriesWindowSeconds = tsWindow;
        if (dto.TimeseriesStepSeconds is { } tsStep) target.TimeseriesStepSeconds = tsStep;

        if (dto.Logs is { } logs)
        {
            if (logs.Path is { } path) target.Logs.Path = path;
            if (logs.MaxMegabytes is { } maxMegabytes) target.Logs.MaxMegabytes = maxMegabytes;
            if (logs.RetainedFileCount is { } retainedFileCount) target.Logs.RetainedFileCount = retainedFileCount;
        }

        if (dto.CfProxy is { } cf)
        {
            if (cf.Enabled is { } cfEnabled) target.CfProxy.Enabled = cfEnabled;
            if (cf.H2Media is { } cfH2Media) target.CfProxy.H2Media = cfH2Media;
            if (cf.Domains is { } cfDomains) Replace(target.CfProxy.Domains, cfDomains);
            if (cf.WorkerDomains is { } cfWorkerDomains) Replace(target.CfProxy.WorkerDomains, cfWorkerDomains);
        }

        if (dto.DisableSecure is { } disableSecure) target.DisableSecure = disableSecure;
        if (dto.FakeTlsDomain is { } fakeTlsDomain) target.FakeTlsDomain = fakeTlsDomain;
        if (dto.ProxyProtocol is { } proxyProtocol) target.ProxyProtocol = proxyProtocol;
        if (dto.ForceTestDc is { } forceTestDc) target.ForceTestDc = forceTestDc;

        if (dto.WebProxy is { } wp)
        {
            if (wp.Enabled is { } wpEnabled) target.WebProxy.Enabled = wpEnabled;
            if (wp.Port is { } wpPort) target.WebProxy.Port = wpPort;
            if (wp.BasePath is { } wpBasePath) target.WebProxy.BasePath = wpBasePath;
            if (wp.PublicHost is { } wpPublicHost) target.WebProxy.PublicHost = wpPublicHost.Trim();
            if (wp.CertPath is { } wpCertPath) target.WebProxy.CertPath = wpCertPath;
            if (wp.CertPassword is { } wpCertPassword) target.WebProxy.CertPassword = wpCertPassword;
            if (wp.CarrierMode is { } wpCarrierMode) target.WebProxy.CarrierMode = wpCarrierMode.Trim();
            if (wp.MaxSessions is { } wpMaxSessions) target.WebProxy.MaxSessions = wpMaxSessions;
            if (wp.MaxSessionsPerIp is { } wpMaxSessionsPerIp) target.WebProxy.MaxSessionsPerIp = wpMaxSessionsPerIp;
            if (wp.MaxStreamsPerSession is { } wpMaxStreamsPerSession) target.WebProxy.MaxStreamsPerSession = wpMaxStreamsPerSession;
            if (wp.MaxStreams is { } wpMaxStreams) target.WebProxy.MaxStreams = wpMaxStreams;
            if (wp.MaxPendingBytesPerSession is { } wpMaxPendingBytes) target.WebProxy.MaxPendingBytesPerSession = wpMaxPendingBytes;
            if (wp.MaxPendingItemsPerSession is { } wpMaxPendingItems) target.WebProxy.MaxPendingItemsPerSession = wpMaxPendingItems;
        }

        if (dto.Otel is { } otel)
        {
            if (otel.Enabled is { } enabled) target.Otel.Enabled = enabled;
            if (otel.ServiceName is { } serviceName) target.Otel.ServiceName = serviceName.Trim();
            if (otel.Endpoint is { } endpoint) target.Otel.Endpoint = endpoint.Trim();
            if (otel.Protocol is { } protocol) target.Otel.Protocol = protocol.Trim();
            if (otel.ExportTraces is { } exportTraces) target.Otel.ExportTraces = exportTraces;
            if (otel.ExportLogs is { } exportLogs) target.Otel.ExportLogs = exportLogs;
            if (otel.ExportMetrics is { } exportMetrics) target.Otel.ExportMetrics = exportMetrics;
        }

        if (dto.AllowIp is { } allowIp) Replace(target.AllowIp, allowIp);
        if (dto.DenyIp is { } denyIp) Replace(target.DenyIp, denyIp);
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
            MtProto = new MtProtoConfigDto
            {
                Port = config.MtProto.Port,
                PublicHost = config.MtProto.PublicHost,
                DcIp = [.. config.MtProto.DcIp],
                AllowInvalidCertificates = config.MtProto.AllowInvalidCertificates,
                WsMaxFrameBytes = config.MtProto.WsMaxFrameBytes,
                WsConnectTimeoutSeconds = config.MtProto.WsConnectTimeoutSeconds,
                SocketBufferKb = config.MtProto.SocketBufferKb,
                WsPoolSize = config.MtProto.WsPoolSize,
            },
            Tokens = [.. config.Tokens.Select(static token => token.Clone())],
            AllowIp = [.. config.AllowIp],
            DenyIp = [.. config.DenyIp],
            Verbose = config.Verbose,
            MaxConnections = config.MaxConnections,
            RateLimitPerIp = config.RateLimitPerIp,
            RateLimitWindowSeconds = config.RateLimitWindowSeconds,
            WebApiToken = config.WebApiToken,
            Stats = new StatsConfigDto
            {
                Enabled = config.Stats.Enabled,
                TtlSeconds = config.Stats.TtlSeconds,
                MaxPeers = config.Stats.MaxPeers,
            },
            PrometheusTopPeers = config.PrometheusTopPeers,
            EnableSwagger = config.EnableSwagger,
            TimeseriesWindowSeconds = config.TimeseriesWindowSeconds,
            TimeseriesStepSeconds = config.TimeseriesStepSeconds,
            Logs = new LogsConfigDto
            {
                Path = config.Logs.Path,
                MaxMegabytes = config.Logs.MaxMegabytes,
                RetainedFileCount = config.Logs.RetainedFileCount,
            },
            CfProxy = new CfProxyConfigDto
            {
                Enabled = config.CfProxy.Enabled,
                H2Media = config.CfProxy.H2Media,
                Domains = [.. config.CfProxy.Domains],
                WorkerDomains = [.. config.CfProxy.WorkerDomains],
            },
            DisableSecure = config.DisableSecure,
            FakeTlsDomain = config.FakeTlsDomain,
            ProxyProtocol = config.ProxyProtocol,
            ForceTestDc = config.ForceTestDc,
            WebProxy = new WebProxyConfigDto
            {
                Enabled = config.WebProxy.Enabled,
                Port = config.WebProxy.Port,
                BasePath = config.WebProxy.BasePath,
                PublicHost = config.WebProxy.PublicHost,
                CertPath = config.WebProxy.CertPath,
                CertPassword = config.WebProxy.CertPassword,
                CarrierMode = config.WebProxy.CarrierMode,
                MaxSessions = config.WebProxy.MaxSessions,
                MaxSessionsPerIp = config.WebProxy.MaxSessionsPerIp,
                MaxStreamsPerSession = config.WebProxy.MaxStreamsPerSession,
                MaxStreams = config.WebProxy.MaxStreams,
                MaxPendingBytesPerSession = config.WebProxy.MaxPendingBytesPerSession,
                MaxPendingItemsPerSession = config.WebProxy.MaxPendingItemsPerSession,
            },
            Otel = new OtelConfigDto
            {
                Enabled = config.Otel.Enabled,
                ServiceName = config.Otel.ServiceName,
                Endpoint = config.Otel.Endpoint,
                Protocol = config.Otel.Protocol,
                ExportTraces = config.Otel.ExportTraces,
                ExportLogs = config.Otel.ExportLogs,
                ExportMetrics = config.Otel.ExportMetrics,
            },
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