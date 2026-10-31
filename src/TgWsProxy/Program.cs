using System.Diagnostics;
using System.Reflection;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;
using TgWsProxy.Application;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Hosting;
using TgWsProxy.Infrastructure.Logging;
using TgWsProxy.Logging;
using TgWsProxy.Web;

// Версия сборки нужна глобальному энричеру Serilog (Version) и Swagger-документу.
// InformationalVersion хранит ровно то, что указано в <Version>, без суффикса «+hash».
var appVersion = typeof(Program).Assembly
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
    .Split('+')[0] ?? "unknown";

BootstrapOptions options;
try
{
    options = BootstrapOptions.Parse(args);
}
catch (ArgumentException e)
{
    // F-1.10: аргументы конфигурацию не задают, поэтому любая опция, кроме --config,
    // — ошибка, а не молчаливый старт на умолчаниях. Сообщение уходит в stderr: журнал ещё не поднят.
    Console.Error.WriteLine(e.Message);
    return 1;
}

// F-2.2: единственный источник параметров — умолчания и config.json.
// F-2.4: отсутствие файла при неявном пути — норма, прокси работает на умолчаниях.
Config cfg;
Dictionary<int, string> dcOpt;
string configPath;
bool configPathIsExplicit;
bool tokensBootstrapped;
try
{
    (configPath, configPathIsExplicit) = ConfigFileLoader.ResolvePath(options.ConfigPath);
    cfg = ConfigFileLoader.Load(configPath, configPathIsExplicit);

    // F-8.3: устаревший ключ secret уже перенесён в tokens при чтении; здесь дополняются
    // пропущенные поля и создаётся токен по умолчанию, если список пуст.
    tokensBootstrapped = TokenBootstrap.EnsureTokens(cfg);

    ConfigValidator.Validate(cfg);
    dcOpt = ConfigDefaults.ParseDcIpList(cfg.MtProto.DcIp);
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

// F-8.3: миграцию нужно закрепить на диске, иначе прежний ключ secret остался бы в файле
// рядом с новым списком токенов, и следующий запуск снова шёл бы по ветке миграции.
if (tokensBootstrapped && File.Exists(configPath))
{
    try
    {
        ConfigFileLoader.Save(configPath, cfg);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Cannot write migrated config {configPath}: {e.Message}");
        return 1;
    }
}

// Аргументы в WebApplication не передаются: их разбором владеет BootstrapOptions, а
// CommandLineConfigurationProvider падал бы на одиночных флагах вида "--config".
// Уровень журналирования задаётся только при старте: verbose относится к ключам
// «только старт», поэтому переключатель не меняется на лету.
var levelSwitch = new Serilog.Core.LoggingLevelSwitch(
    cfg.Verbose ? LogEventLevel.Debug : LogEventLevel.Information);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, lc) => ConfigureSerilog(lc, services, cfg, levelSwitch, appVersion));

// Ручной ActivityListener нужен только когда OTel-трассы выключены: иначе сам SDK
// (AddAspNetCoreInstrumentation) ставит слушателя на "Microsoft.AspNetCore" с сэмплированием.
if (!(cfg.Otel.Enabled && cfg.Otel.ExportTraces))
{
    InstallRequestActivityListener();
}

builder.Services
    .AddSingleton(cfg)
    .AddSingleton(dcOpt)
    .AddSingleton(new ConfigRuntime(configPath, configPathIsExplicit))
    .AddSingleton<ConfigUpdateService>()
    .AddProxyApplication()
    .AddProxyInfrastructure();

// F-3.1/F-3.4/F-3.10: реестр метрик Prometheus и health checks (liveness — self,
// readiness — слушатель, конфигурация, реестр статистики).
builder.Services.AddProxyObservability(cfg);

// OpenTelemetry (OTLP): трассы/метрики — SDK, логи — Serilog.Sinks.OpenTelemetry.
// При otel.enabled=false это no-op (см. AddProxyOpenTelemetry).
builder.Services.AddProxyOpenTelemetry(cfg, appVersion);

// F-1.11: OpenAPI. Документ строится по эндпоинтам и XML-комментариям сборки,
// ключ enableSwagger — только стартовый: маршруты регистрируются до сборки хоста.
if (cfg.EnableSwagger)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "tg-ws-proxy Web API",
            Version = appVersion,
            Description = "Управление прокси: метрики, статистика по (IP, токен), токены доступа и конфигурация.",
        });

        var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
        }

        options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            Description = "Токен панели (webApiToken). Обязателен для /api/*.",
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("bearer", document)] = [],
        });
    });
}

// Панель и bridge (WEB proxy) слушают на разных портах одного Kestrel-хоста. Bridge
// поднимается только при webProxy.enabled и требует PFX-сертификат (N-W.1, проверен валидатором).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Веб-сервер по умолчанию: все интерфейсы, порт 80.
    kestrel.ListenAnyIP(80);

    if (cfg.WebProxy.Enabled)
    {
        kestrel.ListenAnyIP(
            cfg.WebProxy.Port,
            listen => listen.UseHttps(cfg.WebProxy.CertPath, cfg.WebProxy.CertPassword));
    }
});

await using var app = builder.Build();

// Корреляция HTTP-запросов: TraceId/SpanId из Activity (Serilog.Enrichers.Span) +
// CorrelationId из X-Correlation-Id (CorrelationIdMiddleware). Шумные маршруты
// (healthz/metrics/swagger/WEB-proxy long-poll) опускаются ниже минимального уровня.
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set(LoggingConstants.RequestHost, httpContext.Request.Host.Value);
        diagnosticContext.Set(LoggingConstants.UserAgent, httpContext.Request.Headers.UserAgent.ToString());
        diagnosticContext.Set(LoggingConstants.TraceId, Activity.Current?.TraceId.ToString() ?? string.Empty);
        diagnosticContext.Set(LoggingConstants.SpanId, Activity.Current?.SpanId.ToString() ?? string.Empty);
        if (httpContext.Items.TryGetValue(LoggingConstants.CorrelationIdItemKey, out var correlationId))
        {
            diagnosticContext.Set(LoggingConstants.CorrelationIdProperty, correlationId);
        }
    };
    options.GetLevel = (httpContext, _, ex) =>
        IsRequestLoggingNoise(httpContext.Request)
            ? LogEventLevel.Verbose
            : ex is not null || httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information;
});

app.UseMiddleware<CorrelationIdMiddleware>();

// F-1.11: Swagger UI на /swagger и документ на /swagger/v1/swagger.json. Оба адреса
// открыты без токена (F-3.8): спецификация не содержит секретов (N-8).
if (cfg.EnableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "tg-ws-proxy Web API v1");
        options.DocumentTitle = "tg-ws-proxy Web API";
    });
}

// Bridge WEB proxy регистрируется до панели, чтобы перехватывать GET / на bridge-порту
// раньше UseDefaultFiles панели (разные порты — общий хост).
app.MapWebProxyBridge();
app.MapWebApiEndpoints();

using var runtimeLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var runtimeLogger = runtimeLoggerFactory.CreateLogger("runtime");

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception ex)
    {
        runtimeLogger.LogCritical(ex, "Unhandled exception");
    }
    else
    {
        runtimeLogger.LogCritical("Unhandled non-exception object");
    }
};

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    runtimeLogger.LogError(eventArgs.Exception, "Unobserved task exception");
    eventArgs.SetObserved();
};

var startupLogger = runtimeLoggerFactory.CreateLogger<ILogger<Program>>();

// F-1.1: прокси запускается как hosted-сервис; точка входа только собирает хост.
var proxyService = app.Services.GetRequiredService<ProxyHostedService>();

// Веб-сервер слушает все интерфейсы (0.0.0.0): всегда доступен извне, поэтому
// предупреждаем, если не задан webApiToken.
if (string.IsNullOrWhiteSpace(cfg.WebApiToken))
{
    startupLogger.LogWarning(
        "Web API is listening on all interfaces (port 80), and webApiToken is empty: " +
        "anyone who can reach this port can read metrics and change the configuration");
}
else
{
    startupLogger.LogInformation(
        "Web API is listening on all interfaces (port 80), and requires a bearer token for /api/*");
}

// «Слушает» логируется только после успешного старта хоста: ApplicationStarted
// срабатывает после того, как Kestrel привязал порт и hosted-сервисы поднялись (F-1.1).
app.Lifetime.ApplicationStarted.Register(() =>
{
    startupLogger.LogInformation("Web API listening");

    if (cfg.WebProxy.Enabled)
    {
        startupLogger.LogInformation(
            "WEB proxy bridge listening on port {Port} (basePath '{BasePath}')",
            cfg.WebProxy.Port,
            cfg.WebProxy.BasePath);
    }
});

try
{
    // app.RunAsync() — идиоматичный запуск сервиса: стартует Kestrel и hosted-сервисы,
    // ждёт SIGTERM/Ctrl+C (ConsoleLifetime) и штатно останавливает их в порядке F-1.3.
    // Авария цикла приёма соединений в ProxyHostedService вызывает StopApplication,
    // поэтому RunAsync возвращается, и ниже код возврата процесса становится 1.
    await app.RunAsync();
}
catch (Exception ex)
{
    // F-1.5: без веб-слоя управлять прокси нечем, поэтому отказ привязки фатален.
    // Отказ привязки самого прокси-листенера фатальным не является: его обрабатывает
    // ProxyHostedService, а readiness отдаёт 503.
    startupLogger.LogCritical(ex, "Failed to start web API: the web server is the only interface of this edition");
    return 1;
}

if (proxyService.Faulted)
{
    return 1;
}

return 0;

// Собирает конфигурацию Serilog: консоль со структурированной корреляцией и, при заданном
// logs.path, асинхронный файл с ограничением размера. Уровень задаётся переключателем.
static void ConfigureSerilog(
    LoggerConfiguration loggerConfiguration,
    IServiceProvider services,
    Config cfg,
    Serilog.Core.LoggingLevelSwitch levelSwitch,
    string appVersion)
{
    loggerConfiguration
        .MinimumLevel.ControlledBy(levelSwitch)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()
        .Enrich.WithProperty(LoggingConstants.Application, LoggingConstants.ApplicationName)
        .Enrich.WithProperty(LoggingConstants.Version, appVersion)
        .Enrich.WithSpan()
        .Destructure.With(new SecretMaskingPolicy())
        .WriteTo.Console(new CompactJsonFormatter());

    if (!string.IsNullOrWhiteSpace(cfg.Logs.Path))
    {
        if (cfg.Logs.MaxMegabytes > 0)
        {
            var limitBytes = (long)Math.Max(32 * 1024, cfg.Logs.MaxMegabytes * 1024 * 1024);
            loggerConfiguration.WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                cfg.Logs.Path,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: limitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: Math.Max(1, cfg.Logs.RetainedFileCount + 1)));
        }
        else
        {
            loggerConfiguration.WriteTo.Async(a => a.File(new CompactJsonFormatter(), cfg.Logs.Path, rollingInterval: RollingInterval.Hour));
        }
    }

    // Логи в OTLP-совместимый backend для корреляции с трассами (TraceId/SpanId). Синк собирается
    // один раз при старте; при otel.enabled=false или otel.exportLogs=false не подключается.
    if (cfg.Otel.Enabled && cfg.Otel.ExportLogs)
    {
        loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            if (!string.IsNullOrWhiteSpace(cfg.Otel.Endpoint))
            {
                options.Endpoint = cfg.Otel.Endpoint;
            }

            options.Protocol = cfg.Otel.Protocol.Trim() switch
            {
                "grpc" => OtlpProtocol.Grpc,
                "http" => OtlpProtocol.HttpProtobuf,
                _ => options.Protocol,
            };

            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = OpenTelemetryExtensions.ResolveServiceName(cfg),
                ["service.version"] = appVersion,
            };
        });
    }
}

// Маршруты, которые считаются шумом для request-logging: проверки живости/готовности,
// метрики, Swagger и высокочастотный WEB-proxy long-poll/ws.
static bool IsRequestLoggingNoise(HttpRequest request)
{
    var path = request.Path.Value ?? string.Empty;
    return path.StartsWith(LoggingConstants.HealthzPathPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(LoggingConstants.MetricsPathPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(LoggingConstants.SwaggerPathPrefix, StringComparison.OrdinalIgnoreCase)
        || path.Contains(LoggingConstants.ApiV1PathSegment, StringComparison.OrdinalIgnoreCase);
}

// Гарантирует создание W3C Activity на каждый HTTP-запрос (TraceId/SpanId) без подключения
// OpenTelemetry: ASP.NET Core использует ActivitySource "Microsoft.AspNetCore", и без слушателя
// Activity.Current остаётся пустым, а Serilog.Enrichers.Span не сможет проставить TraceId.
static void InstallRequestActivityListener()
{
    ActivitySource.AddActivityListener(new ActivityListener
    {
        ShouldListenTo = static source => source.Name == "Microsoft.AspNetCore",
        Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    });
}
