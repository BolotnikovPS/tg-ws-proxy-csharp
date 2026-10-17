#nullable enable

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Аргументы командной строки в режиме «только веб» (F-1.9, F-1.10).
///
/// Конфигурация задаётся исключительно файлом <c>config.json</c> и страницей настроек,
/// поэтому от CLI остались только bootstrap-опции: путь к файлу, справка и версия.
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

    /// <summary>Запрошена справка (<c>-h</c>/<c>--help</c>).</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Запрошена версия (<c>--version</c>).</summary>
    public bool ShowVersion { get; init; }

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
        var showHelp = false;
        var showVersion = false;

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

                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--version":
                    showVersion = true;
                    break;

                default:
                    throw new ArgumentException(BuildUnknownArgumentMessage(arg));
            }
        }

        return new BootstrapOptions
        {
            ConfigPath = configPath,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
        };
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
                   $"the proxy is configured by config.json only, use the \"{jsonKey}\" key there " +
                   "(see --help).";
        }

        return $"Unknown argument: {arg}. The proxy is configured by config.json only; " +
               "run with --help to see the remaining bootstrap arguments.";
    }

    /// <summary>
    /// Прежние флаги CLI и заменяющие их ключи <c>config.json</c>. Служит только для подсказок
    /// в сообщении об ошибке: разбора этих флагов больше нет.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyFlags = new(StringComparer.Ordinal)
    {
        ["--port"] = "port",
        ["--host"] = "host",
        ["--secret"] = "tokens",
        ["--dc-ip"] = "dcIp",
        ["--cfproxy-domain"] = "cfProxyDomains",
        ["--cfproxy-worker-domain"] = "cfProxyWorkerDomains",
        ["--no-cfproxy"] = "cfProxyEnabled",
        ["--no-h2"] = "cfProxyH2Media",
        ["--cfproxy-h2-media"] = "cfProxyH2Media",
        ["--no-secure"] = "disableSecure",
        ["--fake-tls-domain"] = "fakeTlsDomain",
        ["--force-test-dc"] = "forceTestDc",
        ["--proxy-protocol"] = "proxyProtocol",
        ["-v"] = "verbose",
        ["--verbose"] = "verbose",
        ["--log-file"] = "logPath",
        ["--log-path"] = "logPath",
        ["--log-max-mb"] = "logMaxMegabytes",
        ["--log-backups"] = "logRetainedFileCount",
        ["--buf-kb"] = "socketBufferKb",
        ["--pool-size"] = "wsPoolSize",
        ["--ws-timeout"] = "wsConnectTimeoutSeconds",
        ["--ws-max-frame-bytes"] = "wsMaxFrameBytes",
        ["--allow-invalid-certs"] = "allowInvalidCertificates",
        ["--allow-ip"] = "allowIp",
        ["--deny-ip"] = "denyIp",
        ["--max-connections"] = "maxConnections",
        ["--rate-limit"] = "rateLimitPerIp",
        ["--rate-limit-window"] = "rateLimitWindowSeconds",
        ["--metrics-port"] = "metricsPort",
        ["--metrics-host"] = "metricsHost",
        ["--web-api-token"] = "webApiToken",
        ["--enable-per-ip-stats"] = "enablePerIpStats",
        ["--no-per-ip-stats"] = "enablePerIpStats",
        ["--per-ip-stats-ttl"] = "perIpStatsTtlSeconds",
        ["--per-ip-stats-max-peers"] = "perIpStatsMaxPeers",
        ["--timeseries-window"] = "timeseriesWindowSeconds",
        ["--timeseries-step"] = "timeseriesStepSeconds",
    };

    /// <summary>Справка по оставшимся bootstrap-опциям (F-1.7).</summary>
    public const string HelpText =
        """
        usage: TgWsProxy [--config PATH] [-h|--help] [--version]

        Telegram MTProto WebSocket Bridge Proxy — web-only edition.

        The proxy always runs as a web application: it serves metrics, health
        checks and the settings page over HTTP. Every proxy setting (listener,
        tokens, limits, statistics) lives in config.json and is edited on the
        settings page — the command line only points to the file.

        options:
          --config PATH   путь к файлу конфигурации (также --config=PATH).
                          По умолчанию: переменная окружения TGWS_CONFIG,
                          затем ./config.json в рабочей директории
                          (в контейнере — /app/config/config.json)
          -h, --help      показать эту справку и выйти
          --version       показать версию и выйти

        Настройки и мониторинг доступны на странице:
          http://<metricsHost>:<metricsPort>/

        Отсутствующий файл конфигурации — не ошибка: применяются умолчания,
        а файл создаётся при первом сохранении из панели.
        """;
}
