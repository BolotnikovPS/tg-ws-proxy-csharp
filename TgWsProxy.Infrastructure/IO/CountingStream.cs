#nullable enable

namespace TgWsProxy.Infrastructure.IO;

/// <summary>
/// Прозрачная обёртка над <see cref="Stream"/>, считающая прочитанные и записанные байты
/// (<see cref="BytesRead"/> / <see cref="BytesWritten"/>). Не владеет внутренним потоком
/// и не закрывает его при освобождении.
/// </summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    public CountingStream(Stream inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Суммарное число байт, прочитанных из внутреннего потока.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Суммарное число байт, записанных во внутренний поток.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value >= 0)
        {
            BytesRead++;
        }
        return value;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        CountReadAsync(_inner.ReadAsync(buffer, offset, count, cancellationToken));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        CountReadAsync(_inner.ReadAsync(buffer, cancellationToken));

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        BytesWritten++;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        CountWriteAsync(_inner.WriteAsync(buffer, offset, count, cancellationToken), count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        CountWriteAsync(_inner.WriteAsync(buffer, cancellationToken), buffer.Length);

    /// <summary>Не освобождаем внутренний поток — обёртка им не владеет.</summary>
    protected override void Dispose(bool disposing)
    {
        // Намеренно пусто: CountingStream не закрывает внутренний поток.
    }

    private async Task<int> CountReadAsync(Task<int> readTask)
    {
        var read = await readTask.ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    private async ValueTask<int> CountReadAsync(ValueTask<int> readTask)
    {
        var read = await readTask.ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    private async Task CountWriteAsync(Task writeTask, int count)
    {
        await writeTask.ConfigureAwait(false);
        BytesWritten += count;
    }

    private async ValueTask CountWriteAsync(ValueTask writeTask, int count)
    {
        await writeTask.ConfigureAwait(false);
        BytesWritten += count;
    }
}
