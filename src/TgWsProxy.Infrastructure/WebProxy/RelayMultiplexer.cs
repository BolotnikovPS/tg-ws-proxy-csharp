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
    private readonly int _maxStreams;
    private readonly Func<bool>? _tryAcquireGlobalStream;
    private readonly Action? _releaseGlobalStream;
    private readonly Dictionary<int, RelayStream> _streams = new();
    private readonly HashSet<int> _globalSlots = [];
    private readonly List<Task> _bridges = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly object _sync = new();
    private int _closed;

    /// <param name="sink">Куда сериализуются исходящие кадры.</param>
    /// <param name="startBridge">Запуск моста для открытого потока.</param>
    /// <param name="initialSendWindow">Начальный кредит отправки потока.</param>
    /// <param name="maxStreams">
    /// Предел одновременных потоков в сессии (<c>webProxy.maxStreamsPerSession</c>); <c>0</c> — без
    /// ограничения. OPEN сверх предела отклоняется кадром <c>CLOSE</c> (F-W.25).
    /// </param>
    /// <param name="tryAcquireGlobalStream">
    /// Резерв слота в глобальном бюджете потоков (<c>webProxy.maxStreams</c>); <c>null</c> — бюджет
    /// не ведётся. Отказ также отклоняет OPEN кадром <c>CLOSE</c>.
    /// </param>
    /// <param name="releaseGlobalStream">Возврат ранее зарезервированного глобального слота.</param>
    public RelayMultiplexer(
        IRelayFrameSink sink,
        Func<RelayStream, CancellationToken, Task>? startBridge = null,
        long initialSendWindow = RelayFrameCodec.InitialStreamWindow,
        int maxStreams = 0,
        Func<bool>? tryAcquireGlobalStream = null,
        Action? releaseGlobalStream = null)
    {
        _sink = sink;
        _startBridge = startBridge;
        _initialSendWindow = initialSendWindow;
        _maxStreams = maxStreams;
        _tryAcquireGlobalStream = tryAcquireGlobalStream;
        _releaseGlobalStream = releaseGlobalStream;
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
                await HandleOpenAsync(frame.StreamId, ct).ConfigureAwait(false);
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
            ReleaseGlobalSlot(stream.StreamId);
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

    private async ValueTask HandleOpenAsync(int streamId, CancellationToken ct)
    {
        RelayStream? stream = null;
        var globalSlot = false;
        lock (_sync)
        {
            if (_streams.ContainsKey(streamId))
            {
                return; // дублирующий Open — игнорируем
            }

            // F-W.25: локальный предел сессии проверяется до резервирования глобального слота,
            // чтобы отказ не «съедал» бюджет других сессий.
            if (_maxStreams > 0 && _streams.Count >= _maxStreams)
            {
                stream = null;
            }
            else
            {
                stream = new RelayStream(streamId, SendFrameAsync, _initialSendWindow) { OnDisposed = Unregister };
            }
        }

        if (stream is null)
        {
            await RejectOpenAsync(streamId, ct).ConfigureAwait(false);
            return;
        }

        if (_tryAcquireGlobalStream is not null && !_tryAcquireGlobalStream())
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            await RejectOpenAsync(streamId, ct).ConfigureAwait(false);
            return;
        }

        globalSlot = _tryAcquireGlobalStream is not null;

        lock (_sync)
        {
            _streams[streamId] = stream;
            if (globalSlot)
            {
                _globalSlots.Add(streamId);
            }
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

    /// <summary>
    /// Отклоняет OPEN кадром <c>CLOSE</c>: клиент видит закрытие потока и не ждёт данных
    /// (F-W.25 — предел потоков сессии либо исчерпанный глобальный бюджет).
    /// </summary>
    private ValueTask RejectOpenAsync(int streamId, CancellationToken ct)
        => SendFrameAsync(RelayFrameType.Close, streamId, ReadOnlyMemory<byte>.Empty, ct);

    /// <summary>Возвращает глобальный слот потока (ровно один раз на зарезервированный поток).</summary>
    private void ReleaseGlobalSlot(int streamId)
    {
        if (_releaseGlobalStream is null)
        {
            return;
        }

        var released = false;
        lock (_sync)
        {
            released = _globalSlots.Remove(streamId);
        }

        if (released)
        {
            _releaseGlobalStream();
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

        ReleaseGlobalSlot(streamId);
        stream?.CompleteInbound();
    }

    private void Unregister(RelayStream stream)
    {
        lock (_sync)
        {
            _streams.Remove(stream.StreamId);
        }

        ReleaseGlobalSlot(stream.StreamId);
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
