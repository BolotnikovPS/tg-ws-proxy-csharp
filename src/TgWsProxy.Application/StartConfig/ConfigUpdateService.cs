using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Применяет изменения конфигурации из Web API: классифицирует ключи по трём категориям
/// (F-2.11), проверяет, пишет файл атомарно, перезапускает слушатель при необходимости и
/// обновляет «горячие» подсистемы.
///
/// Применение транзакционно (F-2.13): сначала собирается и валидируется <em>кандидат</em>,
/// затем он пишется на диск, затем применяется. Если перепривязка слушателя не удалась,
/// файл откатывается к прежнему содержимому, а значения кандидата не попадают в живой
/// <see cref="Config"/> — процесс остаётся в согласованном состоянии.
/// </summary>
public sealed class ConfigUpdateService(
    Config config,
    ConfigRuntime runtime,
    ConnectionLimiter connectionLimiter,
    PeerRateLimiter peerRateLimiter,
    PeerAccessControl accessControl,
    IListenerController listenerController,
    ITimeseriesBufferConfigurator timeseries,
    IProxyTokenRegistry tokenRegistry,
    ILogger<ConfigUpdateService> logger)
{
    /// <summary>
    /// Ключи, применяемые к работающим подсистемам без перезапуска слушателя (F-2.11, § 4).
    /// </summary>
    private static readonly HashSet<string> HotKeys = new(StringComparer.Ordinal)
    {
        "maxConnections",
        "rateLimitPerIp",
        "rateLimitWindowSeconds",
        "allowIp",
        "denyIp",
        // Токен читается middleware на каждом запросе, поэтому смена действует сразу.
        "webApiToken",
        "publicHost",
        // Внешний домен для bridge-ссылки пересчитывается без перезапуска листенера.
        "webProxyPublicHost",
        "enablePerIpStats",
        "perIpStatsTtlSeconds",
        "perIpStatsMaxPeers",
        // F-9.6: предел выгрузки per-IP серий читается при формировании ответа /metrics.
        "prometheusTopPeers",
        "timeseriesWindowSeconds",
        "timeseriesStepSeconds",
        // F-8.9: список токенов применяется к живому процессу без перезапуска слушателя —
        // новые handshake сразу используют новый набор, активные сессии сохраняются.
        "tokens",
    };

    /// <summary>
    /// Ключи, требующие перезапуска слушателя: смена привязки, DC-адресов, TLS- и CF-настроек
    /// (F-2.11, § 2.7). Активные сессии при этом разрываются (F-7.4).
    /// </summary>
    private static readonly HashSet<string> ListenerKeys = new(StringComparer.Ordinal)
    {
        "proxyProtocol",
        "dcIp",
        "allowInvalidCertificates",
        "wsMaxFrameBytes",
        "wsConnectTimeoutSeconds",
        "socketBufferKb",
        "wsPoolSize",
        "forceTestDc",
        "fakeTlsDomain",
        "cfProxyEnabled",
        "cfProxyDomains",
        "cfProxyWorkerDomains",
        "cfProxyH2Media",
        "disableSecure",
    };

    /// <summary>
    /// Ключи, читаемые только при запуске процесса (F-2.11, § 4.4). Попытка изменить их через
    /// API отклоняется с <c>409</c> и списком <c>startupOnly</c> (F-2.12).
    /// </summary>
    private static readonly HashSet<string> StartupOnlyKeys = new(StringComparer.Ordinal)
    {
        // Привязка слушателя и уровень журналирования меняются только файлом: панель
        // показывает их как справочные значения, а API отклоняет правку с 409.
        "port",
        "host",
        "verbose",
        "logPath",
        "logMaxMegabytes",
        "logRetainedFileCount",
        "metricsPort",
        "metricsHost",
        "enableSwagger",
        // Bridge-листенер (Kestrel второй endpoint) привязывается и настраивает TLS только
        // при старте процесса; включение/выключение и адрес порта нельзя применить на лету,
        // поэтому API отклоняет их правку с 409.
        "webProxyEnabled",
        "webProxyHost",
        "webProxyPort",
        "webProxyBasePath",
        "webProxyCertPath",
        "webProxyCertPassword",
    };

    private static readonly JsonSerializerOptions DiffOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Путь к файлу, в который пишет API.</summary>
    public string Path => runtime.Path;

    /// <summary>
    /// Возвращает текущую конфигурацию с маскированными секретами (F-2.10).
    /// </summary>
    public ConfigDto GetMasked() => Mask(config);

    /// <summary>
    /// Возвращает конфигурацию по умолчанию для кнопки «Сбросить» (F-5.10). Ничего не пишет:
    /// пользователь должен подтвердить сброс через <c>PUT /api/config</c>.
    /// </summary>
    public ConfigDto GetDefaults()
    {
        var defaults = new Config();
        ConfigDefaults.Apply(defaults);
        return Mask(defaults);
    }

    /// <summary>
    /// Применяет частичное обновление: <c>null</c> в полях DTO означает «не менять».
    /// </summary>
    /// <param name="patch">Изменения из тела запроса.</param>
    /// <param name="cancellationToken">Отмена записи.</param>
    /// <exception cref="ArgumentException">Конфигурация не проходит валидацию.</exception>
    /// <exception cref="StartupOnlyKeysException">
    /// Запрос меняет ключи категории «только старт» (F-2.12).
    /// </exception>
    /// <exception cref="ListenerRestartInProgressException">
    /// Перезапуск слушателя уже идёт (F-7.6).
    /// </exception>
    /// <exception cref="ListenerRebindException">Перепривязка не удалась, выполнен откат (F-7.5).</exception>
    public async Task<ConfigUpdateResult> ApplyAsync(ConfigDto patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // Маскированные значения из формы не должны попасть в файл вместо настоящих секретов.
        if (IsMasked(patch.WebApiToken))
        {
            patch.WebApiToken = null;
        }

        if (IsMasked(patch.WebProxyCertPassword))
        {
            patch.WebProxyCertPassword = null;
        }

        if (patch.Tokens is { } patchedTokens)
        {
            // Секреты токенов приходят из GET /api/config в маскированном виде; такое значение
            // означает «не менять» и подменяется текущим секретом того же токена (F-8.8).
            foreach (var token in patchedTokens)
            {
                if (token is null || !IsMasked(token.Secret))
                {
                    continue;
                }

                var existing = config.Tokens.Find(t =>
                    string.Equals(t.Id, token.Id, StringComparison.Ordinal));
                token.Secret = existing?.Secret ?? "";
            }

            // Секрет, которого нет в прежнем наборе (новый токен с маской), не может быть
            // восстановлен — запрос отклоняется, иначе в файл попала бы маска.
            var unresolved = patchedTokens
                .Where(t => t is not null && IsMasked(t.Secret))
                .Select(t => t!.Name)
                .ToList();

            if (unresolved.Count > 0)
            {
                throw new ArgumentException(
                    "Token secrets must be provided in clear text: " + string.Join(", ", unresolved));
            }
        }

        await runtime.Sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = BuildCandidate(patch);
            ConfigValidator.Validate(candidate);

            var changed = Diff(ConfigDtoMapper.FromConfig(config), ConfigDtoMapper.FromConfig(candidate));

            // F-2.12: ключи «только старт» отклоняются до записи файла — иначе диск и память
            // разойдутся, а пользователь получит ложное «сохранено».
            var startupOnly = changed.Where(StartupOnlyKeys.Contains).ToList();
            if (startupOnly.Count > 0)
            {
                logger.LogWarning(
                    "Configuration rejected: startup-only keys requested [{Keys}]",
                    string.Join(", ", startupOnly));
                throw new StartupOnlyKeysException(startupOnly);
            }

            var hot = changed.Where(HotKeys.Contains).ToList();
            var listener = changed.Where(ListenerKeys.Contains).ToList();

            // Снимок прежнего файла нужен для отката, если перепривязка не удастся (F-2.13).
            var previousFile = ConfigFileLoader.Serialize(ConfigDtoMapper.FromConfig(config));

            ConfigFileLoader.Save(runtime.Path, candidate);

            var listenerRestarted = false;
            var droppedSessions = 0;

            if (listener.Count > 0)
            {
                try
                {
                    var result = await listenerController
                        .RestartAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);

                    if (!result.Restarted)
                    {
                        RollbackFile(previousFile);
                        logger.LogError(
                            "Listener rebind failed, configuration rolled back: {Error}",
                            result.Error);
                        throw new ListenerRebindException(
                            result.Error ?? "Listener rebind failed.");
                    }

                    listenerRestarted = true;
                    droppedSessions = result.DroppedSessions;
                }
                catch (ListenerRestartInProgressException)
                {
                    // Файл уже записан — возвращаем прежнее содержимое, чтобы он не опережал память.
                    RollbackFile(previousFile);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    RollbackFile(previousFile);
                    throw;
                }
            }

            // «Горячие» ключи применяются и при перезапуске слушателя: часть из них
            // (например, лимиты) не входит в набор подсистем, поднимаемых заново.
            ApplyHot(candidate, hot);

            var warnings = CollectWarnings(candidate);
            logger.LogInformation(
                "Configuration saved to {Path}: {Count} key(s) changed, hot-reloaded [{Hot}], " +
                "listener restart [{Listener}] (restarted: {Restarted}, dropped sessions: {Dropped})",
                runtime.Path,
                changed.Count,
                string.Join(", ", hot),
                string.Join(", ", listener),
                listenerRestarted,
                droppedSessions);

            return new ConfigUpdateResult(
                true,
                runtime.Path,
                hot,
                listenerRestarted,
                droppedSessions,
                warnings);
        }
        finally
        {
            runtime.Sync.Release();
        }
    }

    /// <summary>Возвращает файл к прежнему содержимому после неудачного применения (F-2.13).</summary>
    private void RollbackFile(string previousContent)
    {
        try
        {
            ConfigFileLoader.WriteRaw(runtime.Path, previousContent);
            logger.LogWarning("Configuration file {Path} restored to its previous content", runtime.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to roll back configuration file {Path}", runtime.Path);
        }
    }

    /// <summary>Собирает кандидата: текущие значения плюс непустые поля патча.</summary>
    private Config BuildCandidate(ConfigDto patch)
    {
        var candidate = new Config();
        ConfigDtoMapper.Apply(ConfigDtoMapper.FromConfig(config), candidate);
        ConfigDtoMapper.Apply(patch, candidate);
        return candidate;
    }

    /// <summary>
    /// Переносит «горячие» ключи в живой конфиг и подсистемы. Ключи категории «слушатель»
    /// уже применены перезапуском (он переносит всего кандидата целиком), поэтому здесь
    /// повторно не трогаются.
    /// </summary>
    private void ApplyHot(Config candidate, IReadOnlyList<string> hotKeys)
    {
        foreach (var key in hotKeys)
        {
            switch (key)
            {
                case "maxConnections":
                    config.MaxConnections = candidate.MaxConnections;
                    connectionLimiter.UpdateLimit(candidate.MaxConnections);
                    break;

                case "rateLimitPerIp":
                case "rateLimitWindowSeconds":
                    config.RateLimitPerIp = candidate.RateLimitPerIp;
                    config.RateLimitWindowSeconds = candidate.RateLimitWindowSeconds;
                    peerRateLimiter.UpdateLimits(candidate.RateLimitPerIp, candidate.RateLimitWindowSeconds);
                    break;

                case "allowIp":
                case "denyIp":
                    ReplaceAll(config.AllowIp, candidate.AllowIp);
                    ReplaceAll(config.DenyIp, candidate.DenyIp);
                    accessControl.Update(config.AllowIp, config.DenyIp);
                    break;

                case "webApiToken":
                    // Middleware читает Config.WebApiToken на каждом запросе (F-3.8).
                    config.WebApiToken = candidate.WebApiToken;
                    break;

                case "publicHost":
                    // Ссылка t.me/proxy собирается при каждом запросе /api/tokens/{id}/link,
                    // поэтому внешний адрес применяется без перезапуска.
                    config.PublicHost = candidate.PublicHost;
                    break;

                case "enablePerIpStats":
                    config.EnablePerIpStats = candidate.EnablePerIpStats;
                    break;

                case "perIpStatsTtlSeconds":
                    config.PerIpStatsTtlSeconds = candidate.PerIpStatsTtlSeconds;
                    break;

                case "perIpStatsMaxPeers":
                    config.PerIpStatsMaxPeers = candidate.PerIpStatsMaxPeers;
                    break;

                case "prometheusTopPeers":
                    config.PrometheusTopPeers = candidate.PrometheusTopPeers;
                    break;

                case "timeseriesWindowSeconds":
                case "timeseriesStepSeconds":
                    config.TimeseriesWindowSeconds = candidate.TimeseriesWindowSeconds;
                    config.TimeseriesStepSeconds = candidate.TimeseriesStepSeconds;
                    // Буфер пересоздаётся: точки, снятые с прежним шагом, нельзя пересчитать.
                    timeseries.Reconfigure(
                        candidate.TimeseriesStepSeconds,
                        candidate.TimeseriesWindowSeconds);
                    break;

                case "tokens":
                    // F-8.9: набор токенов копируется в живой конфиг; реестр читает его
                    // лениво, поэтому следующий handshake уже видит новый список.
                    config.Tokens.Clear();
                    config.Tokens.AddRange(candidate.Tokens.Select(static token => token.Clone()));
                    tokenRegistry.Reload();
                    break;
            }
        }
    }

    /// <summary>Некритичные замечания, которые страница показывает после сохранения.</summary>
    private static List<string> CollectWarnings(Config candidate)
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(candidate.WebApiToken) && !IsLoopback(candidate.MetricsHost))
        {
            warnings.Add(
                $"metricsHost = {candidate.MetricsHost} is not a loopback address and webApiToken is empty: " +
                "the Web API will be reachable without authentication.");
        }

        if (!candidate.EnablePerIpStats)
        {
            warnings.Add("enablePerIpStats = false: /api/per-ip will return an empty list.");
        }

        if (candidate.RateLimitPerIp > 0 && candidate.RateLimitWindowSeconds <= 0)
        {
            warnings.Add("rateLimitPerIp is set but rateLimitWindowSeconds is 0: rate limiting is disabled.");
        }

        return warnings;
    }

    /// <summary>
    /// Копирует DTO с маскированными чувствительными значениями: секреты токенов доступа и
    /// bearer-токен Web API никогда не покидают процесс в открытом виде (F-2.10, F-8.8).
    /// </summary>
    private static ConfigDto Mask(Config source)
    {
        var dto = ConfigDtoMapper.FromConfig(source);
        dto.WebApiToken = MaskSecret(source.WebApiToken);
        dto.WebProxyCertPassword = MaskSecret(source.WebProxyCertPassword);
        if (dto.Tokens is { } tokens)
        {
            foreach (var token in tokens)
            {
                token.Secret = MaskSecret(token.Secret);
            }
        }

        return dto;
    }

    /// <summary>
    /// Определяет, пришло ли значение из маскированного ответа <c>GET /api/config</c>.
    /// Такие значения означают «не менять», иначе маска затрёт настоящий секрет в файле.
    /// </summary>
    private static bool IsMasked(string? value) => SecretMask.IsMasked(value);

    /// <summary>
    /// Маскирует секрет как <c>abcd…ef01</c>: первые и последние четыре символа. Пустой секрет
    /// остаётся пустым, чтобы страница понимала, что он не задан.
    /// </summary>
    internal static string MaskSecret(string? secret) => SecretMask.Mask(secret);

    /// <summary>Список ключей, значения которых отличаются между двумя DTO.</summary>
    private static List<string> Diff(ConfigDto before, ConfigDto after)
    {
        var left = JsonSerializer.SerializeToNode(before, DiffOptions) as JsonObject;
        var right = JsonSerializer.SerializeToNode(after, DiffOptions) as JsonObject;

        if (left is null || right is null)
        {
            return [];
        }

        var changed = new List<string>();
        foreach (var (key, value) in right)
        {
            if (!left.TryGetPropertyValue(key, out var previous) || !JsonNode.DeepEquals(previous, value))
            {
                changed.Add(key);
            }
        }

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    private static void ReplaceAll(List<string> target, List<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    /// <summary>Проверяет, что адрес прослушивания — loopback (F-3.7).</summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
