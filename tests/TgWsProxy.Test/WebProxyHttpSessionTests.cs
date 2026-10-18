using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="WebProxyHttpSession"/>: последовательность/повтор восходящих батчей
/// (<c>POST /up</c>), доставка/повтор нисходящих батчей (<c>POST /down</c>), валидация курсора
/// и закрытие сессии.
/// </summary>
public class WebProxyHttpSessionTests
{
    [Fact]
    public async Task ProcessUp_AcceptsFirstSequence()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        var result = await session.ProcessUpAsync(1, hello, default);

        Assert.True(result.Accepted);
        Assert.Equal(1, result.Ack);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProcessUp_RejectsGapInSequence()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        await session.ProcessUpAsync(1, hello, default);
        var gap = await session.ProcessUpAsync(3, hello, default);

        Assert.False(gap.Accepted);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProcessUp_RejectsSeqZero()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        var result = await session.ProcessUpAsync(0, hello, default);

        Assert.False(result.Accepted);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProcessUp_RepeatsAcceptedBatchByIdentity()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        await session.ProcessUpAsync(1, hello, default);
        var replay = await session.ProcessUpAsync(1, hello, default);

        Assert.True(replay.Accepted);
        Assert.Equal(1, replay.Ack);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProcessUp_RejectsSameSeqWithDifferentBody()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });
        var other = Frame(RelayFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty);

        await session.ProcessUpAsync(1, hello, default);
        var different = await session.ProcessUpAsync(1, other, default);

        Assert.False(different.Accepted);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProcessUp_RejectsEmptyDataFrame()
    {
        var session = CreateSession();
        var emptyData = Frame(RelayFrameType.Data, 1, ReadOnlyMemory<byte>.Empty);

        var result = await session.ProcessUpAsync(1, emptyData, default);

        Assert.False(result.Accepted);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Poll_DeliversWelcomeAfterHello()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        await session.ProcessUpAsync(1, hello, default);
        var down = await session.PollAsync(0, default);

        Assert.NotNull(down.Batch);
        Assert.False(down.Closed);
        Assert.Equal(1, down.Cursor);
        Assert.True(RelayFrameCodec.TryParse(down.Batch!, out var frame, out _));
        Assert.Equal(RelayFrameType.Welcome, frame.Type);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Poll_ReplaysLastBatchOnUnconfirmedCursor()
    {
        var session = CreateSession();
        var hello = Frame(RelayFrameType.Hello, 0, new byte[] { RelayFrameCodec.HelloProtocolVersion });

        await session.ProcessUpAsync(1, hello, default);
        var first = await session.PollAsync(0, default);
        var replay = await session.PollAsync(0, default);

        Assert.NotNull(replay.Batch);
        // По неподтверждённому курсору отдаётся тот же батч (байт-идентичный повтор).
        Assert.Equal(first.Batch, replay.Batch);
        Assert.Equal(1, replay.Cursor);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Poll_InvalidCursor_Throws()
    {
        var session = CreateSession();

        await Assert.ThrowsAsync<InvalidDataException>(() => session.PollAsync(42, default).AsTask());

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Poll_WhenCancelled_ReturnsEmpty()
    {
        var session = CreateSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // отмена до парковки — poll сразу получает 204-эквивалент

        var down = await session.PollAsync(0, cts.Token);

        Assert.Null(down.Batch);
        Assert.False(down.Closed);
        Assert.Equal(0, down.Cursor);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Close_WakesParkedPollWithClosed()
    {
        var session = CreateSession();
        var pollTask = session.PollAsync(0, default).AsTask();

        session.Close();

        var down = await pollTask;
        Assert.True(down.Closed);
        Assert.Null(down.Batch);
        await session.DisposeAsync();
    }

    [Fact]
    public void IsClosed_ReflectsClose()
    {
        var session = CreateSession();

        Assert.False(session.IsClosed);
        session.Close();
        Assert.True(session.IsClosed);
    }

    private static WebProxyHttpSession CreateSession()
        => new((_, _) => Task.CompletedTask);

    private static byte[] Frame(RelayFrameType type, int streamId, ReadOnlyMemory<byte> payload)
    {
        var buffer = new byte[RelayFrameCodec.FrameSize(payload.Length)];
        RelayFrameCodec.Write(buffer, type, streamId, payload.Span);
        return buffer;
    }
}
