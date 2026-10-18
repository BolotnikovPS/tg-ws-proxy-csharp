using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="RelayMultiplexer"/>: диспетчеризация кадров Open/Data/Window/Close/Ping/Hello,
/// жизненный цикл мостов, очистка потоков и ожидание мостов при dispose.
/// </summary>
public class RelayMultiplexerTests
{
    [Fact]
    public async Task Open_StartsBridgeAndRegistersStream()
    {
        RelayStream? bridged = null;
        var mux = new RelayMultiplexer(new CapturingSink(), (s, _) =>
        {
            bridged = s;
            return Task.CompletedTask;
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 5, ReadOnlyMemory<byte>.Empty), default);

        Assert.NotNull(bridged);
        Assert.Equal(5, bridged!.StreamId);
        Assert.Equal(1, mux.ActiveStreamCount);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateOpen_IsIgnored()
    {
        var opens = 0;
        var mux = new RelayMultiplexer(new CapturingSink(), (_, _) =>
        {
            Interlocked.Increment(ref opens);
            return Task.CompletedTask;
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 1, ReadOnlyMemory<byte>.Empty), default);
        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 1, ReadOnlyMemory<byte>.Empty), default);

        Assert.Equal(1, opens);
        Assert.Equal(1, mux.ActiveStreamCount);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Data_IsEnqueuedToStream()
    {
        RelayStream? bridged = null;
        var mux = new RelayMultiplexer(new CapturingSink(), (s, _) =>
        {
            bridged = s;
            return Task.CompletedTask;
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 9, ReadOnlyMemory<byte>.Empty), default);
        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Data, 9, new byte[] { 9, 9, 9 }), default);

        var buffer = new byte[3];
        var n = await bridged!.ReadAsync(buffer);

        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 9, 9, 9 }, buffer);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Data_OnUnknownStream_IsIgnored()
    {
        var sink = new CapturingSink();
        var mux = new RelayMultiplexer(sink);

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Data, 99, new byte[] { 1, 2 }), default);

        Assert.Empty(sink.Frames);
        Assert.Equal(0, mux.ActiveStreamCount);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Window_AddsSendCredit()
    {
        RelayStream? bridged = null;
        var mux = new RelayMultiplexer(new CapturingSink(), (s, _) =>
        {
            bridged = s;
            return Task.CompletedTask;
        }, initialSendWindow: 4);

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 3, ReadOnlyMemory<byte>.Empty), default);

        var writeTask = bridged!.WriteAsync(new byte[8]).AsTask();
        await Task.Delay(100);
        Assert.False(writeTask.IsCompleted);

        var win = new byte[RelayFrameCodec.WindowCreditSize];
        RelayFrameCodec.WriteWindowCredit(win, 4);
        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Window, 3, win), default);

        await writeTask;
        Assert.True(writeTask.IsCompleted);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Close_CompletesStreamAndRemoves()
    {
        RelayStream? bridged = null;
        var mux = new RelayMultiplexer(new CapturingSink(), (s, _) =>
        {
            bridged = s;
            return Task.CompletedTask;
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 3, ReadOnlyMemory<byte>.Empty), default);
        Assert.Equal(1, mux.ActiveStreamCount);

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Close, 3, ReadOnlyMemory<byte>.Empty), default);

        Assert.Equal(0, mux.ActiveStreamCount);
        Assert.Equal(0, await bridged!.ReadAsync(new byte[1]));
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Ping_IsIgnored()
    {
        var sink = new CapturingSink();
        var mux = new RelayMultiplexer(sink);

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty), default);

        Assert.Empty(sink.Frames);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Hello_ValidPayload_RepliesWithWelcome()
    {
        var sink = new CapturingSink();
        var mux = new RelayMultiplexer(sink);

        await mux.HandleInboundFrameAsync(
            MakeFrame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion }),
            default);

        Assert.Contains(sink.Frames, f => f.Type == RelayFrameType.Welcome);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Hello_InvalidPayload_RepliesWithBye()
    {
        var sink = new CapturingSink();
        var mux = new RelayMultiplexer(sink);

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Hello, 0, ReadOnlyMemory<byte>.Empty), default);

        Assert.DoesNotContain(sink.Frames, f => f.Type == RelayFrameType.Welcome);
        Assert.Contains(sink.Frames, f => f.Type == RelayFrameType.Bye);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Hello_NonZeroStream_RepliesWithBye()
    {
        var sink = new CapturingSink();
        var mux = new RelayMultiplexer(sink);

        await mux.HandleInboundFrameAsync(
            MakeFrame(RelayFrameType.Hello, 7, new byte[] { RelayFrameCodec.HelloProtocolVersion }),
            default);

        Assert.DoesNotContain(sink.Frames, f => f.Type == RelayFrameType.Welcome);
        Assert.Contains(sink.Frames, f => f.Type == RelayFrameType.Bye);
        await mux.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesOpenStreams()
    {
        RelayStream? bridged = null;
        var mux = new RelayMultiplexer(new CapturingSink(), (s, _) =>
        {
            bridged = s;
            return Task.CompletedTask;
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 1, ReadOnlyMemory<byte>.Empty), default);
        await mux.DisposeAsync();

        Assert.Equal(0, mux.ActiveStreamCount);
        Assert.Equal(0, await bridged!.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task DisposeAsync_AwaitsRunningBridge()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridgeFinished = false;
        var mux = new RelayMultiplexer(new CapturingSink(), async (_, _) =>
        {
            try
            {
                await release.Task;
            }
            finally
            {
                bridgeFinished = true;
            }
        });

        await mux.HandleInboundFrameAsync(MakeFrame(RelayFrameType.Open, 1, ReadOnlyMemory<byte>.Empty), default);

        var disposeTask = mux.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted);

        release.SetResult();
        await disposeTask;

        Assert.True(bridgeFinished);
    }

    private static RelayFrame MakeFrame(RelayFrameType type, int streamId, ReadOnlyMemory<byte> payload)
    {
        var buffer = new byte[RelayFrameCodec.FrameSize(payload.Length)];
        RelayFrameCodec.Write(buffer, type, streamId, payload.Span);
        RelayFrameCodec.TryParse(buffer, out var frame, out _);
        return frame;
    }

    private sealed class CapturingSink : IRelayFrameSink
    {
        public List<RelayFrame> Frames { get; } = new();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken ct)
        {
            RelayFrameCodec.TryParse(frameBytes, out var frame, out _);
            lock (Frames)
            {
                Frames.Add(new RelayFrame(frame.Type, frame.StreamId, frame.Payload.ToArray()));
            }

            return ValueTask.CompletedTask;
        }
    }
}
