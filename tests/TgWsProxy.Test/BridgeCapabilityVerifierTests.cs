using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты авторизации bridge-канала: HMAC-SHA256 по контекстам v1/v2, base64url без паддинга,
/// постоянное время сравнения, отказ на неверном/пустом capability и неправильной длине.
/// </summary>
public class BridgeCapabilityVerifierTests
{
    // Официальный вектор-секрет из tproxy-server PROTOCOL.md: 0x00..0x0f.
    private static readonly byte[] Secret =
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    [Fact]
    public void ComputeCapability_V1_MatchesReferenceVector()
    {
        // Официальный вектор (tproxy-server PROTOCOL.md): host=proxy.example.com, секрет 00..0f.
        const string expected = "MHLEY5PmW1GWqJkSrlmJpvJUiLhBH_QKy6yKg8a0JPk";

        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "proxy.example.com", "");

        Assert.Equal(expected, capability);
    }

    [Fact]
    public void ComputeCapability_V2_MatchesReferenceVector()
    {
        // Официальный вектор: host=proxy.example.com, basePath=dobry-cola-super-app, секрет 00..0f.
        const string expected = "hHz99Xs93EN1j91G9gpNepXwGNNt5YdAFkEVk_LlqdQ";

        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "proxy.example.com", "dobry-cola-super-app");

        Assert.Equal(expected, capability);
    }

    [Fact]
    public void ComputeCapability_V1_With_DdPrefix_MatchesReferenceVector()
    {
        // Официальный вектор: секрет dd000102...0f (17 байт), host=proxy.example.com.
        const string expected = "IpJrt3e7sKtzPyoXy6w-Zj6GGEvsvclN66JzQEfPYLA";
        var dd = Convert.FromHexString("dd000102030405060708090a0b0c0d0e0f");

        var capability = BridgeCapabilityVerifier.ComputeCapability(dd, "proxy.example.com", "");

        Assert.Equal(expected, capability);
    }

    [Fact]
    public void ComputeCapability_V2_With_DdPrefix_MatchesReferenceVector()
    {
        // Официальный вектор: секрет dd000102...0f, basePath=dobry-cola-super-app.
        const string expected = "TGUkZaevsavLbHvlNWipnRoYxgzZ51ioWvbxgGT3wHo";
        var dd = Convert.FromHexString("dd000102030405060708090a0b0c0d0e0f");

        var capability = BridgeCapabilityVerifier.ComputeCapability(dd, "proxy.example.com", "dobry-cola-super-app");

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
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "proxy");

        var ok = BridgeCapabilityVerifier.Verify(Secret, "example.com", "proxy", capability);

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
        var capability = BridgeCapabilityVerifier.ComputeCapability(Secret, "example.com", "proxy");

        var ok = BridgeCapabilityVerifier.Verify(Secret, "example.com", "other", capability);

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
    [InlineData("v1", "v1")]
    [InlineData("/v1", "v1")]
    [InlineData("/v1/", "v1")]
    [InlineData("/proxy/", "proxy")]
    public void NormalizeBasePath_ProducesCanonicalForm(string? input, string expected)
    {
        Assert.Equal(expected, BridgeCapabilityVerifier.NormalizeBasePath(input));
    }
}
