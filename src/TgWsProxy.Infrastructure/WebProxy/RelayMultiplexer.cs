using System.Buffers;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Мультиплексор relay-протокола: диспетчеризует входящие кадры по потокам, ведёт таблицу
/// потоков, запускает мост для каждого открытого потока и сериализует исходящие кадры через
/// <see cref="IRelayFrameSink"/> (аренда буфера из <see cref="ArrayPool{T}"/>, без копирования
/// в исходящий буфер).
/// </summary>
public sealed class RelayMultiplexer : IAsyncDisposable
{
    private readonly IRelayFrameSink _sink;
    private readonly Func<RelayStream, CancellationToken, Task>? _startBridge;
    private readonly long _initialSendWindow;
    private readonly Dictionary<int, RelayStream> _streams = new();
    private readonly List<Task> _bridges = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly object _sync = new();
    private int _closed;

    public RelayMultiplexer(
        IRelayFrameSink sink,
        Func<RelayStream, CancellationToken, Task>? startBridge = null,
        long initialSendWindow = RelayFrameCodec.InitialStreamWindow)
    {
        _sink = sink;
        _startBridge = startBridge;
        _initialSendWindow = initialSendWindow;
    }

    /// <summary>Число активных потоков (для тестов и диагностики).</summary>
    public int ActiveStreamCount
    {
        get
        {
            lock (_sync)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>
    /// Обрабатывает один разобранный входящий кадр. Вызывается inbound-циклом по одному кадру
    /// за раз. <c>Open</c> создаёт поток и запускает мост; <c>Data</c>/<c>Window</c>/<c>Close</c>
    /// маршрутизируются в поток; входящий <c>Ping</c> игнорируется (PING — r→c); <c>Hello</c>
    /// валидируется (stream0 + payload=<see cref="RelayFrameCodec.HelloProtocolVersion"/>) и получает
    /// <c>Welcome</c> либо <c>Bye</c>.
    /// </summary>
    public async ValueTask HandleInboundFrameAsync(RelayFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case RelayFrameType.Open:
                HandleOpen(frame.StreamId);
                break;
            case RelayFrameType.Data:
                HandleData(frame);
                break;
            case RelayFrameType.Close:
                HandleClose(frame.StreamId);
                break;
            case RelayFrameType.Window:
                HandleWindow(frame);
                break;
            case RelayFrameType.Ping:
                // PING — r→c (keepalive шлёт сам релей); входящий PING от клиента не ожидается — игнорируем.
                break;
            case RelayFrameType.Hello:
                if (frame.StreamId != 0 ||
                    frame.Payload.Length != 1 ||
                    frame.Payload.Span[0] != RelayFrameCodec.HelloProtocolVersion)
                {
                    // Неверное приветствие: завершаем соединение без Welcome.
                    await SendFrameAsync(RelayFrameType.Bye, 0, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    break;
                }

                await SendFrameAsync(RelayFrameType.Welcome, 0, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                break;
            case RelayFrameType.Bye:
                _sessionCts.Cancel();
                break;
            // Welcome/Pong/AuthChallenge/AuthResponse не требуют обработки на сервере (v1: только HMAC ?bridge=).
            case RelayFrameType.Welcome:
            case RelayFrameType.Pong:
            case RelayFrameType.AuthChallenge:
            case RelayFrameType.AuthResponse:
                break;
            default:
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _sessionCts.Cancel();

        RelayStream[] streams;
        Task[] bridges;
        lock (_sync)
        {
            streams = _streams.Values.ToArray();
            _streams.Clear();
            bridges = _bridges.ToArray();
            _bridges.Clear();
        }

        foreach (var stream in streams)
        {
            stream.CompleteInbound();
        }

        try
        {
            await Task.WhenAll(bridges).ConfigureAwait(false);
        }
        catch
        {
            // Мосты гасят свои ошибки; разрыв соединения не должен пробрасывать исключение.
        }

        _sessionCts.Dispose();
    }

    private void HandleOpen(int streamId)
    {
        RelayStream? stream = null;
        lock (_sync)
        {
            if (!_streams.ContainsKey(streamId))
            {
                stream = new RelayStream(streamId, SendFrameAsync, _initialSendWindow) { OnDisposed = Unregister };
                _streams[streamId] = stream;
            }
        }

        if (stream is null)
        {
            return; // дублирующий Open — игнорируем
        }

        PruneCompletedBridges();
        var bridge = _startBridge?.Invoke(stream, _sessionCts.Token);
        if (bridge is not null)
        {
            // Наблюдаем исключения, чтобы не плодить unobserved task exceptions.
            _ = bridge.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            lock (_sync)
            {
                _bridges.Add(bridge);
            }
        }
    }

    private void HandleData(RelayFrame frame)
    {
        RelayStream? stream;
        lock (_sync)
        {
            _streams.TryGetValue(frame.StreamId, out stream);
        }

        stream?.EnqueueData(frame.Payload);
    }

    private void HandleWindow(RelayFrame frame)
    {
        var credit = RelayFrameCodec.ParseWindowCredit(frame.Payload.Span);
        if (credit <= 0)
        {
            return;
        }

        RelayStream? stream;
        lock (_sync)
        {
            _streams.TryGetValue(frame.StreamId, out stream);
        }

        stream?.AddSendCredit(credit);
    }

    private void HandleClose(int streamId)
    {
        RelayStream? stream;
        lock (_sync)
        {
            _streams.Remove(streamId, out stream);
        }

        stream?.CompleteInbound();
    }

    private void Unregister(RelayStream stream)
    {
        lock (_sync)
        {
            _streams.Remove(stream.StreamId);
        }
    }

    private void PruneCompletedBridges()
    {
        lock (_sync)
        {
            _bridges.RemoveAll(static t => t.IsCompleted);
        }
    }

    private async ValueTask SendFrameAsync(
        RelayFrameType type,
        int streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        var size = RelayFrameCodec.FrameSize(payload.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var span = buffer.AsSpan(0, size);
            RelayFrameCodec.Write(span, type, streamId, payload.Span);
            await _sink.SendAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
