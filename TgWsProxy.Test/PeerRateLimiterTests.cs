using System.Net;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты ограничителя частоты подключений с одного IP (--rate-limit / --rate-limit-window).
/// </summary>
public class PeerRateLimiterTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Disabled_When_Limit_Is_Zero()
    {
        var limiter = new PeerRateLimiter(new Config());

        Assert.False(limiter.IsEnabled);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
        }
    }

    [Fact]
    public void Allows_Up_To_Limit_Then_Rejects()
    {
        var limiter = new PeerRateLimiter(3, 60);

        Assert.True(limiter.IsEnabled);
        Assert.Equal(3, limiter.Limit);
        Assert.Equal(60, limiter.WindowSeconds);

        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));

        // Четвёртое подключение с того же адреса в том же окне отклоняется.
        Assert.False(limiter.TryAcquire(Ip("203.0.113.9")));

        // Другой адрес имеет собственное окно.
        Assert.True(limiter.TryAcquire(Ip("198.51.100.7")));
    }

    [Fact]
    public void Window_Resets_After_Expiry()
    {
        var now = 1_000.0;
        var limiter = new PeerRateLimiter(1, 60, () => now);

        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
        Assert.False(limiter.TryAcquire(Ip("203.0.113.9")));

        // Через окно счётчик сбрасывается, и адрес снова допускается.
        now += 61;
        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
        Assert.False(limiter.TryAcquire(Ip("203.0.113.9")));
    }

    [Fact]
    public void Mapped_Ipv6_And_Ipv4_Share_Same_Counter()
    {
        var limiter = new PeerRateLimiter(1, 60);

        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));

        // ::ffff:203.0.113.9 — тот же клиент, окно общее.
        Assert.False(limiter.TryAcquire(Ip("::ffff:203.0.113.9")));
    }

    [Fact]
    public void Expired_Entries_Are_Swept()
    {
        var now = 5_000.0;
        var limiter = new PeerRateLimiter(5, 10, () => now);

        Assert.True(limiter.TryAcquire(Ip("203.0.113.1")));
        Assert.True(limiter.TryAcquire(Ip("203.0.113.2")));
        Assert.Equal(2, limiter.TrackedPeers);

        // Сдвиг на 40с превышает и окно (10с), и интервал очистки (30с), поэтому обращение
        // к новому адресу запускает очистку и удаляет обе устаревшие записи.
        now += 40;
        Assert.True(limiter.TryAcquire(Ip("203.0.113.3")));
        Assert.Equal(1, limiter.TrackedPeers);
    }

    [Fact]
    public void Zero_Window_Disables_Limiter()
    {
        var limiter = new PeerRateLimiter(5, 0);

        Assert.False(limiter.IsEnabled);
        Assert.True(limiter.TryAcquire(Ip("203.0.113.9")));
    }
}
