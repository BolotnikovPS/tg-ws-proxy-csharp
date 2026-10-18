using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Exceptions;
using TgWsProxy.Infrastructure.IO;

namespace TgWsProxy.Infrastructure.Instances;

internal sealed class RawWebSocket(Socket client, Stream transport, string scope, ILogger logger, int wsMaxFrameBytes) : IRawWebSocket
{
    /// <summary>
    /// Предельный суммарный размер пачки кадров, которые склеиваются в один write.
    /// Выше этого порога кадры отправляются по одному, чтобы не арендовать
    /// крупный буфер из пула.
    /// </summary>
    private const int MaxCoalescedBatchBytes = 256 * 1024;

    private bool _closed;

    /// <summary>
    /// Переиспользуемый буфер для чтения коротких заголовков WS-фрейма (2 байта
    /// заголовка, 2/8 байт расширенной длины, 4 байта маски). Устраняет по одной
    /// аллокации массива на каждое из этих чтений в горячем пути <see cref="ReadFrame"/>.
    /// Экземпляр <see cref="RawWebSocket"/> обрабатывается строго последовательно
    /// (один читающий таск на соединение), поэтому общий буфер безопасен.
    /// </summary>
    private readonly byte[] _hdr = new byte[8];

    /// <summary>
    /// Сборщик фрагментированных WS-сообщений. Держится в поле, чтобы продолжения
    /// (continuation-кадры) корректно склеивались между вызовами <see cref="Recv"/>.
    /// </summary>
    private readonly WsBinaryMessageAssembler _assembler = new();

    /// <inheritdoc />
    public bool IsClosed => _closed;

    /// <inheritdoc />
    public bool IsRemoteClosed
    {
        get
        {
            if (_closed)
            {
                return true;
            }

            try
            {
                // Простаивающее соединение из пула не должно получать от Telegram ничего,
                // пока мы не отправим obfs-init (relay_init). Поэтому любая готовность
                // сокета к чтению означает разрыв: FIN/RST, TLS close_notify или
                // WS close-кадр — всё это уже лежит в приёмном буфере и без чтения
                // неотличимо от «данных», но в любом случае соединение непригодно.
                // Проверять только `Available == 0` нельзя: close_notify приходит
                // именно как байты, и такое соединение выдавалось клиенту как живое,
                // после чего клиент мгновенно получал обрыв (upstream-аналог
                // reader.at_eof()/reader.exception() ловит это постоянно читающим таском).
                return client.Poll(0, SelectMode.SelectRead);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public async Task Send(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
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

    public async Task SendBatch(IReadOnlyList<ReadOnlyMemory<byte>> parts, CancellationToken cancellationToken)
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
            await WriteBatch(parts, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] WS send batch failed", scope);
            throw;
        }
    }

    public async Task<byte[]?> Recv(CancellationToken cancellationToken)
    {
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

            var complete = await _assembler.OnFrame(fin, opcode, payload, cancellationToken);
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
            _assembler.Reset();
            try { await WriteMaskedFrame(0x8, ReadOnlyMemory<byte>.Empty, cancellationToken); } catch { }
            try { await transport.DisposeAsync(); } catch { }
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
        await transport.ReadExact(_hdr.AsMemory(0, 2), cancellationToken);
        var fin = (_hdr[0] & 0x80) != 0;
        var rsv = _hdr[0] & 0x70;
        var opcode = (byte)(_hdr[0] & 0x0F);
        var masked = (_hdr[1] & 0x80) != 0;
        var len = (ulong)(_hdr[1] & 0x7F);
        if (rsv != 0)
        {
            throw new IOException($"Unsupported WS RSV bits: 0x{rsv:X}");
        }

        if (len == 126)
        {
            await transport.ReadExact(_hdr.AsMemory(0, 2), cancellationToken);
            len = BinaryPrimitives.ReadUInt16BigEndian(_hdr.AsSpan(0, 2));
        }
        else if (len == 127)
        {
            await transport.ReadExact(_hdr.AsMemory(0, 8), cancellationToken);
            len = BinaryPrimitives.ReadUInt64BigEndian(_hdr.AsSpan(0, 8));
        }

        if (len > (ulong)wsMaxFrameBytes)
        {
            _closed = true;
            throw new WsFrameTooLargeException(len, wsMaxFrameBytes);
        }

        var payload = await transport.ReadExact(checked((int)len), cancellationToken);
        if (masked)
        {
            await transport.ReadExact(_hdr.AsMemory(0, 4), cancellationToken);
            XorMask(payload, _hdr.AsSpan(0, 4));
        }

        return (fin, opcode, payload);
    }

    /// <summary>
    /// Отправляет пачку кадров. При небольшом суммарном размере кадры собираются
    /// в один буфер и уходят одним write — так же, как upstream пишет все кадры в
    /// буфер writer'а и делает один drain. Для больших пачек используется
    /// покадровая отправка, чтобы не арендовать крупный буфер.
    /// </summary>
    private async Task WriteBatch(IReadOnlyList<ReadOnlyMemory<byte>> parts, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var part in parts)
        {
            total += FrameLength(part.Length);
        }

        if (total > MaxCoalescedBatchBytes)
        {
            foreach (var part in parts)
            {
                await WriteMaskedFrame(0x2, part, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)total);
        try
        {
            var offset = 0;
            foreach (var part in parts)
            {
                offset = WriteFrame(buffer, offset, 0x2, part.Span);
            }

            await transport.WriteAsync(buffer.AsMemory(0, offset), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteMaskedFrame(byte opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Proxy acts as WS client towards Telegram -> must mask.
        var frameLen = FrameLength(payload.Length);
        var rented = ArrayPool<byte>.Shared.Rent(frameLen);
        try
        {
            WriteFrame(rented, 0, opcode, payload.Span);
            await transport.WriteAsync(rented.AsMemory(0, frameLen), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Полный размер кадра с учётом 4-байтовой маски.</summary>
    /// <param name="payloadLength">Длина payload.</param>
    private static int FrameLength(int payloadLength) =>
        2 + ExtLenBytes(payloadLength) + 4 + payloadLength;

    /// <summary>Количество байт расширенной длины в заголовке кадра.</summary>
    /// <param name="payloadLength">Длина payload.</param>
    private static int ExtLenBytes(int payloadLength) =>
        payloadLength < 126 ? 0 : payloadLength < 65536 ? 2 : 8;

    /// <summary>
    /// Записывает кадр (заголовок, маска, замаскированный payload) в буфер.
    /// </summary>
    /// <param name="buffer">Буфер назначения.</param>
    /// <param name="offset">Смещение, с которого пишется кадр.</param>
    /// <param name="opcode">Опкод кадра.</param>
    /// <param name="payload">Незамаскированный payload.</param>
    /// <returns>Смещение сразу за записанным кадром.</returns>
    private static int WriteFrame(byte[] buffer, int offset, byte opcode, ReadOnlySpan<byte> payload)
    {
        var len = payload.Length;
        var extLenBytes = ExtLenBytes(len);
        var lenCode = extLenBytes switch
        {
            0 => (byte)len,
            2 => (byte)126,
            _ => (byte)127,
        };

        buffer[offset++] = (byte)(0x80 | (opcode & 0x0F)); // FIN=1, RSV=0
        buffer[offset++] = (byte)(0x80 | lenCode); // MASK=1

        if (extLenBytes == 2)
        {
            buffer[offset++] = (byte)(len >> 8);
            buffer[offset++] = (byte)len;
        }
        else if (extLenBytes == 8)
        {
            var ulen = (ulong)len;
            for (var i = 7; i >= 0; i--)
            {
                buffer[offset++] = (byte)(ulen >> (8 * i));
            }
        }

        // Маску пишем сразу в кадр: так на каждый кадр не нужен отдельный
        // 4-байтовый массив (это горячий путь, кадров много). Вся работа со
        // Span вынесена в синхронный хелпер — C# 12 запрещает Span-локали
        // в async-методах.
        return WriteMaskedPayload(buffer, offset, payload);
    }

    /// <summary>
    /// Записывает 4-байтовую маску и замаскированный payload в уже выделенный кадр.
    /// </summary>
    /// <param name="frame">Буфер кадра.</param>
    /// <param name="maskOffset">Смещение, с которого пишется маска.</param>
    /// <param name="payload">Незамаскированный payload.</param>
    /// <returns>Смещение сразу за записанным payload.</returns>
    private static int WriteMaskedPayload(byte[] frame, int maskOffset, ReadOnlySpan<byte> payload)
    {
        var mask = frame.AsSpan(maskOffset, 4);
        RandomNumberGenerator.Fill(mask);

        var length = payload.Length;
        if (length <= 0)
        {
            return maskOffset + 4;
        }

        // Копируем payload и маскируем на месте: memcpy + векторизованный XOR
        // заметно быстрее побайтового цикла с вычислением i % 4.
        var dataOffset = maskOffset + 4;
        payload.CopyTo(frame.AsSpan(dataOffset, length));
        XorMask(frame.AsSpan(dataOffset, length), mask);
        return dataOffset + length;
    }

    /// <summary>
    /// Применяет XOR-маску к спану: основной цикл векторизован по 16 байт,
    /// остаток обрабатывается побайтово.
    /// </summary>
    /// <param name="data">Буфер данных для модификации.</param>
    /// <param name="mask">4-байтовая маска WebSocket.</param>
    private static void XorMask(Span<byte> data, ReadOnlySpan<byte> mask)
    {
        var i = 0;
        if (Vector128.IsHardwareAccelerated && data.Length >= Vector128<byte>.Count)
        {
            var pattern = Vector128.Create(
                mask[0], mask[1], mask[2], mask[3],
                mask[0], mask[1], mask[2], mask[3],
                mask[0], mask[1], mask[2], mask[3],
                mask[0], mask[1], mask[2], mask[3]);

            var limit = data.Length - (data.Length % Vector128<byte>.Count);
            for (; i < limit; i += Vector128<byte>.Count)
            {
                var block = Vector128.LoadUnsafe(
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(
                        data.Slice(i, Vector128<byte>.Count)));
                (block ^ pattern).CopyTo(data.Slice(i, Vector128<byte>.Count));
            }
        }

        for (; i < data.Length; i++)
        {
            data[i] ^= mask[i & 3];
        }
    }
}
