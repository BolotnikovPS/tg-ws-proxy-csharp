#nullable enable

using System.Buffers.Binary;

namespace TgWsProxy.Infrastructure.FakeTls;

/// <summary>
/// Поток поверх TCP, который прозрачно разбирает и формирует TLS-записи (порт upstream
/// <c>FakeTlsStream</c>): чтение отдаёт полезную нагрузку записей application data,
/// запись оборачивает данные в такие записи.
/// </summary>
internal sealed class FakeTlsStream(Stream inner) : Stream
{
    /// <summary>Заголовок TLS-записи: тип + версия + длина.</summary>
    private readonly byte[] header = new byte[5];

    /// <summary>Остаток полезной нагрузки текущей записи, ещё не прочитанный из сокета.</summary>
    private int recordLeft;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        return await ReadTlsPayloadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => WriteCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => new(WriteCoreAsync(buffer, cancellationToken));

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken);

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Оборачивает данные в TLS-записи и отправляет во внутренний поток.
    /// </summary>
    private async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length == 0)
        {
            return;
        }

        var wrapped = FakeTlsCodec.WrapTlsRecord(data.Span);
        await inner.WriteAsync(wrapped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Возвращает очередную порцию полезной нагрузки application data, пропуская CCS-записи.
    /// </summary>
    /// <returns>Количество прочитанных байт; 0 — поток закрыт или встречена неожидаемая запись.</returns>
    private async ValueTask<int> ReadTlsPayloadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (recordLeft > 0)
        {
            var toRead = Math.Min(recordLeft, buffer.Length);
            var read = await inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }

            recordLeft -= read;
            return read;
        }

        while (true)
        {
            await inner.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var recordType = header[0];
            var recordLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3, 2));

            if (recordType == FakeTlsCodec.RecordChangeCipherSpec)
            {
                if (recordLength > 0)
                {
                    await inner.ReadExactlyAsync(new byte[recordLength], cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            // Всё, что не application data (например, alert или close_notify), завершает поток.
            if (recordType != FakeTlsCodec.RecordApplicationData)
            {
                return 0;
            }

            var toRead = Math.Min(recordLength, buffer.Length);
            var read = await inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }

            recordLeft = recordLength - read;
            return read;
        }
    }
}
