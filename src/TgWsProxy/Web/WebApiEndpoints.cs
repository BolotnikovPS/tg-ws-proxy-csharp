using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Cqrs;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.ReadModels;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using Wolverine;

namespace TgWsProxy.Web;

/// <summary>
/// Служебные HTTP-эндпоинты прокси: <c>/healthz</c> (liveness для Docker HEALTHCHECK),
/// <c>/metrics</c> (Prometheus), <c>/api/*</c> (JSON для страницы инфографики) и
/// <c>/</c> — единственная HTML-страница без сборщика и CDN (F-5.1).
///
/// Слой только описывает контракт: валидация, обращение к конфигурации, агрегация статистики
/// и смена состояния выполняются обработчиками CQRS в Application, а сюда приходят через
/// <see cref="IMessageBus"/>. Логика перевода исключений в коды ответов (ProblemDetails) остаётся здесь.
/// </summary>
internal static class WebApiEndpoints
{
    /// <summary>Content-Type для <c>ProblemDetails</c>-ответов.</summary>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>Content-Type для обычных JSON-ответов с ручными заголовками.</summary>
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    /// Запрет кэширования ответов, содержащих секрет токена в открытом виде (F-8.8).
    /// </summary>
    private const string NoStoreHeader = "no-store";

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
    public static void MapWebApiEndpoints(this WebApplication app)
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
        // на GET /, UseStaticFiles — site.css и app.js. no-cache заставляет браузер перепроверять
        // файлы по ETag, чтобы после обновления панель не показывала устаревшие app.js/site.css.
        var staticFileOptions = new StaticFileOptions
        {
            OnPrepareResponse = staticContext =>
                staticContext.Context.Response.Headers.CacheControl = "no-cache",
        };
        app.UseDefaultFiles();
        app.UseStaticFiles(staticFileOptions);

        // F-3.9: liveness. 200 только когда слушатель принимает соединения; во время
        // перезапуска и при мёртвом слушателе — 503 (Docker HEALTHCHECK смотрит на код).
        app.MapGet("/healthz", (IProxyServer server, IListenerController listener, AppRuntimeInfo runtime) =>
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
                uptimeSeconds = runtime.UptimeSeconds,
                version = runtime.Version,
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
        app.MapGet("/api/info", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<InfoDto>(new GetInfoQuery(), cancellationToken),
                    JsonOptions))
            // F-1.12: явная схема ответа — из IResult Swashbuckle тип не выводит.
            .Produces<InfoDto>(StatusCodes.Status200OK);

        // F-3.2: снимок счётчиков в JSON — источник данных для страницы инфографики.
        app.MapGet("/api/metrics", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<MetricsDto>(new GetMetricsQuery(), cancellationToken),
                    JsonOptions))
            .Produces<MetricsDto>(StatusCodes.Status200OK);

        // F-3.3: временной ряд из кольцевого буфера. Окно и шаг ограничены разумными
        // пределами, чтобы запрос не мог потребовать неограниченный объём работы.
        app.MapGet(
                "/api/metrics/timeseries",
                async (IMessageBus bus, CancellationToken cancellationToken, int? window, int? step) =>
                    Results.Json(
                        await bus.InvokeAsync<TimeseriesDto>(
                            new GetTimeseriesQuery(window, step),
                            cancellationToken),
                        JsonOptions))
            .Produces<TimeseriesDto>(StatusCodes.Status200OK);

        // F-3.5 / F-4.7 / F-9.4: страница списка пар «IP, токен», отсортированного по трафику.
        // offset — смещение, limit — размер страницы (50 по умолчанию, 500 максимум),
        // q — префиксный фильтр по адресу, group — разрез (pair по умолчанию, ip, token),
        // token — фильтр по идентификатору токена.
        app.MapGet(
                "/api/per-ip",
                async (
                    IMessageBus bus,
                    CancellationToken cancellationToken,
                    int? offset,
                    int? limit,
                    string? q,
                    string? group,
                    string? token) =>
                    Results.Json(
                        await bus.InvokeAsync<PerIpPageDto>(
                            new GetPerIpQuery(offset, limit, q, group, token),
                            cancellationToken),
                        JsonOptions))
            .Produces<PerIpPageDto>(StatusCodes.Status200OK);

        // F-9.5: агрегаты по токену целиком, без разреза по адресам — для блока «Токены доступа».
        app.MapGet("/api/metrics/by-token", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<IReadOnlyList<TokenUsageDto>>(new GetTokenUsageQuery(), cancellationToken),
                    JsonOptions))
            .Produces<IReadOnlyList<TokenUsageDto>>(StatusCodes.Status200OK);

        // F-2.10: конфигурация для формы. Секрет маскирован, открытых значений нет.
        app.MapGet("/api/config", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<ConfigDto>(new GetConfigQuery(), cancellationToken),
                    JsonOptions))
            .Produces<ConfigDto>(StatusCodes.Status200OK);

        // F-2.9 / F-2.11 / F-2.12: сохранение изменений. Три отказа различаются кодом и телом:
        // 400 — не проходит валидацию, 409 — ключи «только старт», 409 + Retry-After — идёт
        // перезапуск слушателя, 409 + rolledBack — перепривязка не удалась и файл откачен.
        app.MapPut("/api/config", async (ConfigDto patch, IMessageBus bus, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await bus.InvokeAsync<ConfigUpdateResult>(
                    new UpdateConfigCommand(patch),
                    cancellationToken);
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
        app.MapPost("/api/config/reset", async (IMessageBus bus, CancellationToken cancellationToken) =>
            Results.Json(
                await bus.InvokeAsync<ConfigDto>(new GetDefaultConfigQuery(), cancellationToken),
                JsonOptions));

        // F-8.8: список токенов доступа прокси. Секреты всегда маскированы (N-8);
        // открытое значение выдаётся только при создании и ротации.
        app.MapGet("/api/tokens", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<TokenDto[]>(new GetTokensQuery(), cancellationToken),
                    JsonOptions))
            .Produces<TokenDto[]>(StatusCodes.Status200OK);

        // F-8.8: создание токена. Секрет можно не передавать — тогда сервер генерирует
        // 32 hex-символа, что исключает слабые значения из панели. Ответ содержит секрет
        // один раз и запрещён к кэшированию.
        app.MapPost("/api/tokens", async (
            CreateTokenCommand? body,
            IMessageBus bus,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await bus.InvokeAsync<CreateTokenResult>(
                    body ?? new CreateTokenCommand(null, null, null),
                    cancellationToken);

                return new JsonResult<TokenDto>(
                    result.Token,
                    StatusCodes.Status201Created,
                    JsonContentType,
                    [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
            }
            catch (Exception ex) when (TokenFailure(ex) is { } problem)
            {
                return problem;
            }
        })
        .Produces<TokenDto>(StatusCodes.Status201Created, JsonContentType)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType);

        // F-8.8 / F-8.9: изменение имени, секрета или признака enabled. Маскированный секрет
        // означает «не менять» — его подставляет ConfigUpdateService по идентификатору токена.
        app.MapPut("/api/tokens/{id}", async (
            string id,
            UpdateTokenRequest? body,
            IMessageBus bus,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await bus.InvokeAsync<UpdateTokenResult>(
                    new UpdateTokenCommand(id, body?.Name, body?.Secret, body?.Enabled),
                    cancellationToken);

                return Results.Json(
                    new { token = result.Token, closedSessions = result.ClosedSessions },
                    JsonOptions);
            }
            catch (Exception ex) when (TokenFailure(ex) is { } problem)
            {
                return problem;
            }
        })
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType);

        // F-8.8: удаление токена. Валидатор не даст удалить последний включённый токен,
        // поэтому запрос вернёт 400 с объяснением (F-8.2).
        app.MapDelete("/api/tokens/{id}", async (
            string id,
            IMessageBus bus,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await bus.InvokeAsync<DeleteTokenResult>(
                    new DeleteTokenCommand(id),
                    cancellationToken);

                return Results.Json(
                    new { deleted = true, closedSessions = result.ClosedSessions },
                    JsonOptions);
            }
            catch (Exception ex) when (TokenFailure(ex) is { } problem)
            {
                return problem;
            }
        })
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType);

        // F-8.8: ротация секрета. Новое значение возвращается один раз и не кэшируется;
        // уже установленные сессии продолжают работать (F-8.6 касается только отзыва).
        app.MapPost("/api/tokens/{id}/rotate", async (
            string id,
            IMessageBus bus,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await bus.InvokeAsync<RotateTokenResult>(
                    new RotateTokenCommand(id),
                    cancellationToken);

                return new JsonResult<TokenDto>(
                    result.Token,
                    StatusCodes.Status200OK,
                    JsonContentType,
                    [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
            }
            catch (Exception ex) when (TokenFailure(ex) is { } problem)
            {
                return problem;
            }
        })
        .Produces<TokenDto>(StatusCodes.Status200OK, JsonContentType)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType);

        // F-8.8 (исключение): полная ссылка для Telegram и полный секрет токена.
        // Ссылку панель копирует в буфер обмена, секрет показывает при редактировании
        // токена. Ответ запрещён к кэшированию, как и ответы создания/ротации.
        app.MapGet("/api/tokens/{id}/link", async (
            string id,
            IMessageBus bus,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var link = await bus.InvokeAsync<TokenLinkDto>(
                    new GetTokenLinkQuery(id),
                    cancellationToken);

                return new JsonResult<TokenLinkDto>(
                    link,
                    StatusCodes.Status200OK,
                    JsonContentType,
                    [new KeyValuePair<string, string>("Cache-Control", NoStoreHeader)]);
            }
            catch (Exception ex) when (TokenFailure(ex) is { } problem)
            {
                return problem;
            }
        })
        .Produces<TokenLinkDto>(StatusCodes.Status200OK, JsonContentType)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType);

        // F-9.x: проверка доступности дата-центров прямо с хоста прокси. Проверяются
        // четыре маршрута до каждого DC (прямой WebSocket, прямой TCP, CF-фронтинг,
        // CF Worker), чтобы понять, какой путь реально открыт из сети развёртывания.
        // Результаты — это видимость сети именно с сервера, а не с машины клиента.
        app.MapPost("/api/dc-check", async (IMessageBus bus, CancellationToken cancellationToken) =>
                Results.Json(
                    await bus.InvokeAsync<DcCheckReport>(new RunDcCheckQuery(), cancellationToken),
                    JsonOptions))
            .Produces<DcCheckReport>(StatusCodes.Status200OK);

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
    /// <param name="host">Хост привязки.</param>
    /// <param name="port">Порт привязки.</param>
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
    /// Переводит исключения обработчиков токенов в HTTP-ответ. Возвращает <see langword="null"/>
    /// для незнакомых исключений — тогда срабатывает общий обработчик 500, и текст наружу не уходит.
    /// </summary>
    /// <param name="exception">Исключение, пришедшее из обработчика.</param>
    /// <returns>Готовый ответ либо <see langword="null"/>, если исключение не про токены.</returns>
    private static IResult? TokenFailure(Exception exception) => exception switch
    {
        TokenNotFoundException notFound => Problem(
            StatusCodes.Status404NotFound,
            "Token not found",
            notFound.Message),
        TokenConflictException conflict => Problem(
            StatusCodes.Status409Conflict,
            "Token conflict",
            conflict.Message,
            conflict.ConflictName is { } name
                ? new Dictionary<string, object?> { ["name"] = name }
                : null),
        ArgumentException invalid => Problem(
            StatusCodes.Status400BadRequest,
            "Invalid token",
            invalid.Message),
        IOException or UnauthorizedAccessException => Problem(
            StatusCodes.Status500InternalServerError,
            "Configuration could not be written",
            exception.Message),
        _ => null,
    };

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
