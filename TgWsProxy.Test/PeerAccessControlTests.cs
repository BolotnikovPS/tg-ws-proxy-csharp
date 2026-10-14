using System.Net;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты IP-списков доступа (--allow-ip / --deny-ip): семантика пустых списков,
/// приоритет deny над allow, CIDR и нормализация IPv4-mapped IPv6.
/// </summary>
public class PeerAccessControlTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Allow_Empty_AllowsAnyIp()
    {
        var acl = new PeerAccessControl(new Config());

        Assert.True(acl.IsAllowed(Ip("203.0.113.9")));
        Assert.True(acl.IsAllowed(Ip("10.0.0.1")));
        Assert.True(acl.IsAllowed(Ip("::1")));
        Assert.Equal(0, acl.AllowCount);
        Assert.Equal(0, acl.DenyCount);
    }

    [Fact]
    public void Deny_Empty_DoesNotBlockAnyIp()
    {
        var cfg = new Config();
        cfg.AllowIp.Add("10.0.0.0/8");
        var acl = new PeerAccessControl(cfg);

        // deny пуст — блокировки нет, действует только allow.
        Assert.True(acl.IsAllowed(Ip("10.1.2.3")));
        Assert.False(acl.IsAllowed(Ip("192.168.1.1")));
        Assert.Equal(0, acl.DenyCount);
    }

    [Fact]
    public void Deny_TakesPriority_OverAllow()
    {
        var cfg = new Config();
        cfg.AllowIp.Add("10.0.0.0/8");
        cfg.DenyIp.Add("10.1.2.3");
        var acl = new PeerAccessControl(cfg);

        Assert.False(acl.IsAllowed(Ip("10.1.2.3")));
        Assert.True(acl.IsAllowed(Ip("10.9.9.9")));
    }

    [Fact]
    public void Allow_SingleIp_BlocksOthers()
    {
        var cfg = new Config();
        cfg.AllowIp.Add("192.168.1.5");
        var acl = new PeerAccessControl(cfg);

        Assert.True(acl.IsAllowed(Ip("192.168.1.5")));
        Assert.False(acl.IsAllowed(Ip("192.168.1.6")));
    }

    [Fact]
    public void Allow_Cidr_AllowsMatchingAndBlocksOthers()
    {
        var cfg = new Config();
        cfg.AllowIp.Add("10.20.0.0/16");
        var acl = new PeerAccessControl(cfg);

        Assert.True(acl.IsAllowed(Ip("10.20.0.1")));
        Assert.True(acl.IsAllowed(Ip("10.20.255.254")));
        Assert.False(acl.IsAllowed(Ip("10.21.0.1")));
    }

    [Fact]
    public void Deny_Cidr_BlocksMatching()
    {
        var cfg = new Config();
        cfg.DenyIp.Add("172.16.0.0/12");
        var acl = new PeerAccessControl(cfg);

        Assert.False(acl.IsAllowed(Ip("172.16.0.1")));
        Assert.False(acl.IsAllowed(Ip("172.31.255.255")));
        Assert.True(acl.IsAllowed(Ip("172.32.0.1")));
    }

    [Fact]
    public void IsAllowed_NormalizesIpv4MappedIpv6()
    {
        var cfg = new Config();
        cfg.DenyIp.Add("1.2.3.4");
        var acl = new PeerAccessControl(cfg);

        Assert.False(acl.IsAllowed(IPAddress.Parse("::ffff:1.2.3.4")));
        Assert.True(acl.IsAllowed(IPAddress.Parse("::ffff:1.2.3.5")));
    }

    [Fact]
    public void TryParseEntry_AcceptsCidrAndBareIp()
    {
        Assert.True(PeerAccessControl.TryParseEntry("10.0.0.0/8", out var cidr));
        Assert.Equal(8, cidr.PrefixLength);

        Assert.True(PeerAccessControl.TryParseEntry("192.168.1.5", out var single));
        Assert.Equal(32, single.PrefixLength);

        Assert.True(PeerAccessControl.TryParseEntry("::1", out var singleV6));
        Assert.Equal(128, singleV6.PrefixLength);
    }

    [Fact]
    public void TryParseEntry_RejectsInvalid()
    {
        Assert.False(PeerAccessControl.TryParseEntry("not-an-ip", out _));
        Assert.False(PeerAccessControl.TryParseEntry("10.0.0.0/33", out _));
        Assert.False(PeerAccessControl.TryParseEntry("", out _));
    }
}
