using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Metrics;

namespace TgWsProxy.Web;

/// <summary>
/// Служебные HTTP-эндпоинты прокси: <c>/healthz</c> (liveness для Docker HEALTHCHECK),
/// <c>/metrics</c> (Prometheus), <c>/api/*</c> (JSON для страницы инфографики) и
/// <c>/</c> — единственная HTML-страница без сборщика и CDN (F-5.1).
/// </summary>
internal static class WebApiEndpoints
{
    /// <summary>Момент старта процесса: база для поля <c>uptimeSeconds</c> в <c>/healthz</c>.</summary>
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>Календарное время старта процесса (UTC) для поля <c>startedAt</c>.</summary>
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    /// <summary>Версия сборки без суффикса «+hash», как в <c>--version</c>.</summary>
    private static readonly string Version = typeof(WebApiEndpoints).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+')[0] ?? "unknown";

    /// <summary>Content-Type для Prometheus text exposition format 0.0.4.</summary>
    private const string PrometheusContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Content-Type для <c>ProblemDetails</c>-ответов.</summary>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>Content-Type для обычных JSON-ответов с ручными заголовками.</summary>
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    /// Запрет кэширования ответов, содержащих секрет токена в открытом виде (F-8.8).
    /// </summary>
    private const string NoStoreHeader = "no-store";

    /// <summary>Значение по умолчанию для <c>limit</c> в <c>/api/per-ip</c> (F-4.7).</summary>
    private const int DefaultPerIpLimit = 50;

    /// <summary>Верхняя граница <c>limit</c> в <c>/api/per-ip</c> (F-4.7).</summary>
    private const int MaxPerIpLimit = 500;

    /// <summary>Единые настройки сериализации JSON для всех <c>/api/*</c> ответов.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Регистрирует конвейер и маршруты служебных эндпоинтов в приложении.
    /// </summary>
    /// <param name="app">Построенное веб-приложение.</param>
    public static void Map(WebApplication app)
    {
        // F-1.6: необработанное исключение в обработчике превращается в 500 + ProblemDetails,
        // а не в обрыв соединения. Текст исключения наружу не отдаём: в нём могут быть пути
        // и параметры конфигурации.
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = ProblemContentType;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal server error",
                    Detail = "The request failed unexpectedly. See the server log for details.",
                    Instance = context.Request.Path,
                },
                JsonOptions);
        }));

        // F-3.8: если webApiToken задан, все /api/* требуют Authorization: Bearer.
        // /healthz, /metrics и страница остаются открытыми — их читают Docker и Prometheus.
        app.Use(async (context, next) =>
        {
            var config = context.RequestServices.GetRequiredService<Config>();
            var expected = config.WebApiToken;

            if (!string.IsNullOrWhiteSpace(expected) && context.Request.Path.StartsWithSegments("/api"))
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (!IsAuthorized(header, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    context.Response.ContentType = ProblemContentType;
                    await context.Response.WriteAsJsonAsync(
                        new ProblemDetails
                        {
                            Status = StatusCodes.Status401Unauthorized,
                            Title = "Unauthorized",
                            Detail = "A valid bearer token is required: send 'Authorization: Bearer <token>'.",
                            Instance = context.Request.Path,
                        },
                        JsonOptions);
                    return;
                }
            }

            await next();
        });

        // F-5.1: статическая страница из wwwroot без сборщика. UseDefaultFiles отдаёт index.html
        // на GET /, UseStaticFiles — site.css и app.js.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // F-3.9: liveness. 200 только когда слушатель принимает соединения; во время
        // перезапуска и при мёртвом слушателе — 503 (Docker HEALTHCHECK смотрит на код).
        app.MapGet("/healthz", (IProxyServer server, IListenerController listener) =>
        {
            var state = listener.State;
            var status = state switch
            {
                ListenerState.Listening => "ok",
                ListenerState.Restarting => "restarting",
                _ => "unavailable",
            };

            var payload = new
            {
                status,
                listening = server.IsListening,
                uptimeSeconds = (long)Uptime.Elapsed.TotalSeconds,
                version = Version,
            };

            var statusCode = state == ListenerState.Listening
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;

            return Results.Json(payload, statusCode: statusCode);
        });

        // F-3.4/F-3.9: readiness. Проверки помечены тегом ready; тело формируется
        // штатным UIResponseWriter из AspNetCore.HealthChecks.UI.Client, Degraded
        // трактуется как «не готов» (503), чтобы балансировщик снял инстанс с ротации.
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = static check => check.Tags.Contains("ready"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        // F-3.6: версия, uptime, состояние подсистем и действующие лимиты.
        // F-7.8: состояние слушателя, число перезапусков и время последнего перезапуска.
        app.MapGet("/api/info", (IProxyServer server, Config cfg, IListenerController listener) =>
        {
            var info = new InfoDto(
                Version,
                (long)Uptime.Elapsed.TotalSeconds,
                StartedAt,
                server.IsListening,
                cfg.H2Enabled,
                cfg.CfProxyEnabled,
                !string.IsNullOrWhiteSpace(cfg.FakeTlsDomain),
                cfg.ProxyProtocol,
                cfg.MetricsPort,
                cfg.MetricsHost,
                cfg.MaxConnections,
                cfg.RateLimitPerIp,
                cfg.RateLimitWindowSeconds,
                cfg.DcIp.Count,
                cfg.Host,
                cfg.Port,
                listener.State.ToString().ToLowerInvariant(),
                listener.RestartCount,
                listener.LastRestartAt,
                cfg.Tokens.Count,
                cfg.Tokens.Count(static token => token.Enabled));

            return Results.Json(info, JsonOptions);
        })
        // F-1.12: явная схема ответа — из IResult Swashbuckle тип не выводит.
        .Produces<InfoDto>(StatusCodes.Status200OK);

        // F-3.2: снимок счётчиков в JSON — источник данных для страницы инфографики.
        app.MapGet("/api/metrics", (IProxyStats stats, IPeerStatsRegistry peerStats, Config cfg) =>
        {
            var dto = MetricsDto.From(
                stats.Snapshot(),
                Version,
                (long)Uptime.Elapsed.TotalSeconds,
                cfg.H2Enabled,
                peerStats.TrackedPeers,
                peerStats.OverflowCount);

            return Results.Json(dto, JsonOptions);
        })
        .Produces<MetricsDto>(StatusCodes.Status200OK);

        // F-3.3: временной ряд из кольцевого буфера. Окно и шаг ограничены разумными
        // пределами, чтобы запрос не мог потребовать неограниченный объём работы.
        app.MapGet("/api/metrics/timeseries", (MetricsHistory history, Config cfg, int? window, int? step) =>
        {
            var windowSeconds = Math.Clamp(window ?? cfg.TimeseriesWindowSeconds, 60, 3600);
            var stepSeconds = Math.Clamp(step ?? cfg.TimeseriesStepSeconds, 1, 300);
            var dto = history.Query(
                TimeSpan.FromSeconds(windowSeconds),
                TimeSpan.FromSeconds(stepSeconds),
                DateTimeOffset.UtcNow);

            return Results.Json(dto, JsonOptions);
        })
        .Produces<TimeseriesDto>(StatusCodes.Status200OK);

        // F-3.5 / F-4.7 / F-9.4: страница списка пар «IP, токен», отсортированного по трафику.
        // offset — смещение, limit — размер страницы (50 по умолчанию, 500 максимум),
        // q — префиксный фильтр по адресу, group — разрез (pair по умолчанию, ip, token),
        // token — фильтр по идентификатору токена.
        app.MapGet("/api/per-ip", (IPeerStatsRegistry peerStats, PeerStatsSnapshotCache snapshotCache, int? offset, int? limit, string? q, string? group, string? token) =>
        {
            var pageOffset = Math.Max(0, offset ?? 0);
            var pageLimit = Math.Clamp(limit ?? DefaultPerIpLimit, 1, MaxPerIpLimit);

            IEnumerable<PeerStatsSnapshot> filtered = snapshotCache.Get();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                filtered = filtered.Where(peer => peer.Ip.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                var tokenId = token.Trim();
                filtered = filtered.Where(peer => string.Equals(peer.TokenId, tokenId, StringComparison.OrdinalIgnoreCase));
            }

            var rows = PeerStatsAggregator.Aggregate(filtered, group);

            var ordered = rows
                .OrderByDescending(peer => peer.TotalBytes)
                .ThenByDescending(peer => peer.TotalConnections)
                .ThenBy(peer => peer.Ip, StringComparer.Ordinal)
                .ThenBy(peer => peer.TokenId, StringComparer.Ordinal)
                .ToList();

            var dto = new PerIpPageDto(
                pageOffset,
                pageLimit,
                ordered.Count,
                peerStats.TrackedPeers,
                peerStats.OverflowCount,
                [.. ordered.Skip(pageOffset).Take(pageLimit).Select(PeerStatsDto.From)]);

            return Results.Json(dto, JsonOptions);
        })
        .Produces<PerIpPageDto>(StatusCodes.Status200OK);

        // F-9.5: агрегаты по токену целиком, без разреза по адресам — для блока «Токены доступа».
        app.MapGet("/api/metrics/by-token", (PeerStatsSnapshotCache snapshotCache, IProxyTokenRegistry tokens) =>
        {
            var aggregated = PeerStatsAggregator
                .Aggregate(snapshotCache.Get(), "token")
                .ToDictionary(peer => peer.TokenId ?? "", StringComparer.Ordinal);

            var items = tokens.All
                .Select(token =>
                {
                    aggregated.TryGetValue(token.Id, out var usage);
                    return new TokenUsageDto(
                        token.Id,
                        token.Name,
                        usage?.ActiveConnections ?? 0,
                        usage?.TotalConnections ?? 0,
                        usage?.BytesUp ?? 0,
                        usage?.BytesDown ?? 0,
                        usage?.WsErrors ?? 0,
                        usage?.Rejected ?? 0,
                        usage?.PoolHits ?? 0,
                        usage?.PoolMisses ?? 0);
                })
                .OrderByDescending(item => item.BytesUp + item.BytesDown)
                .ThenBy(item => item.TokenName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Json(items, JsonOptions);
        })
        .Produces<List<TokenUsageDto>>(StatusCodes.Status200OK);

        // F-2.10: конфигурация для формы. Секрет маскирован, открытых значений нет.
        app.MapGet("/api/config", (ConfigUpdateService configService) =>
                Results.Json(configService.GetMasked(), JsonOptions))
            .Produces<ConfigDto>(StatusCodes.Status200OK);

        // F-2.9 / F-2.11 / F-2.12: сохранение изменений. Три отказа различаются кодом и телом:
        // 400 — не проходит валидацию, 409 — ключи «только старт», 409 + Retry-After — идёт
        // перезапуск слушателя, 409 + rolledBack — перепривязка не удалась и файл откачен.
        app.MapPut("/api/config", async (ConfigDto patch, ConfigUpdateService configService, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await configService.ApplyAsync(patch, cancellationToken);
                return Results.Json(result, JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Invalid configuration",
                    ex.Message);
            }
            catch (StartupOnlyKeysException ex)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Configuration keys require a process restart",
                    "These keys are read once at startup and cannot be changed at runtime.",
                    new Dictionary<string, object?> { ["startupOnly"] = ex.StartupOnly },
                    "https://tools.ietf.org/html/rfc9110#section-15.5.10");
            }
            catch (ListenerRebindException ex)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Listener rebind failed",
                    $"{ex.Message} Previous binding restored.",
                    new Dictionary<string, object?> { ["rolledBack"] = true },
                    "https://tools.ietf.org/html/rfc9110#section-15.5.10");
            }
            catch (ListenerRestartInProgressException)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Listener restart in progress",
                    "Another listener restart is already running. Retry the request.",
                    retryAfterSeconds: "1");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problem(
                    StatusCodes.Status500InternalServerError,
                    "Configuration could not be written",
                    ex.Message);
            }
        })
        .Produces<ConfigUpdateResult>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError, ProblemContentType);

        // F-5.10: сброс к умолчаниям без записи — пользователь подтверждает его через PUT.
        app.MapPost("/api/config/reset", (ConfigUpdateService configService) =>
            Results.Json(configService.GetDefaults(), JsonOptions));

        // F-8.8: список токенов доступа прокси. Секреты всегда маскированы (N-8);
        // открытое значение выдаётся только при создании и ротации.
        app.MapGet("/api/tokens", (Config cfg, IActiveSessionRegistry sessions) =>
        {
            var tokens = cfg.Tokens
                .Select(token => TokenDto.From(token, activeSessions: sessions.CountByToken(token.Id)))
                .ToArray();

            return Results.Json(tokens, JsonOptions);
        })
        .Produces<TokenDto[]>(StatusCodes.Status200OK);

        // F-8.8: создание токена. Секрет можно не передавать — тогда сервер генерирует
        // 32 hex-символа, что исключает слабые значения из панели. Ответ содержит секрет
        // один раз и запрещён к кэшированию.
        app.MapPost("/api/tokens", async (
            CreateTokenRequest? body,
            Config cfg,
            ConfigUpdateService configService,
            CancellationToken cancellationToken) =>
        {
            var name = body?.Name?.Trim() ?? "";
            if (name.Length is < 1 or > ConfigValidator.MaxTokenNameLength)
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "Invalid token",
                    $"Token name must be 1..{ConfigValidator.MaxTokenNameLength} characters.");
            }

            if (cfg.Tokens.Any(t => string.Equals(t.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Token name already exists",
                    $"A token named '{name}' already exists.",
                    new Dictionary<string, object?> { ["name"] = name });
            }

            if (cfg.Tokens.Count >= ConfigValidator.MaxTokens)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "Too many tokens",
                    $"At most {ConfigValidator.MaxTokens} tokens are supported.");
            }

            var secret = string.IsNullOrWhiteSpace(body?.Secret)
                ? TokenBootstrap.GenerateSecret()
                : body!.Secret!.Trim();

            var token = new ProxyToken
            {
                Id = TokenBootstrap.NewId(),
                Name = name,
                Secret = secret,
                Enabled = body?.Enabled ?? true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
            list.Add(token);

            try
            {
                await configService
                    .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid token", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problem(
                    StatusCodes.Status500InternalServerError,
                    "Configuration could not be written",
                    ex.Message);
            }

            return new JsonResult<TokenDto>(
                TokenDto.From(token, revealSecret: true),
                StatusCodes.Status201Created,
                JsonContentType,
                [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
        })
        .Produces<TokenDto>(StatusCodes.Status201Created, JsonContentType)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType);

        // F-8.8 / F-8.9: изменение имени, секрета или признака enabled. Маскированный секрет
        // означает «не менять» (его подставляет ConfigUpdateService по идентификатору токена).
        app.MapPut("/api/tokens/{id}", async (
            string id,
            UpdateTokenRequest? body,
            Config cfg,
            ConfigUpdateService configService,
            IActiveSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            var index = cfg.Tokens.FindIndex(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return Problem(
                    StatusCodes.Status404NotFound,
                    "Token not found",
                    $"No token with id '{id}'.");
            }

            var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
            var target = list[index];

            if (body?.Name is { } name)
            {
                target.Name = name.Trim();
            }

            if (body?.Secret is { } secret)
            {
                // Маска остаётся маской: ApplyAsync восстановит настоящий секрет по id.
                target.Secret = secret.Trim();
            }

            if (body?.Enabled is { } enabled)
            {
                target.Enabled = enabled;
            }

            try
            {
                await configService
                    .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid token", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problem(
                    StatusCodes.Status500InternalServerError,
                    "Configuration could not be written",
                    ex.Message);
            }

            // F-8.6: отключение токена разрывает только его сессии; остальные не затрагиваются.
            var closed = target.Enabled ? 0 : sessions.CloseByToken(target.Id);

            return Results.Json(
                new { token = TokenDto.From(target), closedSessions = closed },
                JsonOptions);
        });

        // F-8.8: удаление токена. Валидатор не даст удалить последний включённый токен,
        // поэтому запрос вернёт 400 с объяснением (F-8.2).
        app.MapDelete("/api/tokens/{id}", async (
            string id,
            Config cfg,
            ConfigUpdateService configService,
            IActiveSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            var list = cfg.Tokens
                .Where(t => !string.Equals(t.Id, id, StringComparison.Ordinal))
                .Select(static existing => existing.Clone())
                .ToList();

            if (list.Count == cfg.Tokens.Count)
            {
                return Problem(
                    StatusCodes.Status404NotFound,
                    "Token not found",
                    $"No token with id '{id}'.");
            }

            try
            {
                await configService
                    .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid token", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problem(
                    StatusCodes.Status500InternalServerError,
                    "Configuration could not be written",
                    ex.Message);
            }

            var closed = sessions.CloseByToken(id);

            return Results.Json(new { deleted = true, closedSessions = closed }, JsonOptions);
        });

        // F-8.8: ротация секрета. Новое значение возвращается один раз и не кэшируется;
        // уже установленные сессии продолжают работать (F-8.6 касается только отзыва).
        app.MapPost("/api/tokens/{id}/rotate", async (
            string id,
            Config cfg,
            ConfigUpdateService configService,
            CancellationToken cancellationToken) =>
        {
            var index = cfg.Tokens.FindIndex(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return Problem(
                    StatusCodes.Status404NotFound,
                    "Token not found",
                    $"No token with id '{id}'.");
            }

            var list = cfg.Tokens.Select(static existing => existing.Clone()).ToList();
            var target = list[index];
            target.Secret = TokenBootstrap.GenerateSecret();
            target.LastUsedAt = null;

            try
            {
                await configService
                    .ApplyAsync(new ConfigDto { Tokens = list }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid token", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problem(
                    StatusCodes.Status500InternalServerError,
                    "Configuration could not be written",
                    ex.Message);
            }

            return new JsonResult<TokenDto>(
                TokenDto.From(target, revealSecret: true),
                StatusCodes.Status200OK,
                JsonContentType,
                [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
        });

        // F-8.8 (исключение): полная ссылка для Telegram и полный секрет токена.
        // Ссылку панель копирует в буфер обмена, секрет показывает при редактировании
        // токена. Ответ запрещён к кэшированию, как и ответы создания/ротации.
        app.MapGet("/api/tokens/{id}/link", (string id, Config cfg) =>
        {
            var token = cfg.Tokens
                .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (token is null)
            {
                return Problem(
                    StatusCodes.Status404NotFound,
                    "Token not found",
                    $"No token with id '{id}'.");
            }

            return new JsonResult<TokenLinkDto>(
                new TokenLinkDto(token.Id, token.Name, BuildTelegramLink(cfg, token), token.Secret),
                StatusCodes.Status200OK,
                JsonContentType,
                [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
        })
        .Produces<TokenLinkDto>(StatusCodes.Status200OK, JsonContentType)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType);

        // F-3.1: экспозиция Prometheus через prometheus-net. Значения снимаются со
        // счётчиков в before-collect колбэке реестра, поэтому формат и имена метрик
        // целиком принадлежат библиотеке (счётчики получают суффикс _total).
        app.MapGet("/metrics", async (ProxyMetrics metrics, HttpContext context) =>
        {
            context.Response.ContentType = ProxyMetrics.ContentType;
            await metrics.CollectAsync(context.Response.Body, context.RequestAborted);
        });
    }

    /// <summary>
    /// Проверяет заголовок <c>Authorization</c> на соответствие схеме Bearer.
    /// Сравнение — за постоянное время, чтобы токен нельзя было подобрать по задержке.
    /// </summary>
    /// <param name="header">Значение заголовка целиком.</param>
    /// <param name="expected">Ожидаемый токен из конфигурации.</param>
    internal static bool IsAuthorized(string? header, string expected)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header.AsSpan(scheme.Length).Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided.ToString());

        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    /// <summary>
    /// Собирает базовый адрес Kestrel для заданных хоста и порта.
    /// Поддерживает wildcard-хосты (<c>*</c>, <c>0.0.0.0</c>, <c>::</c>) и IPv6-литералы.
    /// </summary>
    /// <param name="host">Хост привязки из <c>MetricsHost</c>.</param>
    /// <param name="port">Порт привязки из <c>MetricsPort</c>.</param>
    /// <returns>URL вида <c>http://host:port</c>.</returns>
    public static string BuildListenUrl(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*" || host == "+" || host == "0.0.0.0")
        {
            return string.Create(CultureInfo.InvariantCulture, $"http://*:{port}");
        }

        if (host is "::" or "[::]")
        {
            return string.Create(CultureInfo.InvariantCulture, $"http://[::]:{port}");
        }

        // IPv6-литерал без скобок Kestrel не примет в URI — оборачиваем.
        var formattedHost = host.Contains(':', StringComparison.Ordinal) && !host.Contains('[', StringComparison.Ordinal)
            ? $"[{host}]"
            : host;

        return string.Create(CultureInfo.InvariantCulture, $"http://{formattedHost}:{port}");
    }

    /// <summary>
    /// Собирает ссылку <c>https://t.me/proxy</c> для подключения клиента Telegram (F-8.8).
    /// Секрет передаётся в формате fake-TLS (<c>dd</c> + 32 hex), который ожидает клиент.
    /// Адрес берётся из <c>publicHost</c> — внешней точки доступа, — иначе из <c>host</c>.
    /// Для служебных адресов (<c>0.0.0.0</c>, <c>::</c>) без явного <c>publicHost</c> ссылка
    /// не строится: снаружи такой адрес недостижим, и панель предлагает задать внешний адрес.
    /// </summary>
    /// <param name="cfg">Живая конфигурация: источник <c>publicHost</c>/<c>host</c> и <c>port</c>.</param>
    /// <param name="token">Токен, для которого строится ссылка.</param>
    /// <returns>Готовая ссылка или <see langword="null"/>, если внешний адрес не определён.</returns>
    private static string? BuildTelegramLink(Config cfg, ProxyToken token)
    {
        if (cfg.ResolvePublicEndpoint() is not { } endpoint)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"https://t.me/proxy?server={endpoint.Host}&port={endpoint.Port}&secret=dd{token.Secret}");
    }

    /// <summary>
    /// Собирает ответ <c>application/problem+json</c> по контракту §5. Дополнительные поля
    /// (<c>startupOnly</c>, <c>rolledBack</c>) добавляются в <see cref="ProblemDetails.Extensions"/>
    /// и сериализуются на верхнем уровне.
    /// </summary>
    /// <param name="statusCode">Код ответа.</param>
    /// <param name="title">Краткое название проблемы.</param>
    /// <param name="detail">Подробность для пользователя.</param>
    /// <param name="extensions">Дополнительные поля тела.</param>
    /// <param name="type">URI типа проблемы (RFC 9110) или <see langword="null"/>.</param>
    /// <param name="retryAfterSeconds">Значение заголовка <c>Retry-After</c> для 409 (F-7.6).</param>
    private static IResult Problem(
        int statusCode,
        string title,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null,
        string? type = null,
        string? retryAfterSeconds = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type,
        };

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return retryAfterSeconds is null
            ? Results.Json(problem, JsonOptions, contentType: ProblemContentType, statusCode: statusCode)
            : new JsonResult<ProblemDetails>(
                problem,
                statusCode,
                ProblemContentType,
                [new KeyValuePair<string, string>("Retry-After", retryAfterSeconds)]);
    }

    /// <summary>
    /// JSON-ответ с дополнительными заголовками. Нужен там, где <c>Results.Json</c> не
    /// позволяет выставить заголовок ответа (например, <c>Retry-After</c> при 409, F-7.6).
    /// </summary>
    private sealed class JsonResult<T>(
        T value,
        int statusCode,
        string contentType,
        IReadOnlyList<KeyValuePair<string, string>> headers) : IResult
    {
        /// <inheritdoc />
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            foreach (var (name, headerValue) in headers)
            {
                httpContext.Response.Headers[name] = headerValue;
            }

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            return httpContext.Response.WriteAsJsonAsync(value, JsonOptions);
        }
    }
}
