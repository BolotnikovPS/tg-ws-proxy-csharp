using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure;

var cfg = CliParser.Parse(args);
if (cfg.DcIp.Count == 0)
{
    cfg.DcIp.Add("2:149.154.167.220");
    cfg.DcIp.Add("4:149.154.167.220");
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
    builder.ClearProviders();
    builder.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
        options.SingleLine = true;
    });
    builder.SetMinimumLevel(cfg.Verbose ? LogLevel.Debug : LogLevel.Information);
})
    .AddProxyCore(cfg, dcOpt)
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

var startupLogger = provider.GetRequiredService<ILogger<Program>>();
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
var statsTask = Task.Run(async () =>
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
}, cts.Token);
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
