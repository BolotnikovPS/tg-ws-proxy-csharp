using System.Threading.Channels;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Один мультиплексированный поток relay-протокола как <see cref="Stream"/>: чтение возвращает
/// payload кадров <c>Data</c> (клиент → сервер), запись отправляет <c>Data</c> (сервер → клиент)
/// с соблюдением flow-control. Создаётся <see cref="RelayMultiplexer"/> по кадру <c>Open</c>.
/// </summary>
public sealed class RelayStream : Stream
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly RelayFrameSender _sender;
    private readonly SendWindowGate _sendWindow;
    private readonly byte[] _windowCreditBuffer = new byte[RelayFrameCodec.WindowCreditSize];
    private ReadOnlyMemory<byte> _current;
    private int _disposed;

    internal RelayStream(int streamId, RelayFrameSender sender, long initialSendWindow = RelayFrameCodec.InitialStreamWindow)
    {
        StreamId = streamId;
        _sender = sender;
        _sendWindow = new SendWindowGate(initialSendWindow);
    }

    /// <summary>Идентификатор потока (24-битное поле заголовка).</summary>
    public int StreamId { get; }

    /// <summary>Обратный вызов удаления потока из таблицы мультиплексора.</summary>
    internal Action<RelayStream>? OnDisposed { get; set; }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    /// <summary>Помещает payload кадра <c>Data</c> во входящую очередь (вызывается мультиплексором).</summary>
    internal void EnqueueData(ReadOnlyMemory<byte> payload) => _inbound.Writer.TryWrite(payload.ToArray());

    /// <summary>Завершает входящую очередь (кадр <c>Close</c>/<c>Bye</c> или разрыв соединения).</summary>
    internal void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <summary>Возвращает кредит окна отправки (кадр <c>Window</c> от противоположной стороны).</summary>
    internal void AddSendCredit(int credit) => _sendWindow.Add(credit);

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        while (_current.IsEmpty)
        {
            if (!await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                return 0; // EOF
            }

            if (_inbound.Reader.TryRead(out var payload))
            {
                _current = payload;
            }
        }

        var count = Math.Min(_current.Length, destination.Length);
        _current.Span[..count].CopyTo(destination.Span);
        _current = _current[count..];

        // Вернуть клиенту кредит приёмного окна: сервер потребил count байт.
        RelayFrameCodec.WriteWindowCredit(_windowCreditBuffer, count);
        await _sender(RelayFrameType.Window, StreamId, _windowCreditBuffer, ct).ConfigureAwait(false);
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        var remaining = source;
        while (!remaining.IsEmpty)
        {
            var credit = await _sendWindow.WaitAsync(ct).ConfigureAwait(false);
            var chunk = Math.Min(remaining.Length, (int)Math.Min(credit, RelayFrameCodec.MaxFramePayload));
            await _sender(RelayFrameType.Data, StreamId, remaining[..chunk], ct).ConfigureAwait(false);
            _sendWindow.Consume(chunk);
            remaining = remaining[chunk..];
        }
    }

    public override Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _inbound.Writer.TryComplete();
        try
        {
            await _sender(RelayFrameType.Close, StreamId, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Транспорт уже закрыт — Close не критичен.
        }

        OnDisposed?.Invoke(this);
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
