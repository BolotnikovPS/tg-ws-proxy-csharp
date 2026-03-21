#nullable enable

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure;

internal sealed class RawWebSocket(TcpClient client, SslStream ssl, string scope, ILogger logger) : IRawWebSocket
{
    private bool _closed;

    public async Task Send(byte[] data)
    {
        if (_closed)
        {
            throw new IOException("WebSocket closed");
        }

        try
        {
            await ssl.WriteAsync(BuildFrame(0x2, data, true));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] WS send failed", scope);
            throw;
        }
    }

    public async Task<byte[]?> Recv()
    {
        while (!_closed)
        {
            (byte opcode, byte[] payload) frame;
            try
            {
                frame = await ReadFrame();
            }
            catch (EndOfStreamException)
            {
                _closed = true;
                logger.LogDebug("[{Scope}] WS read ended (peer closed TCP without WS close frame)", scope);
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Scope}] WS read frame failed", scope);
                throw;
            }
            var (opcode, payload) = frame;
            if (opcode == 0x8)
            {
                _closed = true;
                return null;
            }
            if (opcode == 0x9)
            {
                await ssl.WriteAsync(BuildFrame(0xA, payload, true));
                continue;
            }
            if (opcode == 0xA)
            {
                continue;
            }

            if (opcode is 0x1 or 0x2)
            {
                return payload;
            }
        }
        return null;
    }

    public async Task Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try { await ssl.WriteAsync(BuildFrame(0x8, [], true)); } catch { }
        try { ssl.Dispose(); } catch { }
        try { client.Close(); } catch { }
    }

    /// <summary>
    /// Считывает следующий WebSocket-фрейм и возвращает его opcode и полезную нагрузку.
    /// </summary>
    private async Task<(byte Opcode, byte[] Payload)> ReadFrame()
    {
        var hdr = await IoUtil.ReadExact(ssl, 2);
        var opcode = (byte)(hdr[0] & 0x0F);
        var masked = (hdr[1] & 0x80) != 0;
        var len = (ulong)(hdr[1] & 0x7F);
        if (len == 126)
        {
            len = BinaryPrimitives.ReadUInt16BigEndian(await IoUtil.ReadExact(ssl, 2));
        }
        else if (len == 127)
        {
            len = BinaryPrimitives.ReadUInt64BigEndian(await IoUtil.ReadExact(ssl, 8));
        }

        var mask = masked ? await IoUtil.ReadExact(ssl, 4) : null;
        var payload = await IoUtil.ReadExact(ssl, checked((int)len));
        if (mask is not null)
        {
            XorMask(payload, mask);
        }

        return (opcode, payload);
    }

    /// <summary>
    /// Формирует бинарный WebSocket-фрейм с опциональной маскировкой payload.
    /// </summary>
    private static byte[] BuildFrame(byte opcode, byte[] payload, bool mask)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode));
        var len = payload.Length;
        var maskBit = mask ? 0x80 : 0;
        if (len < 126)
        {
            ms.WriteByte((byte)(maskBit | len));
        }
        else if (len < 65536)
        {
            ms.WriteByte((byte)(maskBit | 126));
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)len);
            ms.Write(b);
        }
        else
        {
            ms.WriteByte((byte)(maskBit | 127));
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)len);
            ms.Write(b);
        }

        if (!mask)
        {
            ms.Write(payload);
            return ms.ToArray();
        }

        var maskKey = RandomNumberGenerator.GetBytes(4);
        ms.Write(maskKey);
        var masked = payload.ToArray();
        XorMask(masked, maskKey);
        ms.Write(masked);
        return ms.ToArray();
    }

    /// <summary>
    /// Применяет XOR-маску к буферу данных по циклическому ключу длиной 4 байта.
    /// </summary>
    private static void XorMask(byte[] data, byte[] mask)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= mask[i % 4];
        }
    }

    /// <summary>
    /// Считывает одну CRLF-строку из потока без символов окончания строки.
    /// </summary>
    private static async Task<string> ReadLine(Stream s)
    {
        var b = new List<byte>();
        while (true)
        {
            var one = await IoUtil.ReadExact(s, 1);
            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] != '\r')
            {
                b.Add(one[0]);
            }
        }
        return Encoding.ASCII.GetString([.. b]);
    }
}
