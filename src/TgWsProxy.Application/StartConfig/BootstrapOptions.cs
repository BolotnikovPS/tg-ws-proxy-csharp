namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Аргументы командной строки в режиме «только веб» (F-1.9, F-1.10).
///
/// Конфигурация задаётся исключительно файлом <c>config.json</c> и страницей настроек,
/// поэтому от CLI осталась единственная bootstrap-опция — путь к файлу конфигурации.
///
/// Любой другой аргумент — ошибка, а не молчаливое игнорирование: опечатка вида
/// <c>--prot 1443</c> иначе привела бы к старту на умолчаниях и к путанице «почему не
/// применилось». Для известных прежних флагов сообщение содержит JSON-ключ-замену.
/// </summary>
public sealed record BootstrapOptions
{
    /// <summary>Флаг пути к файлу конфигурации. Поддерживаются формы <c>--config PATH</c> и <c>--config=PATH</c>.</summary>
    public const string ConfigFlag = "--config";

    /// <summary>Путь к файлу конфигурации; <c>null</c> — искать по умолчанию (F-2.3).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// Разбирает аргументы командной строки.
    ///
    /// Повторный <c>--config</c> разрешён: выигрывает последнее вхождение, как и раньше.
    /// </summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <returns>Разобранные bootstrap-опции.</returns>
    /// <exception cref="ArgumentException">
    /// Аргумент не относится к bootstrap-опциям либо у <c>--config</c> нет значения.
    /// </exception>
    public static BootstrapOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? configPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith(ConfigFlag + "=", StringComparison.Ordinal))
            {
                configPath = RequirePath(arg[(ConfigFlag.Length + 1)..], $"{ConfigFlag}={arg[(ConfigFlag.Length + 1)..]}");
                continue;
            }

            switch (arg)
            {
                case ConfigFlag:
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{ConfigFlag} requires a path to config.json");
                    }

                    configPath = RequirePath(args[++i], ConfigFlag);
                    break;

                default:
                    throw new ArgumentException(BuildUnknownArgumentMessage(arg));
            }
        }

        return new BootstrapOptions { ConfigPath = configPath };
    }

    /// <summary>Проверяет, что значение пути не пустое.</summary>
    private static string RequirePath(string raw, string source)
    {
        var path = raw.Trim();
        if (path.Length == 0)
        {
            throw new ArgumentException($"{source} requires a non-empty path to config.json");
        }

        return path;
    }

    /// <summary>
    /// Строит сообщение о неизвестном аргументе. Для прежних флагов добавляется подсказка
    /// «флаг → ключ config.json» (F-1.10).
    /// </summary>
    private static string BuildUnknownArgumentMessage(string arg)
    {
        // Форма --flag=value в подсказке должна опознаваться как тот же флаг.
        var name = arg;
        var equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex > 0)
        {
            name = arg[..equalsIndex];
        }

        if (LegacyFlags.TryGetValue(name, out var jsonKey))
        {
            return $"Unknown argument: {name}. Command-line configuration was removed: " +
                   $"the proxy is configured by config.json only, use the \"{jsonKey}\" key there.";
        }

        return $"Unknown argument: {arg}. The proxy is configured by config.json only; " +
               $"the only supported argument is {ConfigFlag} <path>.";
    }

    /// <summary>
    /// Прежние флаги CLI и заменяющие их ключи <c>config.json</c>. Служит только для подсказок
    /// в сообщении об ошибке: разбора этих флагов больше нет.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyFlags = new(StringComparer.Ordinal)
    {
        ["--port"] = "mtproto.port",
        ["--secret"] = "tokens",
        ["--dc-ip"] = "mtproto.dcIp",
        ["--cfproxy-domain"] = "cfProxy.domains",
        ["--cfproxy-worker-domain"] = "cfProxy.workerDomains",
        ["--no-cfproxy"] = "cfProxy.enabled",
        ["--no-h2"] = "cfProxy.h2Media",
        ["--cfproxy-h2-media"] = "cfProxy.h2Media",
        ["--no-secure"] = "disableSecure",
        ["--fake-tls-domain"] = "fakeTlsDomain",
        ["--force-test-dc"] = "forceTestDc",
        ["--proxy-protocol"] = "proxyProtocol",
        ["-v"] = "verbose",
        ["--verbose"] = "verbose",
        ["--log-file"] = "logs.path",
        ["--log-path"] = "logs.path",
        ["--log-max-mb"] = "logs.maxMegabytes",
        ["--log-backups"] = "logs.retainedFileCount",
        ["--buf-kb"] = "mtproto.socketBufferKb",
        ["--pool-size"] = "mtproto.wsPoolSize",
        ["--ws-timeout"] = "mtproto.wsConnectTimeoutSeconds",
        ["--ws-max-frame-bytes"] = "mtproto.wsMaxFrameBytes",
        ["--allow-invalid-certs"] = "mtproto.allowInvalidCertificates",
        ["--allow-ip"] = "allowIp",
        ["--deny-ip"] = "denyIp",
        ["--max-connections"] = "maxConnections",
        ["--rate-limit"] = "rateLimitPerIp",
        ["--rate-limit-window"] = "rateLimitWindowSeconds",
        ["--web-api-token"] = "webApiToken",
        ["--enable-per-ip-stats"] = "stats.enabled",
        ["--no-per-ip-stats"] = "stats.enabled",
        ["--per-ip-stats-ttl"] = "stats.ttlSeconds",
        ["--per-ip-stats-max-peers"] = "stats.maxPeers",
        ["--timeseries-window"] = "timeseriesWindowSeconds",
        ["--timeseries-step"] = "timeseriesStepSeconds",
    };
}
