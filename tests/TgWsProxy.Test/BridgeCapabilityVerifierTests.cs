using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты авторизации bridge-канала: HMAC-SHA256 по контекстам v1/v2, base64url без паддинга,
/// постоянное время сравнения, отказ на неверном/пустом capability и неправильной длине.
/// </summary>
public class BridgeCapabilityVerifierTests
{
    // Секрет 16 байт: 0x01..0x10.
    private static readonly byte[] Secret =
        [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10];

    [Fact]
    public void ComputeCapability_V1_MatchesReferenceVector()
    {
        // Вектор рассчитан независимо (Node crypto): context = "tdesktop-web-proxy-bridge-v1\nexample.com".
        const string expected = "xk3aloth6KpS69vPUCgF9KKPSg0-E78k4UuWNZOVo40";

        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "");

        Assert.Equal(expected, capability);
    }

    [Fact]
    public void ComputeCapability_V2_MatchesReferenceVector()
    {
        // Вектор рассчитан независимо (Node crypto): context = "tdesktop-web-proxy-bridge-v2\nexample.com\n/proxy".
        const string expected = "p2QMopLb7q1wi3Cu4Zr8qHlNtrhwKwPFjH5CjZU9yOc";

        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "/proxy");

        Assert.Equal(expected, capability);
    }

    [Fact]
    public void ComputeCapability_IsBase64UrlWithoutPadding()
    {
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "");

        Assert.DoesNotContain('+', capability);
        Assert.DoesNotContain('/', capability);
        Assert.DoesNotContain('=', capability);
        Assert.Equal(43, capability.Length); // 32 байта -> base64 без паддинга
    }

    [Fact]
    public void Verify_CorrectCapability_Accepts()
    {
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "/proxy");

        var ok = BridgeCapabilityVerifier.Verify(Secret, "example.com", "/proxy", capability);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_WrongSecret_Rejects()
    {
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "");
        var otherSecret = (byte[])Secret.Clone();
        otherSecret[0] ^= 0xFF;

        var ok = BridgeCapabilityVerifier.Verify(otherSecret, "example.com", "", capability);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_WrongHost_Rejects()
    {
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "");

        var ok = BridgeCapabilityVerifier.Verify(Secret, "other.com", "", capability);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_WrongBasePath_Rejects()
    {
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "/proxy");

        var ok = BridgeCapabilityVerifier.Verify(Secret, "example.com", "/other", capability);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_EmptyCapability_Rejects()
    {
        var ok = BridgeCapabilityVerifier.Verify(Secret, "example.com", "", "");

        Assert.False(ok);
    }

    [Fact]
    public void Verify_GarbageCapability_Rejects()
    {
        Assert.False(BridgeCapabilityVerifier.Verify(Secret, "example.com", "", "!!!not-base64url!!!"));
        Assert.False(BridgeCapabilityVerifier.Verify(Secret, "example.com", "", "abcd"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("v1", "/v1")]
    [InlineData("/v1", "/v1")]
    [InlineData("/v1/", "/v1")]
    [InlineData("/proxy/", "/proxy")]
    public void NormalizeBasePath_ProducesCanonicalForm(string? input, string expected)
    {
        Assert.Equal(expected, BridgeCapabilityVerifier.NormalizeBasePath(input));
    }
}
