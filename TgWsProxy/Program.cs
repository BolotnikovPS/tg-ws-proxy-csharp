using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Hosting;
using TgWsProxy.Web;

// F-1.7: справка и версия обрабатываются до загрузки конфигурации — иначе --help падал бы
// на отсутствующем config.json. InformationalVersion хранит ровно то, что указано в <Version>.
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
    // F-1.10: аргументы конфигурацию не задают, поэтому неизвестный флаг — ошибка, а не
    // молчаливый старт на умолчаниях. Сообщение уходит в stderr: журнал ещё не поднят.
    Console.Error.WriteLine(e.Message);
    return 1;
}

if (options.ShowHelp)
{
    Console.WriteLine(BootstrapOptions.HelpText);
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine($"tg-ws-proxy {appVersion}");
    return 0;
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
    dcOpt = ConfigDefaults.ParseDcIpList(cfg.DcIp);
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

builder.Logging.ClearProviders().AddDebug().AddSerilog(BuildSerilogLogger(cfg, levelSwitch));

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

// F-1.4: веб-сервер запускается всегда — это единственный интерфейс управления.
// ConfigValidator уже гарантировал, что metricsPort лежит в 1..65535.
var listenUrl = WebApiEndpoints.BuildListenUrl(cfg.MetricsHost, cfg.MetricsPort);
builder.WebHost.UseUrls(listenUrl);

await using var app = builder.Build();

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

WebApiEndpoints.Map(app);

var runtimeLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
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

// F-3.7: безопасность обеспечивается loopback-привязкой. Любой другой адрес — осознанное
// решение, о котором стоит предупредить в журнале.
if (!ConfigUpdateService.IsLoopback(cfg.MetricsHost))
{
    if (string.IsNullOrWhiteSpace(cfg.WebApiToken))
    {
        startupLogger.LogWarning(
            "Web API is bound to {Host}:{Port}, which is not a loopback address, and webApiToken is empty: " +
            "anyone who can reach this port can read metrics and change the configuration",
            cfg.MetricsHost,
            cfg.MetricsPort);
    }
    else
    {
        startupLogger.LogInformation(
            "Web API is bound to {Host}:{Port} and requires a bearer token for /api/*",
            cfg.MetricsHost,
            cfg.MetricsPort);
    }
}

try
{
    // Хост поднимает и Kestrel, и ProxyHostedService (F-1.1).
    await app.StartAsync();
    startupLogger.LogInformation("Web API listening on {Url}", listenUrl);
}
catch (Exception ex)
{
    // F-1.5: без веб-слоя управлять прокси нечем, поэтому отказ привязки фатален.
    // Отказ привязки самого прокси-листенера фатальным не является: его обрабатывает
    // ProxyHostedService, а readiness отдаёт 503.
    startupLogger.LogCritical(ex, "Failed to start web API on {Url}: the web server is the only interface of this edition", listenUrl);
    return 1;
}

// Ctrl+C/SIGTERM обрабатывает ConsoleLifetime хоста: он вызывает StopAsync,
// который гасит ProxyHostedService в порядке F-1.3.
await proxyService.Completion;

if (proxyService.Faulted)
{
    return 1;
}

try { await app.StopAsync(); } catch (Exception) { }

return 0;

// Собирает конфигурацию Serilog: консоль и, при заданном logPath, файл с ограничением размера.
// Уровень задаётся переключателем, чтобы его можно было менять на лету.
static Serilog.ILogger BuildSerilogLogger(Config cfg, Serilog.Core.LoggingLevelSwitch levelSwitch)
{
    var loggerConfiguration = new LoggerConfiguration()
        .MinimumLevel.ControlledBy(levelSwitch)
        .WriteTo.Console();

    if (!string.IsNullOrWhiteSpace(cfg.LogPath))
    {
        if (cfg.LogMaxMegabytes > 0)
        {
            var limitBytes = (long)Math.Max(32 * 1024, cfg.LogMaxMegabytes * 1024 * 1024);
            loggerConfiguration.WriteTo.File(
                new CompactJsonFormatter(),
                cfg.LogPath,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: limitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: Math.Max(1, cfg.LogRetainedFileCount + 1));
        }
        else
        {
            loggerConfiguration.WriteTo.File(new CompactJsonFormatter(), cfg.LogPath, rollingInterval: RollingInterval.Hour);
        }
    }

    return loggerConfiguration.CreateLogger();
}
