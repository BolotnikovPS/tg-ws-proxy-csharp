using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="RelayStream"/>: чтение payload кадров Data (с возвратом кредита Window),
/// запись Data с учётом flow-control, разбиение по kMaxFramePayload, EOF и Close при dispose.
/// </summary>
public class RelayStreamTests
{
    [Fact]
    public async Task ReadAsync_ReturnsEnqueuedPayload()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(1, frames);
        stream.EnqueueData(new byte[] { 1, 2, 3, 4, 5 });

        var buffer = new byte[5];
        var n = await stream.ReadAsync(buffer);

        Assert.Equal(5, n);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer);

        var window = Assert.Single(frames, f => f.Type == RelayFrameType.Window);
        Assert.Equal(1, window.StreamId);
        Assert.Equal(5, RelayFrameCodec.ParseWindowCredit(window.Payload.Span));
    }

    [Fact]
    public async Task ReadAsync_ServesPartialReads()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(1, frames);
        stream.EnqueueData(new byte[] { 1, 2, 3, 4, 5 });

        var b1 = new byte[2];
        var n1 = await stream.ReadAsync(b1);
        var b2 = new byte[10];
        var n2 = await stream.ReadAsync(b2);

        Assert.Equal(2, n1);
        Assert.Equal(new byte[] { 1, 2 }, b1);
        Assert.Equal(3, n2);
        Assert.Equal(new byte[] { 3, 4, 5 }, b2[..3]);

        var credits = frames
            .Where(f => f.Type == RelayFrameType.Window)
            .Select(f => RelayFrameCodec.ParseWindowCredit(f.Payload.Span))
            .ToArray();
        Assert.Equal(new[] { 2, 3 }, credits);
    }

    [Fact]
    public async Task ReadAsync_ReturnsZeroOnComplete()
    {
        var stream = NewStream(1, new List<RelayFrame>());
        stream.CompleteInbound();

        var n = await stream.ReadAsync(new byte[10]);

        Assert.Equal(0, n);
    }

    [Fact]
    public async Task WriteAsync_SendsDataFrame()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(1, frames);

        await stream.WriteAsync(new byte[] { 1, 2, 3 });

        var data = Assert.Single(frames, f => f.Type == RelayFrameType.Data);
        Assert.Equal(1, data.StreamId);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Payload.ToArray());
    }

    [Fact]
    public async Task WriteAsync_RespectsSendWindow()
    {
        var frames = new List<RelayFrame>();
        var stream = new RelayStream(1, CapturingSender(frames), initialSendWindow: 8);

        var writeTask = stream.WriteAsync(new byte[16]).AsTask();
        await Task.Delay(100);
        Assert.False(writeTask.IsCompleted);

        stream.AddSendCredit(8);
        await writeTask;

        Assert.True(writeTask.IsCompleted);
        var dataFrames = frames.Where(f => f.Type == RelayFrameType.Data).ToArray();
        Assert.Equal(2, dataFrames.Length);
        Assert.All(dataFrames, f => Assert.Equal(8, f.Payload.Length));
    }

    [Fact]
    public async Task WriteAsync_SplitsIntoMaxFramePayloadChunks()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(1, frames);
        var size = RelayFrameCodec.MaxFramePayload + 100;

        await stream.WriteAsync(new byte[size]);

        var dataFrames = frames.Where(f => f.Type == RelayFrameType.Data).ToArray();
        Assert.Equal(2, dataFrames.Length);
        Assert.Equal(RelayFrameCodec.MaxFramePayload, dataFrames[0].Payload.Length);
        Assert.Equal(100, dataFrames[1].Payload.Length);
    }

    [Fact]
    public async Task DisposeAsync_SendsCloseFrame()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(7, frames);

        await stream.DisposeAsync();

        Assert.Contains(frames, f => f.Type == RelayFrameType.Close && f.StreamId == 7);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var frames = new List<RelayFrame>();
        var stream = NewStream(1, frames);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Single(frames, f => f.Type == RelayFrameType.Close);
    }

    private static RelayStream NewStream(int streamId, List<RelayFrame> frames) => new(streamId, CapturingSender(frames));

    private static RelayFrameSender CapturingSender(List<RelayFrame> frames) =>
        (type, streamId, payload, ct) =>
        {
            lock (frames)
            {
                frames.Add(new RelayFrame(type, streamId, payload.ToArray()));
            }

            return ValueTask.CompletedTask;
        };
}
