using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Проверки сборки ссылок WEB proxy (browser-relay): внешний домен bridge, секрет
/// <c>tg://webproxy</c> и bridge-URL с HMAC capability (SPEC §2.2/§2.3).
/// </summary>
public sealed class WebProxyLinkBuilderTests
{
    private static Config WebProxyConfig(string webProxyPublicHost, string basePath = "")
    {
        var config = TestConfig.Create();
        config.WebProxy.Enabled = true;
        config.WebProxy.PublicHost = webProxyPublicHost;
        config.WebProxy.BasePath = basePath;
        return config;
    }

    private static ProxyToken Token(Config config) => config.Tokens[0];

    [Fact]
    public void ResolveHost_Uses_PublicHost()
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
        var config = WebProxyConfig("bridge.example.com:8443");

        Assert.Equal("bridge.example.com", WebProxyLinkBuilder.ResolveHost(config));
    }

    [Fact]
    public void BuildMarkerSecret_Root_Returns_Plain_Secret()
    {
        Assert.Equal(
            TestConfig.TokenSecret,
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
    public void BuildShareLink_Null_For_Wildcard_Host()
    {
        var config = WebProxyConfig("0.0.0.0");

        Assert.Null(WebProxyLinkBuilder.BuildShareLink(config, Token(config)));
        Assert.Null(WebProxyLinkBuilder.BuildDeepLink(config, Token(config)));
    }

    [Fact]
    public void BuildShareLink_Root_Uses_Telegram_WebProxy_Template()
    {
        var config = WebProxyConfig("bridge.example.com");

        Assert.Equal(
            $"{WebProxyLinkBuilder.ShareLinkPrefix}?server=bridge.example.com&secret={TestConfig.TokenSecret}",
            WebProxyLinkBuilder.BuildShareLink(config, Token(config)));
    }

    [Fact]
    public void BuildShareLink_BasePath_Encodes_Server_Slash()
    {
        var config = WebProxyConfig("bridge.example.com", "v1");
        var marker = WebProxyLinkBuilder.BuildMarkerSecret(TestConfig.TokenSecret, "v1");

        Assert.Equal(
            $"{WebProxyLinkBuilder.ShareLinkPrefix}?server=bridge.example.com%2Fv1&secret={marker}",
            WebProxyLinkBuilder.BuildShareLink(config, Token(config)));
    }

    [Fact]
    public void BuildDeepLink_Matches_ShareLink_Shape()
    {
        var config = WebProxyConfig("bridge.example.com");

        Assert.Equal(
            $"{WebProxyLinkBuilder.DeepLinkPrefix}?server=bridge.example.com&secret={TestConfig.TokenSecret}",
            WebProxyLinkBuilder.BuildDeepLink(config, Token(config)));
    }

    [Theory]
    [InlineData("bridge.example.com", "bridge.example.com")]
    [InlineData("  Bridge.Example.COM  ", "bridge.example.com")]
    [InlineData("bridge.example.com:8443", null)]
    [InlineData("bridge.example.com/path", null)]
    [InlineData("bridge.example.com.", null)]
    [InlineData("localhost", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("127.1", null)]
    [InlineData("0x7f.1", null)]
    [InlineData("", null)]
    public void NormalizeHost_Rejects_Ip_And_Non_Domain_Forms(string input, string? expected)
    {
        Assert.Equal(expected, WebProxyLinkBuilder.NormalizeHost(input));
    }

    [Fact]
    public void NormalizeHost_Converts_Idn_To_Ace()
    {
        Assert.Equal("xn--bcher-kva.example", WebProxyLinkBuilder.NormalizeHost("bücher.example"));
    }

    [Fact]
    public void BuildShareLink_Uses_Ace_Form_For_Idn_Host()
    {
        var config = WebProxyConfig("bücher.example");

        Assert.Equal(
            $"{WebProxyLinkBuilder.ShareLinkPrefix}?server=xn--bcher-kva.example&secret={TestConfig.TokenSecret}",
            WebProxyLinkBuilder.BuildShareLink(config, Token(config)));
    }

    [Fact]
    public void BuildBridgeUrl_Root_Matches_Capability()
    {
        var config = WebProxyConfig("bridge.example.com");
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(TestConfig.TokenSecret), "bridge.example.com", "");

        Assert.Equal(
            $"https://bridge.example.com/?bridge={capability}",
            WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }

    [Fact]
    public void BuildBridgeUrl_BasePath_Matches_V2_Capability()
    {
        var config = WebProxyConfig("bridge.example.com", "v1");
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(TestConfig.TokenSecret), "bridge.example.com", "v1");

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

    [Fact]
    public void BuildBridgeUrl_Allows_Ip_Host_For_Local_Debugging()
    {
        var config = WebProxyConfig("127.0.0.1");
        var capability = BridgeCapabilityVerifier.ComputeCapability(
            Convert.FromHexString(TestConfig.TokenSecret), "127.0.0.1", "");

        Assert.Equal(
            $"https://127.0.0.1/?bridge={capability}",
            WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }

    [Fact]
    public void BuildShareLink_Null_For_Ip_Host()
    {
        // Telegram принимает для WEB proxy только доменное имя: IP-адрес в ссылке невалиден,
        // при этом bridge-URL (отладочный адрес) остаётся доступным.
        var config = WebProxyConfig("127.0.0.1");

        Assert.Null(WebProxyLinkBuilder.BuildShareLink(config, Token(config)));
        Assert.Null(WebProxyLinkBuilder.BuildDeepLink(config, Token(config)));
        Assert.NotNull(WebProxyLinkBuilder.BuildBridgeUrl(config, Token(config)));
    }

    [Fact]
    public void ResolveBridgeHost_Keeps_Ip_And_Lowercases_Domain()
    {
        Assert.Equal("127.0.0.1", WebProxyLinkBuilder.ResolveBridgeHost(WebProxyConfig("127.0.0.1")));
        Assert.Equal(
            "bridge.example.com",
            WebProxyLinkBuilder.ResolveBridgeHost(WebProxyConfig("Bridge.Example.COM")));
        Assert.Null(WebProxyLinkBuilder.ResolveBridgeHost(WebProxyConfig("0.0.0.0")));
    }
}
