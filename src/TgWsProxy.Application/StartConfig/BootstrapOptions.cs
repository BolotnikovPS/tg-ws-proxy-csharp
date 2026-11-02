namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Аргументы командной строки в режиме «только веб».
///
/// Конфигурация задаётся исключительно файлом <c>config.json</c> и страницей настроек;
/// путь к файлу — только переменной окружения <c>TGWS_CONFIG</c> (иначе <c>config.json</c>
/// в текущем каталоге). Аргументы конфигурацию не задают, поэтому любой аргумент — ошибка,
/// а не молчаливое игнорирование: опечатка вида <c>--prot 1443</c> иначе привела бы к старту
/// на умолчаниях и к путанице «почему не применилось». Для известных прежних флагов
/// сообщение содержит JSON-ключ-замену.
/// </summary>
public sealed record BootstrapOptions
{
    /// <summary>Прежний флаг пути к файлу конфигурации. Удалён: путь задаёт только <c>TGWS_CONFIG</c>.</summary>
    public const string ConfigFlag = "--config";

    /// <summary>Экземпляр для безаргументного запуска.</summary>
    public static readonly BootstrapOptions Empty = new();

    /// <summary>
    /// Проверяет аргументы командной строки и отклоняет любые: конфигурация задаётся файлом,
    /// а путь к нему — переменной окружения <c>TGWS_CONFIG</c>.
    /// </summary>
    /// <param name="args">Аргументы процесса.</param>
    /// <returns><see cref="Empty"/> при пустых аргументах.</returns>
    /// <exception cref="ArgumentException">Передан любой аргумент, включая прежний <c>--config</c>.</exception>
    public static BootstrapOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return Empty;
        }

        var arg = args[0];

        // --config удалён: путь к файлу задаёт только переменная окружения TGWS_CONFIG.
        if (arg == ConfigFlag || arg.StartsWith(ConfigFlag + "=", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The {ConfigFlag} argument was removed. Set the TGWS_CONFIG environment variable " +
                "to point at your config.json, or run without it to use ./config.json in the working directory.");
        }

        throw new ArgumentException(BuildUnknownArgumentMessage(arg));
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
               $"command-line arguments are not supported.";
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
