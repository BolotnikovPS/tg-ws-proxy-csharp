#nullable enable

using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class RawWebSocket(Socket client, SslStream ssl, string scope, ILogger logger, int wsMaxFrameBytes) : IRawWebSocket
{
    private bool _closed;

    public async Task Send(byte[] data, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            throw new IOException("WebSocket closed");
        }

        try
        {
            await WriteMaskedFrame(0x2, data, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] WS send failed", scope);
            throw;
        }
    }

    public async Task SendBatch(IReadOnlyList<byte[]> parts, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            throw new IOException("WebSocket closed");
        }

        if (parts.Count == 0)
        {
            return;
        }

        if (parts.Count == 1)
        {
            await Send(parts[0], cancellationToken);
            return;
        }

        try
        {
            foreach (var p in parts)
            {
                await WriteMaskedFrame(0x2, p, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] WS send batch failed", scope);
            throw;
        }
    }

    public async Task<byte[]?> Recv(CancellationToken cancellationToken)
    {
        var assembler = new WsBinaryMessageAssembler();

        while (!_closed)
        {
            (bool fin, byte opcode, byte[] payload) frame;
            try
            {
                frame = await ReadFrame(cancellationToken);
            }
            catch (EndOfStreamException)
            {
                _closed = true;
                logger.LogDebug("[{Scope}] WS read ended (peer closed TCP without WS close frame)", scope);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Scope}] WS read frame failed", scope);
                throw;
            }

            var (fin, opcode, payload) = frame;
            if (opcode == 0x8)
            {
                _closed = true;
                return null;
            }
            if (opcode == 0x9)
            {
                await WriteMaskedFrame(0xA, payload, cancellationToken);
                continue;
            }
            if (opcode == 0xA)
            {
                continue;
            }

            var complete = await assembler.OnFrame(fin, opcode, payload, cancellationToken);
            if (complete is not null)
            {
                return complete;
            }
        }

        return null;
    }

    public async Task Close(CancellationToken cancellationToken)
    {
        try
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try { await WriteMaskedFrame(0x8, [], cancellationToken); } catch { }
            try { await ssl.DisposeAsync(); } catch { }
            try { client.Close(); } catch { }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Failed to close WS connection", scope);
        }
    }

    /// <summary>
    /// Считывает следующий WebSocket-фрейм и возвращает его opcode и полезную нагрузку.
    /// </summary>
    /// <returns>Кортеж из opcode и payload прочитанного фрейма.</returns>
    private async Task<(bool Fin, byte Opcode, byte[] Payload)> ReadFrame(CancellationToken cancellationToken)
    {
        var hdr = await ssl.ReadExact(2, cancellationToken);
        var fin = (hdr[0] & 0x80) != 0;
        var rsv = hdr[0] & 0x70;
        var opcode = (byte)(hdr[0] & 0x0F);
        var masked = (hdr[1] & 0x80) != 0;
        var len = (ulong)(hdr[1] & 0x7F);
        if (rsv != 0)
        {
            throw new IOException($"Unsupported WS RSV bits: 0x{rsv:X}");
        }

        if (len == 126)
        {
            len = BinaryPrimitives.ReadUInt16BigEndian(await ssl.ReadExact(2, cancellationToken));
        }
        else if (len == 127)
        {
            len = BinaryPrimitives.ReadUInt64BigEndian(await ssl.ReadExact(8, cancellationToken));
        }

        if (len > (ulong)wsMaxFrameBytes)
        {
            _closed = true;
            throw new WsFrameTooLargeException(len, wsMaxFrameBytes);
        }

        var mask = masked ? await ssl.ReadExact(4, cancellationToken) : null;
        var payload = await ssl.ReadExact(checked((int)len), cancellationToken);
        if (mask is not null)
        {
            XorMask(payload, mask);
        }

        return (fin, opcode, payload);
    }

    private async Task WriteMaskedFrame(byte opcode, byte[] payload, CancellationToken cancellationToken)
    {
        // Proxy acts as WS client towards Telegram -> must mask.
        var len = payload.Length;

        byte lenCode;
        int extLenBytes;
        if (len < 126)
        {
            lenCode = (byte)len;
            extLenBytes = 0;
        }
        else if (len < 65536)
        {
            lenCode = 126;
            extLenBytes = 2;
        }
        else
        {
            lenCode = 127;
            extLenBytes = 8;
        }

        // 2 base bytes + optional extended length + 4-byte mask key + payload
        var frameLen = 2 + extLenBytes + 4 + len;
        var rented = ArrayPool<byte>.Shared.Rent(frameLen);
        try
        {
            rented[0] = (byte)(0x80 | (opcode & 0x0F)); // FIN=1, RSV=0
            rented[1] = (byte)(0x80 | lenCode); // MASK=1

            var idx = 2;
            if (extLenBytes == 2)
            {
                rented[idx++] = (byte)(len >> 8);
                rented[idx++] = (byte)len;
            }
            if (extLenBytes == 8)
            {
                var ulen = (ulong)len;
                for (var i = 7; i >= 0; i--)
                {
                    rented[idx++] = (byte)(ulen >> (8 * i));
                }
            }

            var maskKey = new byte[4];
            RandomNumberGenerator.Fill(maskKey);
            rented[idx++] = maskKey[0];
            rented[idx++] = maskKey[1];
            rented[idx++] = maskKey[2];
            rented[idx++] = maskKey[3];

            // Write masked payload directly (no extra masked buffer allocations).
            for (var i = 0; i < len; i++)
            {
                rented[idx + i] = (byte)(payload[i] ^ maskKey[i & 3]);
            }

            await ssl.WriteAsync(rented.AsMemory(0, frameLen), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Применяет XOR-маску к буферу данных по циклическому ключу длиной 4 байта.
    /// </summary>
    /// <param name="data">Буфер данных для модификации.</param>
    /// <param name="mask">4-байтовая маска WebSocket.</param>
    private static void XorMask(byte[] data, byte[] mask)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= mask[i % 4];
        }
    }
}
