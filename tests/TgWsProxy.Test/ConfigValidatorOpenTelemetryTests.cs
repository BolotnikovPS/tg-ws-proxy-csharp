using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Валидация ключей OpenTelemetry (OTLP): протокол из фиксированного набора, endpoint — абсолютный
/// http(s)-URL. Пустые значения допустимы (тогда SDK/Serilog используют default или переменные OTEL_*).
/// </summary>
public class ConfigValidatorOpenTelemetryTests
{
    [Fact]
    public void Validate_AcceptsDefaultOtelSettings()
    {
        var config = TestConfig.Create();
        ConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("http")]
    [InlineData("  http  ")]
    public void Validate_AcceptsSupportedProtocols(string protocol)
    {
        var config = TestConfig.Create(c => c.Otel.Protocol = protocol);
        ConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData("https")]
    [InlineData("grpc-x")]
    public void Validate_RejectsUnsupportedProtocols(string protocol)
    {
        var config = TestConfig.Create(c => c.Otel.Protocol = protocol);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://otel-collector:4317")]
    [InlineData("https://collector.example.com/v1/traces")]
    public void Validate_AcceptsEmptyAndHttpEndpoints(string endpoint)
    {
        var config = TestConfig.Create(c => c.Otel.Endpoint = endpoint);
        ConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData("collector:4317")]
    [InlineData("not a url")]
    [InlineData("ftp://collector:4317")]
    public void Validate_RejectsNonHttpEndpoints(string endpoint)
    {
        var config = TestConfig.Create(c => c.Otel.Endpoint = endpoint);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
    }
}