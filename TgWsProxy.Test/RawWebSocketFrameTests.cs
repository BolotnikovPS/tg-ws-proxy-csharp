#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Проверяет кадрирование WS на стороне отправки: маскирование, расширенную
/// длину и склейку пачки кадров в один write (как upstream writer.write + drain).
/// </summary>
public class RawWebSocketFrameTests
{
    private const int WsMaxFrameBytes = 16 * 1024 * 1024;

    private readonly CancellationToken cts = new CancellationTokenSource().Token;

    [Fact]
    public async Task Send_WritesSingleMaskedBinaryFrame()
    {
        var (ws, stream) = CreateSocket();
        var payload = Pattern(300);

        await ws.Send(payload, cts);

        var frames = ParseFrames(stream.ToArray());
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public async Task Send_WritesExtendedLength_ForLargePayload()
    {
        var (ws, stream) = CreateSocket();
        var payload = Pattern(70_000); // > 65535 -> 8-байтовая длина

        await ws.Send(payload, cts);

        var bytes = stream.ToArray();
        Assert.Equal(0x80 | 127, bytes[1]);
        var frames = ParseFrames(bytes);
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public async Task SendBatch_CoalescesFramesIntoSingleWrite()
    {
        var (ws, stream) = CreateSocket();
        var first = Pattern(100, seed: 1);
        var second = Pattern(200, seed: 2);

        await ws.SendBatch([first, second], cts);

        var frames = ParseFrames(stream.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Equal(first, frames[0]);
        Assert.Equal(second, frames[1]);
    }

    [Fact]
    public async Task SendBatch_AboveCoalescingThreshold_StillWritesValidFrames()
    {
        var (ws, stream) = CreateSocket();
        var parts = new List<ReadOnlyMemory<byte>>
        {
            Pattern(120_000, seed: 1),
            Pattern(120_000, seed: 2),
            Pattern(120_000, seed: 3),
        };

        await ws.SendBatch(parts, cts);

        var frames = ParseFrames(stream.ToArray());
        Assert.Equal(3, frames.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.Equal(parts[i].ToArray(), frames[i]);
        }
    }

    [Fact]
    public async Task SendBatch_WithEmptyPart_WritesZeroLengthFrame()
    {
        var (ws, stream) = CreateSocket();

        await ws.SendBatch([ReadOnlyMemory<byte>.Empty, Pattern(10, seed: 7)], cts);

        var frames = ParseFrames(stream.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Empty(frames[0]);
        Assert.Equal(Pattern(10, seed: 7), frames[1]);
    }

    [Fact]
    public async Task SendBatch_SinglePart_MatchesSend()
    {
        var (ws, stream) = CreateSocket();
        var payload = Pattern(64, seed: 9);

        await ws.SendBatch([payload], cts);

        var frames = ParseFrames(stream.ToArray());
        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    private static (RawWebSocket Ws, MemoryStream Stream) CreateSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var stream = new MemoryStream();
        var ws = new RawWebSocket(socket, stream, "test", NullLogger.Instance, WsMaxFrameBytes);
        return (ws, stream);
    }

    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31 + seed * 7) & 0xFF);
        }

        return data;
    }

    /// <summary>
    /// Разбирает поток WS-кадров и возвращает расшифрованные (снятые с маски) payload'ы.
    /// </summary>
    private static List<byte[]> ParseFrames(byte[] data)
    {
        var frames = new List<byte[]>();
        var i = 0;
        while (i < data.Length)
        {
            Assert.True((data[i] & 0x80) != 0, "FIN must be set");
            Assert.Equal(0x2, data[i] & 0x0F); // бинарный кадр
            i++;

            Assert.True((data[i] & 0x80) != 0, "client frames must be masked");
            long length = data[i] & 0x7F;
            i++;

            if (length == 126)
            {
                length = ((long)data[i] << 8) | data[i + 1];
                i += 2;
            }
            else if (length == 127)
            {
                length = 0;
                for (var b = 0; b < 8; b++)
                {
                    length = (length << 8) | data[i + b];
                }

                i += 8;
            }

            var mask = data.AsSpan(i, 4).ToArray();
            i += 4;

            var payload = new byte[length];
            for (var j = 0; j < length; j++)
            {
                payload[j] = (byte)(data[i + j] ^ mask[j & 3]);
            }

            i += (int)length;
            frames.Add(payload);
        }

        return frames;
    }
}
