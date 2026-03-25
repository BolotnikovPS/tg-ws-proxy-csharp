namespace TgWsProxy.Domain.Exceptions;

public sealed class WsFrameTooLargeException(ulong framePayloadLen, int maxFramePayloadLen)
    : IOException($"WS frame payload too large: {framePayloadLen} bytes (max {maxFramePayloadLen})")
{
    public ulong FramePayloadLen { get; } = framePayloadLen;
    public int MaxFramePayloadLen { get; } = maxFramePayloadLen;
}