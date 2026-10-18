using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты кодека relay-frame WEB proxy: заголовок 8 байт [type:1][streamId:3 BE][length:4 BE],
/// границы streamId/length, отказ при превышении лимита payload и повреждённом заголовке.
/// </summary>
public class RelayFrameCodecTests
{
    [Fact]
    public void WriteHeader_ProducesBigEndianLayout()
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize];

        RelayFrameCodec.WriteHeader(buffer, RelayFrameType.Data, 0x010203, 0x04050607);

        Assert.Equal((byte)RelayFrameType.Data, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
        Assert.Equal(0x02, buffer[2]);
        Assert.Equal(0x03, buffer[3]);
        // length — 4 байта big-endian
        Assert.Equal(0x04, buffer[4]);
        Assert.Equal(0x05, buffer[5]);
        Assert.Equal(0x06, buffer[6]);
        Assert.Equal(0x07, buffer[7]);
    }

    [Fact]
    public void TryParse_Roundtrip_ReturnsPayloadSlice()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize + payload.Length];
        RelayFrameCodec.Write(buffer, RelayFrameType.Data, 7, payload);

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.True(ok);
        Assert.Equal(RelayFrameError.None, error);
        Assert.Equal(RelayFrameType.Data, frame.Type);
        Assert.Equal(7, frame.StreamId);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RelayFrameCodec.MaxStreamId)]
    public void TryParse_StreamIdBoundary_Accepted(int streamId)
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize];
        RelayFrameCodec.WriteHeader(buffer, RelayFrameType.Open, streamId, 0);

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.True(ok);
        Assert.Equal(RelayFrameError.None, error);
        Assert.Equal(streamId, frame.StreamId);
    }

    [Fact]
    public void TryParse_PayloadTooLarge_Rejected()
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize];
        // Объявленная длина превышает kMaxFramePayload; буфер при этом не обязан её содержать.
        RelayFrameCodec.WriteHeader(buffer, RelayFrameType.Data, 1, RelayFrameCodec.MaxFramePayload + 1);

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.False(ok);
        Assert.Equal(RelayFrameError.PayloadTooLarge, error);
        Assert.Equal(default, frame);
    }

    [Fact]
    public void TryParse_TruncatedPayload_Rejected()
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize];
        RelayFrameCodec.WriteHeader(buffer, RelayFrameType.Data, 1, 16);

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.False(ok);
        Assert.Equal(RelayFrameError.HeaderTooShort, error);
    }

    [Fact]
    public void TryParse_ShorterThanHeader_Rejected()
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize - 1];

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.False(ok);
        Assert.Equal(RelayFrameError.HeaderTooShort, error);
    }

    [Fact]
    public void TryParse_UnknownType_Rejected()
    {
        var buffer = new byte[RelayFrameCodec.FrameHeaderSize];
        buffer[0] = 0x7F;

        var ok = RelayFrameCodec.TryParse(buffer, out var frame, out var error);

        Assert.False(ok);
        Assert.Equal(RelayFrameError.UnknownType, error);
    }

    [Fact]
    public void FrameSize_AddsHeaderToPayloadLength()
    {
        Assert.Equal(RelayFrameCodec.FrameHeaderSize, RelayFrameCodec.FrameSize(0));
        Assert.Equal(RelayFrameCodec.FrameHeaderSize + 100, RelayFrameCodec.FrameSize(100));
    }
}
