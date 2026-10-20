using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Infrastructure.Session;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты реестра живых сессий: разрыв через действие отмены (WEB proxy, F-W.21) и через сокет
/// (TCP), привязка к токену и снятие с учёта по Dispose.
/// </summary>
public class ActiveSessionRegistryTests
{
    private static ActiveSessionRegistry CreateRegistry() =>
        new(NullLogger<ActiveSessionRegistry>.Instance);

    [Fact]
    public void Register_Abort_CountsSession()
    {
        var registry = CreateRegistry();
        var lease = registry.Register(NoopAbort, "peer|1");

        Assert.Equal(1, registry.Count);
        Assert.Null(lease.TokenId);

        lease.Dispose();
    }

    [Fact]
    public void AssignToken_ExposesTokenId()
    {
        var registry = CreateRegistry();
        using var lease = registry.Register(NoopAbort, "peer|1");

        lease.AssignToken("t1");

        Assert.Equal("t1", lease.TokenId);
    }

    [Fact]
    public void CloseByToken_InvokesAbort_AndUnregisters()
    {
        var registry = CreateRegistry();
        var aborted = 0;
        using var lease = registry.Register(() => aborted++, "peer|1");
        lease.AssignToken("t1");

        var closed = registry.CloseByToken("t1");

        Assert.Equal(1, closed);
        Assert.Equal(1, aborted);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CloseByToken_WrongToken_DoesNotAbort()
    {
        var registry = CreateRegistry();
        var aborted = 0;
        using var lease = registry.Register(() => aborted++, "peer|1");
        lease.AssignToken("t1");

        var closed = registry.CloseByToken("t-other");

        Assert.Equal(0, closed);
        Assert.Equal(0, aborted);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void CloseAll_InvokesAbortForEverySession()
    {
        var registry = CreateRegistry();
        var aborted = 0;
        using var a = registry.Register(() => aborted++, "peer|1");
        using var b = registry.Register(() => aborted++, "peer|2");

        var closed = registry.CloseAll();

        Assert.Equal(2, closed);
        Assert.Equal(2, aborted);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Dispose_UnregistersWithoutAbort()
    {
        var registry = CreateRegistry();
        var aborted = 0;
        var lease = registry.Register(() => aborted++, "peer|1");

        lease.Dispose();

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, aborted);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var registry = CreateRegistry();
        var lease = registry.Register(NoopAbort, "peer|1");

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Register_Socket_CloseAll_DisposesSocket()
    {
        var registry = CreateRegistry();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        registry.Register(socket, "peer|1");

        var closed = registry.CloseAll();

        Assert.Equal(1, closed);
        Assert.Equal(0, registry.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = socket.Available);
    }

    private static void NoopAbort()
    {
    }
}
