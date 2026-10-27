using Serilog;
using Serilog.Events;
using TgWsProxy.Domain;
using TgWsProxy.Logging;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="SecretMaskingPolicy"/>: защитная сетка от попадания секретов в логи при
/// случайной деструктуризации <c>{@...}</c>.
/// </summary>
public class SecretMaskingPolicyTests
{
    [Fact]
    public void Destructure_Masks_Config()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("cfg={@Config}", new Config { WebProxy = new WebProxyConfig { CertPassword = "hunter2" } });

        var evt = sink.Events.Single();
        var prop = Assert.IsType<ScalarValue>(evt.Properties["Config"]);
        Assert.Equal("***", prop.Value);
    }

    [Fact]
    public void Destructure_Leaves_Ordinary_Objects_Intact()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("obj={@Obj}", new { Name = "x" });

        var evt = sink.Events.Single();
        Assert.IsType<StructureValue>(evt.Properties["Obj"]);
    }

    private static (Serilog.ILogger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Destructure.With(new SecretMaskingPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }
}