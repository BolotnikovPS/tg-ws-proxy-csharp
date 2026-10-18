using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Проверки сборки ссылок WEB proxy (browser-relay): внешний домен bridge, маркерный
/// секрет <c>tg://proxy</c> и bridge-URL с HMAC capability (DESIGN-WEB-PROXY §2.2/§2.3).
/// </summary>
public sealed class WebProxyLinkBuilderTests
{
    private static Config WebProxyConfig(string webProxyHost, string? webProxyPublicHost = null, string basePath = "")
    {
        var config = TestConfig.Create();
        config.WebProxyEnabled = true;
        config.WebProxyHost = webProxyHost;
        config.WebProxyPublicHost = webProxyPublicHost ?? "";
        config.WebProxyBasePath = basePath;
        return config;
    }

    private static ProxyToken Token(Config config) => config.Tokens[0];

    [Fact]
    public void ResolveHost_Prefers_WebProxyPublicHost()
    {
        var config = WebProxyConfig("0.0.0.0", "bridge.example.com");

        Assert.Equal("bridge.example.com", WebProxyLinkBuilder.ResolveHost(config));
    }

    [Fact]
    public void ResolveHost_Falls_Back_To_WebProxyHost()
    {
        var config = WebProxyConfig("bridge.example.com");

        Assert.Equal("bridge.example.com", WebProxyLinkBuilder.ResolveHost(config));
    }

    [Fact]
    public void ResolveHost_Rejects_Wildcard()
    {
        var config = WebProxyConfig("0.0.0.0");

        Assert.Null(WebProxyLinkBuilder.ResolveHost(config));
    }

    [Fact]
    public void ResolveHost_Strips_Explicit_Port()
    {
        var config = WebProxyConfig("0.0.0.0", "bridge.example.com:8443");

        Assert.Equal("bridge.example.com", WebProxyLinkBuilder.ResolveHost(config));
    }

    [Fact]
    public void BuildMarkerSecret_Root_Uses_Hex_With_70_Marker()
    {
        Assert.Equal(
            "70" + TestConfig.TokenSecret,
            WebProxyLinkBuilder.BuildMarkerSecret(TestConfig.TokenSecret, ""));
    }

    [Fact]
    public void BuildMarkerSecret_BasePath_Uses_Base64Url_Without_Padding()
    {
        var marker = WebProxyLinkBuilder.BuildMarkerSecret(TestConfig.TokenSecret, "/v1");

        var marked = new byte[17];
        marked[0] = 0x70; // kWebProxyLinkSecretMarker
        Convert.FromHexString(TestConfig.TokenSecret).CopyTo(marked, 1);
        var expected = Convert.ToBase64String(marked).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, marker);
        Assert.DoesNotContain('=', marker);
    }

    [Fact]
    public void BuildTgLink_Null_For_Wildcard_Host()
    {
        var config = WebProxyConfig("0.0.0.0");

        Assert.Null(WebProxyLinkBuilder.BuildTgLink(config, Token(config)));
    }

    [Fact]
    public void BuildTgLink_Formats_With_443_And_Marker_Secret()
    {
        var config = WebProxyConfig("0.0.0.0", "bridge.example.com");

        Assert.Equal(
            $"tg://proxy?server=bridge.example.com&port=443&secret=70{TestConfig.TokenSecret}",
            WebProxyLinkBuilder.BuildTgLink(config, Token(config)));
    }

    [Fact]
    public void BuildBridgeUrl_Root_Matches_Capability()
    {
        var config = WebProxyConfig("0.0.0.0", "bridge.example.com");
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(TestConfig.TokenSecret), "bridge.example.com", "");

        Assert.Equal(
            $"https://bridge.example.com/?bridge={capability}",
            WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }

    [Fact]
    public void BuildBridgeUrl_BasePath_Matches_V2_Capability()
    {
        var config = WebProxyConfig("0.0.0.0", "bridge.example.com", "v1");
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(TestConfig.TokenSecret), "bridge.example.com", "/v1");

        Assert.Equal(
            $"https://bridge.example.com/v1/?bridge={capability}",
            WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }

    [Fact]
    public void BuildBridgeUrl_Null_For_Wildcard_Host()
    {
        var config = WebProxyConfig("0.0.0.0");

        Assert.Null(WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }
}
