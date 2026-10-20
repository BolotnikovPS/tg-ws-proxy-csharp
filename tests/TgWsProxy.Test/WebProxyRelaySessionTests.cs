using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты relay-сессии WEB proxy: подбор токена по HMAC bridge-capability (F-W.6/F-W.7) —
/// перебор <c>HandshakeOrder</c>, пропуск пустых секретов, отказ на пустом/неверном capability.
/// </summary>
public class WebProxyRelaySessionTests
{
    // Официальный вектор-секрет из tproxy-server PROTOCOL.md: 0x00..0x0f.
    private static readonly byte[] Secret =
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f];

    // Официальные векторы (tproxy-server PROTOCOL.md): host = "proxy.example.com".
    private const string V1Capability = "MHLEY5PmW1GWqJkSrlmJpvJUiLhBH_QKy6yKg8a0JPk"; // basePath ""
    private const string V2Capability = "hHz99Xs93EN1j91G9gpNepXwGNNt5YdAFkEVk_LlqdQ"; // basePath "dobry-cola-super-app"

    private static WebProxyRelaySession CreateSession(Config cfg, params ProxyTokenEntry[] tokens)
        => new(cfg, null!, null!, new FakeTokenRegistry(tokens), null!, null!, null!, null!);

    private static ProxyTokenEntry Token(string id, byte[] secret)
        => new(id, $"token-{id}", Convert.ToHexString(secret).ToLowerInvariant(), secret, true, DateTimeOffset.UtcNow, null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveToken_EmptyCapability_ReturnsNull(string? capability)
    {
        var session = CreateSession(new Config(), Token("a", Secret));

        Assert.Null(session.ResolveToken("proxy.example.com", capability));
    }

    [Fact]
    public void ResolveToken_V1Context_MatchesRootBasePath()
    {
        var session = CreateSession(new Config(), Token("a", Secret));

        var resolved = session.ResolveToken("proxy.example.com", V1Capability);

        Assert.NotNull(resolved);
        Assert.Equal("a", resolved.Id);
    }

    [Fact]
    public void ResolveToken_V2Context_MatchesBasePath()
    {
        // Голый слаг basePath даёт v2-контекст "…\nproxy.example.com\ndobry-cola-super-app".
        var cfg = new Config { WebProxyBasePath = "dobry-cola-super-app" };
        var session = CreateSession(cfg, Token("a", Secret));

        var resolved = session.ResolveToken("proxy.example.com", V2Capability);

        Assert.NotNull(resolved);
        Assert.Equal("a", resolved.Id);
    }

    [Fact]
    public void ResolveToken_WrongSecret_ReturnsNull()
    {
        var other = Token("a",
            [0xff, 0xee, 0xdd, 0xcc, 0xbb, 0xaa, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0x00]);
        var session = CreateSession(new Config(), other);

        Assert.Null(session.ResolveToken("proxy.example.com", V1Capability));
    }

    [Fact]
    public void ResolveToken_SkipsEmptySecretToken()
    {
        var session = CreateSession(new Config(), Token("empty", []), Token("match", Secret));

        var resolved = session.ResolveToken("proxy.example.com", V1Capability);

        Assert.NotNull(resolved);
        Assert.Equal("match", resolved.Id);
    }

    private sealed class FakeTokenRegistry(IReadOnlyList<ProxyTokenEntry> order) : IProxyTokenRegistry
    {
        public IReadOnlyList<ProxyTokenEntry> HandshakeOrder => order;

        public IReadOnlyList<ProxyTokenEntry> All => order;

        public int EnabledCount => order.Count;

        public ProxyTokenEntry? Find(string id) => order.FirstOrDefault(t => t.Id == id);

        public void Touch(string tokenId)
        {
        }

        public void Reload()
        {
        }
    }
}
