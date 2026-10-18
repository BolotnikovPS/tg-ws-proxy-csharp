namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>Результат обработки восходящего батча <c>POST /api/v1/up</c>.</summary>
/// <param name="Accepted">Батч принят (или является корректным повтором последнего принятого).</param>
/// <param name="Ack">Номер подтверждённой последовательности (равен <c>X-Up-Seq</c> при <c>Accepted</c>).</param>
public readonly record struct WebProxyUpResult(bool Accepted, long Ack);

/// <summary>Результат нисходящего long-poll <c>POST /api/v1/down</c>.</summary>
/// <param name="Batch">
/// Готовый батч кадров для доставки; <see langword="null"/> — данных нет (ответ 204).
/// </param>
/// <param name="Cursor">Значение <c>X-Down-Cursor</c> для ответа.</param>
/// <param name="Closed">Сессия завершена (клиент должен закрыться).</param>
public readonly record struct WebProxyDownlink(byte[]? Batch, long Cursor, bool Closed);

/// <summary>
/// Состояние одной relay-сессии WEB proxy поверх HTTPS long-poll носителя. Переиспользует
/// <see cref="RelayMultiplexer"/>: входящие кадры поступают батчами из <c>POST /up</c>
/// (<see cref="ProcessUpAsync"/>), исходящие буферизуются и отдаются батчем через
/// <see cref="PollAsync"/> (<c>POST /down</c>). Реализует байт-идентичный повтор восходящей
/// последовательности и повтор нисходящего батча по неподтверждённому курсору.
/// </summary>
public sealed class WebProxyHttpSession : IAsyncDisposable
{
    private enum PollSignal
    {
        /// <summary>Появились исходящие кадры — перечитать очередь.</summary>
        Data,

        /// <summary>Паркованный poll вытеснен новым либо отменён — вернуть 204.</summary>
        Superseded,

        /// <summary>Сессия закрыта.</summary>
        Closed,
    }

    private readonly RelayMultiplexer _multiplexer;
    private readonly object _sync = new();
    private readonly List<byte[]> _pending = new();
    private readonly SemaphoreSlim _upGate = new(1, 1);
    private TaskCompletionSource<PollSignal>? _parked;
    private bool _closed;

    private long _upSeq;                 // последняя принятая восходящая последовательность (0 — ни одной)
    private byte[]? _lastUpBody;         // тело последнего принятого батча (для байт-идентичного повтора)
    private long _nextCursor;            // курсор, возвращаемый при следующей доставке
    private byte[]? _lastBatch;          // последний доставленный батч (для повтора)
    private long _lastBatchCursor = -1;  // курсор, по которому клиент получает повтор <see cref="_lastBatch"/>

    public WebProxyHttpSession(Func<RelayStream, CancellationToken, Task> startBridge)
    {
        _multiplexer = new RelayMultiplexer(new Sink(this), startBridge);
    }

    /// <summary>Закрыта ли сессия (для диагностики и тестов).</summary>
    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Обрабатывает восходящий батч. Принимает следующую последовательность (<c>seq == last+1</c>)
    /// либо байт-идентичный повтор последней принятой. Иное — отклонение.
    /// </summary>
    public async ValueTask<WebProxyUpResult> ProcessUpAsync(long seq, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        // Быстрый путь: повтор уже принятого батча — подтверждаем без повторной обработки.
        lock (_sync)
        {
            if (seq == _upSeq && _lastUpBody is not null && body.Span.SequenceEqual(_lastUpBody))
            {
                return new WebProxyUpResult(true, seq);
            }
        }

        await _upGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (seq != _upSeq + 1)
                {
                    return new WebProxyUpResult(false, 0);
                }
            }

            if (!TryParseBatch(body, out var frames))
            {
                return new WebProxyUpResult(false, 0);
            }

            foreach (var frame in frames)
            {
                await _multiplexer.HandleInboundFrameAsync(frame, ct).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _upSeq = seq;
                _lastUpBody = body.ToArray();
            }

            return new WebProxyUpResult(true, seq);
        }
        finally
        {
            _upGate.Release();
        }
    }

    /// <summary>
    /// Нисходящий long-poll. <paramref name="cursor"/> равен <see cref="_nextCursor"/> — ждём новых
    /// кадров и доставляем батч; равен <see cref="_lastBatchCursor"/> — повторяем последний батч;
    /// иначе — <see cref="InvalidDataException"/>. При отмене возвращает пустой ответ (204).
    /// </summary>
    public async ValueTask<WebProxyDownlink> PollAsync(long cursor, CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource<PollSignal> parked;
            lock (_sync)
            {
                if (_closed)
                {
                    return new WebProxyDownlink(null, _nextCursor, true);
                }

                if (cursor == _lastBatchCursor && _lastBatch is not null)
                {
                    return new WebProxyDownlink(_lastBatch, _nextCursor, false);
                }

                if (cursor != _nextCursor)
                {
                    throw new InvalidDataException($"invalid down cursor {cursor} (expected {_nextCursor})");
                }

                if (_pending.Count > 0)
                {
                    var batch = JoinAndClear();
                    _lastBatch = batch;
                    _lastBatchCursor = _nextCursor;
                    _nextCursor++;
                    return new WebProxyDownlink(batch, _nextCursor, false);
                }

                // Парковка: новейший poll вытесняет предыдущий (тот получает 204).
                var old = _parked;
                _parked = new TaskCompletionSource<PollSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
                parked = _parked;
                old?.TrySetResult(PollSignal.Superseded);
            }

            using var reg = ct.Register(
                static s => ((TaskCompletionSource<PollSignal>)s!).TrySetResult(PollSignal.Superseded),
                parked);

            var signal = await parked.Task.ConfigureAwait(false);
            switch (signal)
            {
                case PollSignal.Data:
                    continue;
                case PollSignal.Closed:
                    return new WebProxyDownlink(null, _nextCursor, true);
                default:
                    return new WebProxyDownlink(null, _nextCursor, false);
            }
        }
    }

    /// <summary>Помечает сессию закрытой и будит паркованный poll.</summary>
    public void Close()
    {
        TaskCompletionSource<PollSignal>? parked;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            parked = _parked;
            _parked = null;
        }

        parked?.TrySetResult(PollSignal.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
    }

    private void Append(ReadOnlyMemory<byte> frameBytes)
    {
        lock (_sync)
        {
            _pending.Add(frameBytes.ToArray());
            _parked?.TrySetResult(PollSignal.Data);
            _parked = null;
        }
    }

    private byte[] JoinAndClear()
    {
        var total = 0;
        foreach (var frame in _pending)
        {
            total += frame.Length;
        }

        var batch = new byte[total];
        var offset = 0;
        foreach (var frame in _pending)
        {
            frame.CopyTo(batch, offset);
            offset += frame.Length;
        }

        _pending.Clear();
        return batch;
    }

    private static bool TryParseBatch(ReadOnlyMemory<byte> body, out List<RelayFrame> frames)
    {
        frames = [];
        var remaining = body;
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (count >= RelayFrameCodec.MaxBatchFrames)
            {
                frames = null!;
                return false;
            }

            if (!RelayFrameCodec.TryParse(remaining, out var frame, out _))
            {
                frames = null!;
                return false;
            }

            // Пустой DATA-кадр недопустим (см. splitFrames в bridge-странице).
            if (frame.Type == RelayFrameType.Data && frame.Payload.IsEmpty)
            {
                frames = null!;
                return false;
            }

            frames.Add(new RelayFrame(frame.Type, frame.StreamId, frame.Payload.ToArray()));
            remaining = remaining[RelayFrameCodec.FrameSize(frame.Payload.Length)..];
            count++;
        }

        return frames.Count > 0;
    }

    /// <summary>Буферизующий выход мультиплексора: копит кадры и будит паркованный poll.</summary>
    private sealed class Sink(WebProxyHttpSession owner) : IRelayFrameSink
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken ct)
        {
            owner.Append(frameBytes);
            return ValueTask.CompletedTask;
        }
    }
}
