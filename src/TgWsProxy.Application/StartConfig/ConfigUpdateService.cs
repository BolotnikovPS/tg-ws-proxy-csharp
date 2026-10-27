using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;

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
    private static readonly HashSet<string> HotKeys =
    [
        with(StringComparer.Ordinal),
        "maxConnections",
        "rateLimitPerIp",
        "rateLimitWindowSeconds",
        "allowIp",
        "denyIp",
        // Токен читается middleware на каждом запросе, поэтому смена действует сразу.
        "webApiToken",
        "mtproto.publicHost",
        // Внешний домен для bridge-ссылки пересчитывается без перезапуска листенера.
        "webProxy.publicHost",
        // Режим носителя и лимиты ресурсов читаются на каждом запросе carrier/сессии,
        // поэтому применяются к новым сессиям без перезапуска bridge-листенера (F-W.24, F-W.25).
        "webProxy.carrierMode",
        "webProxy.maxSessions",
        "webProxy.maxSessionsPerIp",
        "webProxy.maxStreamsPerSession",
        "webProxy.maxStreams",
        "webProxy.maxPendingBytesPerSession",
        "webProxy.maxPendingItemsPerSession",
        "stats.enabled",
        "stats.ttlSeconds",
        "stats.maxPeers",
        // F-9.6: предел выгрузки per-IP серий читается при формировании ответа /metrics.
        "prometheusTopPeers",
        "timeseriesWindowSeconds",
        "timeseriesStepSeconds",
        // F-8.9: список токенов применяется к живому процессу без перезапуска слушателя —
        // новые handshake сразу используют новый набор, активные сессии сохраняются.
        "tokens",
    ];

    /// <summary>
    /// Ключи, требующие перезапуска слушателя: смена привязки, DC-адресов, TLS- и CF-настроек
    /// (F-2.11, § 2.7). Активные сессии при этом разрываются (F-7.4).
    /// </summary>
    private static readonly HashSet<string> ListenerKeys =
    [
        with(StringComparer.Ordinal),
        "proxyProtocol",
        "mtproto.dcIp",
        "mtproto.allowInvalidCertificates",
        "mtproto.wsMaxFrameBytes",
        "mtproto.wsConnectTimeoutSeconds",
        "mtproto.socketBufferKb",
        "mtproto.wsPoolSize",
        "forceTestDc",
        "fakeTlsDomain",
        "cfProxy.enabled",
        "cfProxy.domains",
        "cfProxy.workerDomains",
        "cfProxy.h2Media",
        "disableSecure",
    ];

    /// <summary>
    /// Ключи, читаемые только при запуске процесса (F-2.11, § 4.4). Попытка изменить их через
    /// API отклоняется с <c>409</c> и списком <c>startupOnly</c> (F-2.12).
    /// </summary>
    private static readonly HashSet<string> StartupOnlyKeys =
    [
        with(StringComparer.Ordinal),
        // Привязка слушателя и уровень журналирования меняются только файлом: панель
        // показывает их как справочные значения, а API отклоняет правку с 409.
        "mtproto.port",
        "verbose",
        "logs.path",
        "logs.maxMegabytes",
        "logs.retainedFileCount",
        "enableSwagger",
        // Bridge-листенер (Kestrel второй endpoint) привязывается и настраивает TLS только
        // при старте процесса; включение/выключение и адрес порта нельзя применить на лету,
        // поэтому API отклоняет их правку с 409.
        "webProxy.enabled",
        "webProxy.port",
        "webProxy.basePath",
        "webProxy.certPath",
        "webProxy.certPassword",
        // OpenTelemetry: SDK и синки собираются один раз при старте процесса, на лету не применяются.
        "otel.enabled",
        "otel.serviceName",
        "otel.endpoint",
        "otel.protocol",
        "otel.exportTraces",
        "otel.exportLogs",
        "otel.exportMetrics",
    ];

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
    public static ConfigDto GetDefaults()
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

        if (patch.WebProxy is { } webProxyPatch && IsMasked(webProxyPatch.CertPassword))
        {
            webProxyPatch.CertPassword = null;
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

                case "mtproto.publicHost":
                    // Ссылка t.me/proxy собирается при каждом запросе /api/tokens/{id}/link,
                    // поэтому внешний адрес применяется без перезапуска.
                    config.MtProto.PublicHost = candidate.MtProto.PublicHost;
                    break;

                case "stats.enabled":
                    config.Stats.Enabled = candidate.Stats.Enabled;
                    break;

                case "stats.ttlSeconds":
                    config.Stats.TtlSeconds = candidate.Stats.TtlSeconds;
                    break;

                case "stats.maxPeers":
                    config.Stats.MaxPeers = candidate.Stats.MaxPeers;
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

                case "webProxy.publicHost":
                    // Домен попадает и в ссылку t.me/webproxy, и в relayBase bridge-страницы:
                    // обе собираются на каждый запрос, перезапуск не нужен.
                    config.WebProxy.PublicHost = candidate.WebProxy.PublicHost;
                    break;

                case "webProxy.carrierMode":
                    // Носитель читается при выдаче bridge-страницы и в ответе POST /session,
                    // поэтому смена действует на следующую сессию; открытые сессии сохраняют
                    // режим, с которым были созданы.
                    config.WebProxy.CarrierMode = candidate.WebProxy.CarrierMode;
                    break;

                case "webProxy.maxSessions":
                    config.WebProxy.MaxSessions = candidate.WebProxy.MaxSessions;
                    break;

                case "webProxy.maxSessionsPerIp":
                    config.WebProxy.MaxSessionsPerIp = candidate.WebProxy.MaxSessionsPerIp;
                    break;

                case "webProxy.maxStreamsPerSession":
                    config.WebProxy.MaxStreamsPerSession = candidate.WebProxy.MaxStreamsPerSession;
                    break;

                case "webProxy.maxStreams":
                    config.WebProxy.MaxStreams = candidate.WebProxy.MaxStreams;
                    break;

                case "webProxy.maxPendingBytesPerSession":
                    config.WebProxy.MaxPendingBytesPerSession = candidate.WebProxy.MaxPendingBytesPerSession;
                    break;

                case "webProxy.maxPendingItemsPerSession":
                    config.WebProxy.MaxPendingItemsPerSession = candidate.WebProxy.MaxPendingItemsPerSession;
                    break;
            }
        }
    }

    /// <summary>Некритичные замечания, которые страница показывает после сохранения.</summary>
    private static List<string> CollectWarnings(Config candidate)
    {
        var warnings = new List<string>();

        // Веб-сервер всегда слушает 0.0.0.0:80 (публично), поэтому без webApiToken API
        // достижим без аутентификации — предупреждаем об этом явно.
        if (string.IsNullOrWhiteSpace(candidate.WebApiToken))
        {
            warnings.Add(
                "webApiToken is empty: the Web API is reachable without authentication.");
        }

        if (!candidate.Stats.Enabled)
        {
            warnings.Add("stats.enabled = false: /api/per-ip will return an empty list.");
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
        if (dto.WebProxy is { } webProxy)
        {
            webProxy.CertPassword = MaskSecret(source.WebProxy.CertPassword);
        }

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
        if (JsonSerializer.SerializeToNode(before, DiffOptions) is not JsonObject left || JsonSerializer.SerializeToNode(after, DiffOptions) is not JsonObject right)
        {
            return [];
        }

        var changed = new List<string>();
        CollectChanges(left, right, "", changed);
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    /// <summary>
    /// Сравнивает вложенный JSON: объекты (<c>mtproto</c>, <c>stats</c>, <c>otel</c>, <c>webProxy</c>,
    /// <c>cfProxy</c>) диффятся по точкам (<c>webProxy.carrierMode</c>), чтобы смешанные блоки
    /// классифицировались на уровне подключа; массивы и скаляры сравниваются целиком как
    /// верхнеуровневые ключи.
    /// </summary>
    private static void CollectChanges(JsonObject left, JsonObject right, string prefix, List<string> changed)
    {
        foreach (var (key, value) in right)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";

            if (value is JsonObject childRight
                && left.TryGetPropertyValue(key, out var prev) && prev is JsonObject childLeft)
            {
                CollectChanges(childLeft, childRight, path, changed);
                continue;
            }

            if (!left.TryGetPropertyValue(key, out var previous) || !JsonNode.DeepEquals(previous, value))
            {
                changed.Add(path);
            }
        }
    }

    private static void ReplaceAll(List<string> target, List<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
