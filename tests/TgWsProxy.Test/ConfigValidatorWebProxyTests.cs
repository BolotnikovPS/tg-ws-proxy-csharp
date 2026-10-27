using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Валидация ключей WEB proxy (browser-relay): режим носителя (F-W.24) и лимиты ресурсов
/// (F-W.25). Проверяем и «счастливые» значения по умолчанию, и отказы, чтобы оператор не мог
/// сохранить конфиг, который сервер всё равно не сможет обслужить.
/// </summary>
public class ConfigValidatorWebProxyTests
{
    [Fact]
    public void Validate_AcceptsDefaultWebProxySettings()
    {
        var config = TestConfig.Create();
        ConfigValidator.Validate(config);
    }

    [Fact]
    public void WebProxyCarrierModes_ContainHttpsAndWebSocket()
    {
        Assert.Equal(["https", "websocket"], ConfigValidator.WebProxyCarrierModes);
    }

    [Theory]
    [InlineData("https")]
    [InlineData("websocket")]
    [InlineData("  websocket  ")]
    public void Validate_AcceptsSupportedCarrierModes(string mode)
    {
        var config = TestConfig.Create(c => c.WebProxy.CarrierMode = mode);
        ConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https-lanes")]
    [InlineData("websocket-lanes")]
    [InlineData("QUIC")]
    public void Validate_RejectsUnsupportedCarrierModes(string mode)
    {
        var config = TestConfig.Create(c => c.WebProxy.CarrierMode = mode);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxy.carrierMode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositiveSessionLimit()
    {
        var config = TestConfig.Create(c => c.WebProxy.MaxSessions = 0);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxy.maxSessions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNegativePerIpSessionLimit()
    {
        var config = TestConfig.Create(c => c.WebProxy.MaxSessionsPerIp = -1);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxy.maxSessionsPerIp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsZeroPerIpSessionLimit_AsUnlimited()
    {
        var config = TestConfig.Create(c => c.WebProxy.MaxSessionsPerIp = 0);
        ConfigValidator.Validate(config);
    }

    [Fact]
    public void Validate_RejectsGlobalStreamLimitBelowPerSessionLimit()
    {
        var config = TestConfig.Create(c =>
        {
            c.WebProxy.MaxStreams = 10;
            c.WebProxy.MaxStreamsPerSession = 128;
        });

        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxy.maxStreams", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositiveStreamLimits()
    {
        var perSession = TestConfig.Create(c => c.WebProxy.MaxStreamsPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(perSession));

        var global = TestConfig.Create(c => c.WebProxy.MaxStreams = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(global));
    }

    [Fact]
    public void Validate_RejectsPendingQueueAboveCap()
    {
        var config = TestConfig.Create(
            c => c.WebProxy.MaxPendingBytesPerSession = ConfigValidator.MaxWebProxyPendingBytes + 1);

        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxy.maxPendingBytesPerSession", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositivePendingQueueLimits()
    {
        var bytes = TestConfig.Create(c => c.WebProxy.MaxPendingBytesPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(bytes));

        var items = TestConfig.Create(c => c.WebProxy.MaxPendingItemsPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(items));
    }

    [Fact]
    public void Validate_AcceptsExplicitLimits()
    {
        var config = TestConfig.Create(c =>
        {
            c.WebProxy.CarrierMode = "websocket";
            c.WebProxy.MaxSessions = 128;
            c.WebProxy.MaxSessionsPerIp = 4;
            c.WebProxy.MaxStreamsPerSession = 128;
            c.WebProxy.MaxStreams = 4096;
            c.WebProxy.MaxPendingBytesPerSession = 32 * 1024 * 1024;
            c.WebProxy.MaxPendingItemsPerSession = 16384;
        });

        ConfigValidator.Validate(config);
    }
}