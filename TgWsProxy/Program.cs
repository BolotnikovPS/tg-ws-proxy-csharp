using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure;

var cfg = CliParser.Parse(args);
if (cfg.DcIp.Count == 0)
{
    // IP должны соответствовать номеру DC: для WSS используется SNI kws{N}.web.telegram.org к этому адресу.
    cfg.DcIp.Add("2:149.154.167.220");
    cfg.DcIp.Add("4:149.154.167.91");
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

var services = new ServiceCollection();
services.AddLogging(builder =>
{
    var logLevel = cfg.Verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    var loggerConfiguration = new LoggerConfiguration()
        .WriteTo.Console(logLevel);

    if (!string.IsNullOrWhiteSpace(cfg.LogPath))
    {
        loggerConfiguration.WriteTo.File(new CompactJsonFormatter(), cfg.LogPath, logLevel, rollingInterval: RollingInterval.Hour);
    }

    builder.AddSerilog(loggerConfiguration.CreateLogger());
})
    .AddSingleton(cfg)
    .AddSingleton(dcOpt)
    .AddProxyApplication()
    .AddProxyInfrastructure();

await using var provider = services.BuildServiceProvider();
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
if (cfg.Credentials.Count > 0)
{
    startupLogger.LogInformation("SOCKS5 auth enabled. Accounts: {Count}", cfg.Credentials.Count);
}
else
{
    startupLogger.LogInformation("SOCKS5 auth disabled.");
}

var server = provider.GetRequiredService<IProxyServer>();
var sessionHandler = provider.GetRequiredService<IClientSessionHandler>();
var stats = provider.GetRequiredService<IProxyStats>();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var statsTask = Task.Factory.StartNew(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cts.Token);
            startupLogger.LogInformation("stats: {Summary}", stats.Summary());
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
    await sessionHandler.WarmupAsync(cts.Token);
    await server.RunAsync(cts.Token);
}
catch (Exception ex)
{
    startupLogger.LogCritical(ex, "Server crashed");
    return 1;
}
finally
{
    cts.Cancel();
    try { await statsTask; } catch (OperationCanceledException) { }
}
return 0;
