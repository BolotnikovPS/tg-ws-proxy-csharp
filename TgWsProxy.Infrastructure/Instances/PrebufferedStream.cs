namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Обёртка над потоком, отдающая сначала уже прочитанный «префикс» байтов, а затем данные
/// внутреннего потока. Нужна, чтобы байты WebSocket-фреймов, случайно попавшие в буфер
/// вместе с HTTP-заголовками рукопожатия, не потерялись.
/// </summary>
internal sealed class PrebufferedStream(Stream inner, byte[] prefix) : Stream
{
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var served = ServePrefix(buffer.AsSpan(offset, count));
        return served > 0 ? served : inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var served = ServePrefix(buffer.Span);
        return served > 0
            ? served
            : await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Отдаёт очередную порцию префикса.
    /// </summary>
    /// <returns>Число скопированных байтов (0, если префикс исчерпан).</returns>
    private int ServePrefix(Span<byte> destination)
    {
        var remaining = prefix.Length - _offset;
        if (remaining <= 0 || destination.Length == 0)
        {
            return 0;
        }

        var take = Math.Min(remaining, destination.Length);
        prefix.AsSpan(_offset, take).CopyTo(destination);
        _offset += take;
        return take;
    }
}
