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
        var config = TestConfig.Create(c => c.WebProxyCarrierMode = mode);
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
        var config = TestConfig.Create(c => c.WebProxyCarrierMode = mode);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxyCarrierMode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositiveSessionLimit()
    {
        var config = TestConfig.Create(c => c.WebProxyMaxSessions = 0);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxyMaxSessions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNegativePerIpSessionLimit()
    {
        var config = TestConfig.Create(c => c.WebProxyMaxSessionsPerIp = -1);
        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxyMaxSessionsPerIp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsZeroPerIpSessionLimit_AsUnlimited()
    {
        var config = TestConfig.Create(c => c.WebProxyMaxSessionsPerIp = 0);
        ConfigValidator.Validate(config);
    }

    [Fact]
    public void Validate_RejectsGlobalStreamLimitBelowPerSessionLimit()
    {
        var config = TestConfig.Create(c =>
        {
            c.WebProxyMaxStreams = 10;
            c.WebProxyMaxStreamsPerSession = 128;
        });

        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxyMaxStreams", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositiveStreamLimits()
    {
        var perSession = TestConfig.Create(c => c.WebProxyMaxStreamsPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(perSession));

        var global = TestConfig.Create(c => c.WebProxyMaxStreams = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(global));
    }

    [Fact]
    public void Validate_RejectsPendingQueueAboveCap()
    {
        var config = TestConfig.Create(
            c => c.WebProxyMaxPendingBytesPerSession = ConfigValidator.MaxWebProxyPendingBytes + 1);

        var error = Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(config));
        Assert.Contains("webProxyMaxPendingBytesPerSession", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsNonPositivePendingQueueLimits()
    {
        var bytes = TestConfig.Create(c => c.WebProxyMaxPendingBytesPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(bytes));

        var items = TestConfig.Create(c => c.WebProxyMaxPendingItemsPerSession = 0);
        Assert.Throws<ArgumentException>(() => ConfigValidator.Validate(items));
    }

    [Fact]
    public void Validate_AcceptsExplicitLimits()
    {
        var config = TestConfig.Create(c =>
        {
            c.WebProxyCarrierMode = "websocket";
            c.WebProxyMaxSessions = 128;
            c.WebProxyMaxSessionsPerIp = 4;
            c.WebProxyMaxStreamsPerSession = 128;
            c.WebProxyMaxStreams = 4096;
            c.WebProxyMaxPendingBytesPerSession = 32 * 1024 * 1024;
            c.WebProxyMaxPendingItemsPerSession = 16384;
        });

        ConfigValidator.Validate(config);
    }
}
