using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Exceptions;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Один MTProto-канал поверх HTTP/2-lane (порт upstream <c>_HttpChannel</c> из
/// <c>proxy/cf_h2.py</c>): очередь запросов к Cloudflare, буфер ответов для native-стороны,
/// повторная отправка «зависших» пакетов и ротация медленных запросов ради пропускной способности.
/// </summary>
internal sealed class CfH2Channel : ICfH2Channel
{
    private const int MaxChannelRequests = 8;
    private const long MaxChannelBytes = 8L * 1024 * 1024;
    private const int QueueCapacity = 16;
    private const int RequestTimeoutSeconds = 65;
    private const double ReplayIdleSeconds = 1.0;
    private const double ReplayRequestSeconds = 3.0;
    private const double ReplayCheckSeconds = 0.25;
    private const double ReplayMaxAge = 30.0;
    private const int ReplayHistoryBytes = 64 * 1024;
    private const int ReplayHistoryPackets = 16;
    private const int MaxChannelReplays = 2;
    private const double ReplaySlotSeconds = 30.0;
    private const double ReplayRetrySeconds = 2.0;
    private const int ReplayMaxAttempts = 3;
    private const double DiagnosticIntervalSeconds = 5.0;

    private readonly CfH2Lane _lane;
    private readonly IProxyStats _stats;
    private readonly ILogger _logger;
    private readonly AsyncCapacityGate _capacity = new(MaxChannelRequests, MaxChannelBytes);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly HashSet<RequestState> _pending = [];
    private readonly Dictionary<RequestState, double> _pendingSince = [];
    private readonly Dictionary<RequestState, double> _sentSince = [];
    private readonly HashSet<RequestState> _receivingTasks = [];
    private readonly HashSet<RequestState> _replayPending = [];
    private readonly Dictionary<RequestState, Task> _tasks = [];
    private readonly List<ReplayPacket> _replayHistory = [];
    private readonly object _sync = new();
    private readonly object _wakeSync = new();

    private TaskCompletionSource _wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _replayKey;
    private long _replyBytes;
    private long _requests;
    private long _up;
    private long _down;
    private long _quickRequests;
    private long _replayRequests;
    private long _pollRequests;
    private long _capacityRotations;
    private long _replayRotations;
    private double _firstPollGap;
    private double _lastProgress;
    private double _lastNativeSend;
    private double _nextCapacityLog;
    private double? _uploadWaitSince;
    private string _uploadWaitPhase = "-";
    private int _uploadWaitBytes;
    private bool _closed;
    private bool _closeDone;

    public CfH2Channel(CfH2Lane lane, int channelId, string label, IProxyStats stats, ILogger logger)
    {
        _lane = lane;
        ChannelId = channelId;
        Label = label;
        _stats = stats;
        _logger = logger;
        var now = CfH2Lane.CurrentTime();
        _lastProgress = now;
        _lastNativeSend = now;
        lane.Register(this);
    }

    /// <summary>Идентификатор канала (для логов).</summary>
    public int ChannelId { get; }

    /// <summary>Метка сессии (scope).</summary>
    public string Label { get; }

    /// <inheritdoc />
    public bool Closed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <inheritdoc />
    public int? TransportError
    {
        get
        {
            lock (_sync)
            {
                return _transportError;
            }
        }
    }

    private int? _transportError;

    private bool NativeBackpressure
    {
        get
        {
            lock (_sync)
            {
                return _replyBytes > 0 || _deliveringSince is not null;
            }
        }
    }

    private double? _deliveringSince;

    /// <inheritdoc />
    public Task<bool> SendAsync(byte[] body, bool quick, CancellationToken cancellationToken) =>
        SendInternalAsync(body, quick, replay: false, cancellationToken);

    /// <inheritdoc />
    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var item = await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (item is Exception error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        var reply = (byte[])item;
        lock (_sync)
        {
            _replyBytes -= reply.Length;
            _down += reply.Length;
            _deliveringSince = CfH2Lane.CurrentTime();
        }

        WakeReceiver();
        return reply;
    }

    /// <summary>Отмечает, что ответ передан клиенту (снимает native backpressure).</summary>
    public void MarkDelivered()
    {
        lock (_sync)
        {
            _deliveringSince = null;
        }

        WakeReceiver();
    }

    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        while (!Closed && !cancellationToken.IsCancellationRequested)
        {
            await WaitWakeupAsync(cancellationToken).ConfigureAwait(false);
            var now = CfH2Lane.CurrentTime();
            await MakeUploadRoomAsync(now).ConfigureAwait(false);
            if (Closed || NativeBackpressure)
            {
                continue;
            }

            var candidates = _replayHistory
                .Select(entry => (Entry: entry, Overdue: OverdueOriginal(entry, now)))
                .OrderBy(pair => pair.Overdue ? 0 : 1)
                .ThenBy(pair => pair.Overdue ? pair.Entry.Sequence : -pair.Entry.Sequence)
                .Select(pair => pair.Entry)
                .ToList();

            foreach (var entry in candidates)
            {
                if (!ReplayDue(entry, now))
                {
                    continue;
                }

                var replace = entry.Pending;
                if (replace is null && _replayPending.Count >= MaxChannelReplays)
                {
                    replace = _pending.FirstOrDefault(state =>
                        _replayPending.Contains(state) && !_receivingTasks.Contains(state));
                    if (replace is null)
                    {
                        continue;
                    }
                }

                if (replace is not null)
                {
                    double sent;
                    lock (_sync)
                    {
                        if (_receivingTasks.Contains(replace) || !_sentSince.TryGetValue(replace, out sent))
                        {
                            continue;
                        }
                    }

                    if (now - sent < ReplaySlotSeconds)
                    {
                        continue;
                    }

                    CancelRequest(replace);
                    await AwaitRequestAsync(replace).ConfigureAwait(false);
                    _replayRotations++;
                }

                await SendInternalAsync(entry.Body, quick: false, replay: true, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary>Регистрирует факт получения HTTP-ответа (вызывается из lane).</summary>
    /// <param name="length">Длина тела ответа.</param>
    public void OnHttpResponse(int length)
    {
        lock (_sync)
        {
            _lastProgress = CfH2Lane.CurrentTime();
        }
    }

    /// <summary>Отмечает, что по каналу сейчас принимается тело ответа.</summary>
    /// <param name="receiving">Признак приёма.</param>
    public void MarkHttpReceiving(bool receiving)
    {
        lock (_sync)
        {
            _receiving = receiving;
        }
    }

    /// <summary>Обновляет отметку прогресса канала.</summary>
    public void MarkProgress()
    {
        lock (_sync)
        {
            _lastProgress = CfH2Lane.CurrentTime();
        }
    }

    private bool _receiving;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closeDone)
            {
                return;
            }

            _closeDone = true;
        }

        var waiting = _pending.Count;
        Fail(new IOException("H2 channel closed"));

        List<RequestState> states;
        lock (_sync)
        {
            states = [.. _pending];
        }

        foreach (var state in states)
        {
            CancelRequest(state);
        }

        foreach (var state in states)
        {
            await AwaitRequestAsync(state).ConfigureAwait(false);
        }

        _capacity.Close();
        _lane.Unregister(this);
        _logger.LogDebug(
            "[{Label}] H2 channel={ChannelId} closed lane={LaneId} requests={Requests} replays={Replays} "
            + "rotations={Rotations} capacity_rotations={CapacityRotations} up={Up} down={Down} quickack={Quick} pending={Pending}",
            Label,
            ChannelId,
            _lane.LaneId,
            _requests,
            _replayRequests,
            _replayRotations,
            _capacityRotations,
            _up,
            _down,
            _quickRequests,
            waiting);
    }

    private async Task<bool> SendInternalAsync(
        byte[] body,
        bool quick,
        bool replay,
        CancellationToken cancellationToken)
    {
        if (replay && _sendLock.CurrentCount == 0)
        {
            return false;
        }

        if (!replay)
        {
            lock (_sync)
            {
                _uploadWaitSince = CfH2Lane.CurrentTime();
                _uploadWaitPhase = "send-lock";
                _uploadWaitBytes = body.Length;
            }
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SendCoreAsync(body, quick, replay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            if (!replay)
            {
                lock (_sync)
                {
                    _uploadWaitSince = null;
                    _uploadWaitPhase = "-";
                    _uploadWaitBytes = 0;
                }
            }
        }
    }

    private async Task<bool> SendCoreAsync(
        byte[] body,
        bool quick,
        bool replay,
        CancellationToken cancellationToken)
    {
        var length = body.Length;
        var channelReserved = false;
        var laneReserved = false;
        var committed = false;

        try
        {
            if (replay)
            {
                if (!_capacity.TryReserve(length))
                {
                    return false;
                }

                channelReserved = true;
            }
            else
            {
                lock (_sync)
                {
                    _uploadWaitPhase = "channel-capacity";
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                await _capacity.ReserveAsync(length, wait: true, timeout.Token).ConfigureAwait(false);
                channelReserved = true;
            }

            if (Closed)
            {
                throw new IOException("H2 channel closed");
            }

            if (!replay)
            {
                lock (_sync)
                {
                    _uploadWaitPhase = "lane-capacity";
                }
            }

            if (!await _lane.ReserveAsync(length, wait: !replay, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            laneReserved = true;

            if (Closed)
            {
                throw new IOException("H2 channel closed");
            }

            var now = CfH2Lane.CurrentTime();
            ReplayPacket? replayPacket = null;
            if (replay)
            {
                replayPacket = FindPacket(body);
                if (replayPacket is null
                    || replayPacket.Pending is not null
                    || !ReplayDue(replayPacket, now))
                {
                    return false;
                }
            }

            var state = new RequestState { Length = length, PendingSince = now };
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            state.Cts = cts;

            ReplayPacket? nativePacket = null;
            lock (_sync)
            {
                _pending.Add(state);
                _pendingSince[state] = now;
                _requests++;
                _up += length;
                _replayRequests += replay ? 1 : 0;
                if (quick)
                {
                    _quickRequests++;
                }

                if (!replay)
                {
                    _lastNativeSend = now;
                }
            }

            _stats.AddBytesUp(length);

            if (!replay)
            {
                nativePacket = RememberPacket(body, now);
            }

            var packet = replayPacket ?? nativePacket;
            state.Packet = packet;

            lock (_sync)
            {
                if (nativePacket is not null)
                {
                    nativePacket.Originals.Add(state);
                }

                if (replay && replayPacket is not null)
                {
                    if (!OverdueOriginal(replayPacket, now))
                    {
                        _pollRequests++;
                        if (!replayPacket.Replayed)
                        {
                            _firstPollGap = Math.Max(_firstPollGap, now - _lastProgress);
                        }
                    }

                    _replayPending.Add(state);
                    replayPacket.Attempts++;
                    replayPacket.LastAttemptAt = now;
                    replayPacket.Pending = state;
                }
            }

            if (nativePacket is not null)
            {
                TrimHistory(now);
            }

            committed = true;

            var task = RunRequestAsync(state, body, replay, packet);
            lock (_sync)
            {
                if (_pending.Contains(state))
                {
                    _tasks[state] = task;
                }
            }
        }
        finally
        {
            if (!committed)
            {
                if (laneReserved)
                {
                    _lane.Release(length);
                }

                if (channelReserved)
                {
                    _capacity.Release(length);
                }
            }
        }

        return true;
    }

    private async Task RunRequestAsync(RequestState state, byte[] body, bool replay, ReplayPacket? packet)
    {
        var token = state.Cts?.Token ?? CancellationToken.None;
        try
        {
            // Момент отправки тела запроса: с него отсчитывается «зависание» пакета.
            lock (_sync)
            {
                _sentSince[state] = CfH2Lane.CurrentTime();
            }

            var reply = await _lane.PostAsync(body, this, replay, token).ConfigureAwait(false);
            lock (_sync)
            {
                _lastProgress = CfH2Lane.CurrentTime();
            }

            if (reply.Length > 0 && !Closed)
            {
                if (packet is not null
                    && (packet.ReplyId is null || !HeadMatches(reply, packet.ReplyId)))
                {
                    // Как в upstream: reply_id = reply[:24]. Выделяем массив только когда
                    // идентификатор ответа действительно изменился (стабильный поток media
                    // идёт по горячему пути без аллокаций).
                    packet.ReplyId = HeadOf(reply);
                    packet.Attempts = 0;
                }

                if (!TryEnqueueReply(reply))
                {
                    throw new InvalidDataException("H2 client response queue exceeded bound");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MTProtoTransportException ex)
        {
            _stats.IncH2Errors();
            if (!Closed)
            {
                _logger.LogWarning(
                    "[{Label}] H2 lane={LaneId} channel={ChannelId} host={Host} MTProto transport error={Error} "
                    + "request={Request} bytes={Bytes} replay={Replay}; reporting to client",
                    Label,
                    _lane.LaneId,
                    ChannelId,
                    _lane.Host,
                    ex.Code,
                    _requests,
                    body.Length,
                    replay);
            }

            Fail(ex);
        }
        catch (Exception ex)
        {
            _stats.IncH2Errors();
            if (!Closed)
            {
                _logger.LogWarning(
                    "[{Label}] H2 lane={LaneId} channel={ChannelId} request failed: {Error}; "
                    + "closing native channel, reconnect may select another domain",
                    Label,
                    _lane.LaneId,
                    ChannelId,
                    ex.Message);
            }

            Fail(ex);
        }
        finally
        {
            await FinishRequestAsync(state, packet).ConfigureAwait(false);
        }
    }

    private async Task FinishRequestAsync(RequestState state, ReplayPacket? packet)
    {
        lock (_sync)
        {
            if (!_pending.Remove(state))
            {
                return;
            }

            _pendingSince.Remove(state);
            _sentSince.Remove(state);
            _replayPending.Remove(state);
            _tasks.Remove(state);
        }

        state.Cts?.Dispose();

        var owner = packet
            ?? _replayHistory.FirstOrDefault(entry => entry.Originals.Contains(state) || entry.Pending == state);
        if (owner is not null)
        {
            lock (_sync)
            {
                owner.Originals.Remove(state);
                if (owner.Pending == state)
                {
                    owner.Pending = null;
                }
            }
        }

        _capacity.Release(state.Length);
        _lane.Release(state.Length);
        WakeReceiver();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private bool TryEnqueueReply(byte[] reply)
    {
        lock (_sync)
        {
            if (_replyBytes + reply.Length > MaxChannelBytes || _queue.Reader.Count >= QueueCapacity)
            {
                return false;
            }

            _replyBytes += reply.Length;
        }

        if (!_queue.Writer.TryWrite(reply))
        {
            lock (_sync)
            {
                _replyBytes -= reply.Length;
            }

            return false;
        }

        return true;
    }

    private void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            if (error is MTProtoTransportException transport)
            {
                _transportError = transport.Code;
            }

            _closed = true;
            _replyBytes = 0;
            _deliveringSince = null;
        }

        SignalWakeup();

        while (_queue.Reader.TryRead(out _))
        {
            // Освобождаем очередь перед публикацией ошибки.
        }

        _queue.Writer.TryWrite(error);
    }

    private void CancelRequest(RequestState state)
    {
        state.Cancelled = true;
        try
        {
            state.Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Запрос уже завершился.
        }
    }

    private async Task AwaitRequestAsync(RequestState state)
    {
        Task? task;
        lock (_sync)
        {
            _tasks.TryGetValue(state, out task);
        }

        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ошибка уже обработана в RunRequestAsync.
        }
    }

    private async Task MakeUploadRoomAsync(double now)
    {
        double? waitSince;
        string phase;
        int waitBytes;
        lock (_sync)
        {
            waitSince = _uploadWaitSince;
            phase = _uploadWaitPhase;
            waitBytes = _uploadWaitBytes;
        }

        if (Closed || waitSince is null)
        {
            return;
        }

        List<CfH2Channel> channels;
        if (phase == "channel-capacity")
        {
            if (_capacity.HasCapacity(waitBytes))
            {
                return;
            }

            channels = [this];
        }
        else if (phase == "lane-capacity")
        {
            if (_lane.HasCapacity(waitBytes))
            {
                return;
            }

            channels = [.. _lane.SnapshotChannels().OrderBy(channel => channel.MinSentSince(now))];
        }
        else
        {
            return;
        }

        foreach (var channel in channels)
        {
            var candidate = channel.CapacityCandidate(now);
            if (candidate is null)
            {
                continue;
            }

            var (state, entry) = candidate.Value;
            lock (channel._sync)
            {
                channel._sentSince.Remove(state);
            }

            if (entry is not null)
            {
                entry.Retired = true;
            }

            if (now >= _nextCapacityLog)
            {
                _nextCapacityLog = now + DiagnosticIntervalSeconds;
                _logger.LogDebug(
                    "H2 CAPACITY lane={LaneId} waiting_channel={Waiting} release_channel={Release} "
                    + "reason={Reason} replay={Replay} wait_ms={Wait:F0} inflight={Inflight}",
                    _lane.LaneId,
                    ChannelId,
                    channel.ChannelId,
                    phase,
                    channel._replayPending.Contains(state),
                    (now - waitSince.Value) * 1000,
                    _lane.ReservedRequests);
            }

            channel.CancelRequest(state);
            await channel.AwaitRequestAsync(state).ConfigureAwait(false);
            channel._capacityRotations++;
            return;
        }
    }

    private (RequestState State, ReplayPacket? Entry)? CapacityCandidate(double now)
    {
        List<KeyValuePair<RequestState, double>> ordered;
        lock (_sync)
        {
            ordered = [.. _sentSince.OrderBy(pair => _replayPending.Contains(pair.Key)).ThenBy(pair => pair.Value)];
        }

        foreach (var pair in ordered)
        {
            var state = pair.Key;
            bool receiving;
            bool replayPending;
            lock (_sync)
            {
                receiving = _receivingTasks.Contains(state);
                replayPending = _replayPending.Contains(state);
            }

            if (state.Cancelled || receiving || now - pair.Value < ReplayRequestSeconds)
            {
                continue;
            }

            if (replayPending)
            {
                return (state, null);
            }

            var entry = _replayHistory.FirstOrDefault(item => item.Originals.Contains(state));
            if (entry is not null && now - entry.SentAt <= ReplayMaxAge)
            {
                return (state, entry);
            }
        }

        return null;
    }

    private double MinSentSince(double now)
    {
        lock (_sync)
        {
            return _sentSince.Count == 0 ? now : _sentSince.Values.Min();
        }
    }

    private string? ReplayBlocker(ReplayPacket entry, double now)
    {
        var age = now - entry.SentAt;
        if (age < 0 || age > ReplayMaxAge)
        {
            return "expired";
        }

        if (entry.Attempts >= ReplayLimit(entry))
        {
            return "budget-used";
        }

        if (NativeBackpressure)
        {
            return "native-backpressure";
        }

        lock (_sync)
        {
            if (entry.Originals.Any(_receivingTasks.Contains))
            {
                return "original-body";
            }

            if (entry.Pending is not null && _receivingTasks.Contains(entry.Pending))
            {
                return "replay-body";
            }
        }

        if (entry.Replayed && now - entry.LastAttemptAt < ReplayRetrySeconds)
        {
            return "backoff-or-progress";
        }

        if (OverdueOriginal(entry, now))
        {
            return null;
        }

        lock (_sync)
        {
            if (entry != _replayHistory[^1] || _pending.Any(state => state != entry.Pending))
            {
                return "receiver-active";
            }

            if (entry.Replayed && now - _lastProgress < ReplayIdleSeconds)
            {
                return "backoff-or-progress";
            }
        }

        return null;
    }

    private bool ReplayDue(ReplayPacket entry, double now) => ReplayBlocker(entry, now) is null;

    private int ReplayLimit(ReplayPacket entry) =>
        entry.Originals.Count > 0 || entry.Retired || entry == _replayHistory[^1] ? ReplayMaxAttempts : 0;

    private bool OverdueOriginal(ReplayPacket entry, double now)
    {
        lock (_sync)
        {
            if (entry.Retired)
            {
                return true;
            }

            return entry.Originals.Any(state =>
                _sentSince.TryGetValue(state, out var sent)
                && !_receivingTasks.Contains(state)
                && now - sent >= ReplayRequestSeconds);
        }
    }

    /// <summary>Возвращает первые 24 байта ответа (идентификатор ответа Telegram).</summary>
    private static byte[] HeadOf(byte[] reply) => reply.AsSpan(0, Math.Min(24, reply.Length)).ToArray();

    /// <summary>Сравнивает первые 24 байта ответа с ранее запомненным идентификатором.</summary>
    private static bool HeadMatches(byte[] reply, byte[] candidate)
    {
        var length = Math.Min(24, reply.Length);
        return candidate.Length == length && reply.AsSpan(0, length).SequenceEqual(candidate);
    }

    private ReplayPacket? FindPacket(byte[] body) =>
        _replayHistory.FirstOrDefault(entry => entry.Body.AsSpan().SequenceEqual(body));

    private ReplayPacket? RememberPacket(byte[] body, double now)
    {
        var key = body.Length >= 8 ? body.AsSpan(0, 8) : body.AsSpan();
        if (key.Length >= 8 && key.IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        if (_replayKey is null || !_replayKey.AsSpan().SequenceEqual(key))
        {
            _replayHistory.Clear();
            _replayKey = key.ToArray();
        }

        if (body.Length > ReplayHistoryBytes)
        {
            return null;
        }

        var packet = FindPacket(body);
        if (packet is null)
        {
            packet = new ReplayPacket { SentAt = now, Body = body, Sequence = _requests };
            _replayHistory.Add(packet);
        }

        return packet;
    }

    private void TrimHistory(double now)
    {
        var retained = _replayHistory.Sum(entry => (long)entry.Body.Length);
        while (_replayHistory.Count > ReplayHistoryPackets || retained > ReplayHistoryBytes)
        {
            var victim = _replayHistory.FirstOrDefault(entry =>
                    entry.Originals.Count == 0
                    && !(entry.Retired && entry.Attempts < ReplayMaxAttempts && now - entry.SentAt <= ReplayMaxAge))
                ?? _replayHistory[0];
            _replayHistory.Remove(victim);
            retained -= victim.Body.Length;
        }
    }

    private void WakeReceiver()
    {
        if (Closed || _pending.Count > 0 || NativeBackpressure)
        {
            return;
        }

        SignalWakeup();
    }

    private void SignalWakeup()
    {
        lock (_wakeSync)
        {
            _wakeup.TrySetResult();
        }
    }

    private async Task WaitWakeupAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_wakeSync)
        {
            pending = _wakeup.Task;
        }

        var delay = Task.Delay(TimeSpan.FromSeconds(ReplayCheckSeconds), cancellationToken);
        await Task.WhenAny(pending, delay).ConfigureAwait(false);

        lock (_wakeSync)
        {
            if (_wakeup.Task.IsCompleted)
            {
                _wakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (delay.IsCompleted && !pending.IsCompleted)
        {
            return;
        }
    }

    /// <summary>Состояние одного запроса к Cloudflare (аналог ключа-таска в upstream).</summary>
    private sealed class RequestState
    {
        public int Length { get; init; }

        public double PendingSince { get; init; }

        public CancellationTokenSource? Cts { get; set; }

        public ReplayPacket? Packet { get; set; }

        public bool Cancelled { get; set; }

        public double SentSince { get; set; }
    }

    /// <summary>Запоминаемый пакет для возможной повторной отправки (порт upstream <c>_ReplayPacket</c>).</summary>
    private sealed class ReplayPacket
    {
        public required double SentAt { get; init; }

        public required byte[] Body { get; init; }

        public required long Sequence { get; init; }

        public int Attempts { get; set; }

        public double LastAttemptAt { get; set; }

        public RequestState? Pending { get; set; }

        public HashSet<RequestState> Originals { get; } = [];

        public bool Retired { get; set; }

        public byte[]? ReplyId { get; set; }

        public bool Replayed => Attempts > 0 || LastAttemptAt > 0;
    }
}
