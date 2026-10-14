using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Reflection;
using System.Security.Cryptography;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.Instances;

// Информационные флаги обрабатываются до разбора конфигурации: --help/-h и --version.
// InformationalVersion хранит ровно то, что указано в <Version> проекта (например "2.4").
var appVersion = typeof(Program).Assembly
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
    .Split('+')[0] ?? "unknown";
if (CliParser.TryHandleInfo(args, appVersion, out var infoText))
{
    Console.WriteLine(infoText);
    return 0;
}

var cfg = CliParser.Parse(args);


if (string.IsNullOrWhiteSpace(cfg.Secret))
{
    cfg.Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

Dictionary<int, string> dcOpt;
try
{
    ConfigValidator.Validate(cfg);
    dcOpt = CliParser.ParseDcIpList(cfg.DcIp);
}
catch (Exception e)
{
    Console.WriteLine(e.Message);
    return 1;
}

await using var provider = new ServiceCollection()
    .AddLogging(builder =>
    {
        var logLevel = cfg.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;

        var loggerConfiguration = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console(logLevel);

        if (!string.IsNullOrWhiteSpace(cfg.LogPath))
        {
            if (cfg.LogMaxMegabytes > 0)
            {
                var limitBytes = (long)Math.Max(32 * 1024, cfg.LogMaxMegabytes * 1024 * 1024);
                loggerConfiguration.WriteTo.File(
                    new CompactJsonFormatter(),
                    cfg.LogPath,
                    restrictedToMinimumLevel: logLevel,
                    rollingInterval: RollingInterval.Infinite,
                    fileSizeLimitBytes: limitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: Math.Max(1, cfg.LogRetainedFileCount + 1));
            }
            else
            {
                loggerConfiguration.WriteTo.File(new CompactJsonFormatter(), cfg.LogPath, logLevel, rollingInterval: RollingInterval.Hour);
            }
        }

        builder
        .ClearProviders()
        .AddDebug()
        .AddSerilog(loggerConfiguration.CreateLogger());
    })
    .AddSingleton(cfg)
    .AddSingleton(dcOpt)
    .AddProxyApplication()
    .AddProxyInfrastructure()
    .BuildServiceProvider();

var runtimeLoggerFactory = provider.GetRequiredService<ILoggerFactory>();
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

var server = provider.GetRequiredService<IProxyServer>();
var sessionHandler = provider.GetRequiredService<IClientSessionHandler>();
var stats = provider.GetRequiredService<IProxyStats>();
var wsPool = provider.GetRequiredService<IWsPool>();
var cfWorkerPool = provider.GetRequiredService<ICfWorkerPool>();
var cfDomainRefresher = provider.GetRequiredService<ICfDomainRefresher>();
var cfH2Pool = provider.GetRequiredService<ICfH2Pool>();
var bridgeService = provider.GetRequiredService<ITcpBridgeService>();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

// Сброс состояния маршрутизации перед стартом (порт upstream _run).
bridgeService.ResetTcpBackoff();
wsPool.Reset();
cfWorkerPool.Reset();
cfDomainRefresher.Start(cts.Token);

var statsTask = Task.Factory.StartNew(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cts.Token);
            startupLogger.LogInformation("stats: {Summary} | pool: {Pool}", stats.Summary(), wsPool.FormatPoolSummary());
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
},
cts.Token,
TaskCreationOptions.LongRunning,
TaskScheduler.Current
).Unwrap();

try
{
    sessionHandler.Warmup(cts.Token);
    cfWorkerPool.Warmup();
    await server.Run(cts.Token);
}
catch (Exception ex)
{
    startupLogger.LogCritical(ex, "Server crashed");
    return 1;
}
finally
{
    cts.Cancel();
    cfDomainRefresher.Stop();
    wsPool.Reset();
    cfWorkerPool.Reset();
    try { await cfH2Pool.CloseAsync(); } catch (Exception) { }
    try { await statsTask; } catch (OperationCanceledException) { }
}
return 0;
