#nullable enable

using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Пул HTTP/2-lane'ов к Cloudflare для media-трафика (порт upstream <c>CfH2Pool</c>
/// из <c>proxy/cf_h2.py</c>). Держит по одному соединению на хост
/// <c>kws{dc}.{baseDomain}</c> и выдаёт каналы поверх них.
/// </summary>
internal sealed class CfH2Pool(
    ICfDomainBalancer balancer,
    IProxyStats stats,
    ILogger<CfH2Pool> logger) : ICfH2Pool
{
    private const double DomainCooldownSeconds = 30.0;
    private const double SetupTimeoutSeconds = 8.0;

    private readonly object _sync = new();
    private readonly Dictionary<string, CfH2Lane> _lanes = [];
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];
    private readonly Dictionary<string, double> _failedUntil = [];
    private readonly Dictionary<int, double> _setupRetryAfter = [];
    private int _nextLane;
    private int _nextChannel;
    private bool _closed;

    /// <inheritdoc />
    public async Task<ICfH2Channel?> OpenAsync(int dc, string label, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_closed)
            {
                throw new IOException("H2 pool closed");
            }

            if (CfH2Lane.CurrentTime() < _setupRetryAfter.GetValueOrDefault(dc))
            {
                return null;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(SetupTimeoutSeconds));

        try
        {
            return await OpenCoreAsync(dc, label, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _setupRetryAfter[dc] = CfH2Lane.CurrentTime() + DomainCooldownSeconds;
            }

            stats.IncH2Errors();
            logger.LogWarning(
                "[{Label}] H2 DC{Dc} setup timed out; using WS for {Cooldown:F0}s",
                label,
                dc,
                DomainCooldownSeconds);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync()
    {
        List<CfH2Lane> lanes;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            lanes = [.. _lanes.Values];
            _lanes.Clear();
        }

        foreach (var lane in lanes)
        {
            await lane.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ICfH2Channel?> OpenCoreAsync(int dc, string label, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_closed)
            {
                throw new IOException("H2 pool closed");
            }
        }

        if (!Protocol.DcDefaultIps.ContainsKey(dc))
        {
            logger.LogWarning("[{Label}] H2 unsupported DC{Dc} -> existing WS route", label, dc);
            return null;
        }

        foreach (var baseDomain in balancer.DomainsForDc(dc))
        {
            var host = $"kws{dc}.{baseDomain}";
            var hostLock = GetHostLock(host);
            await hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_closed)
                    {
                        throw new IOException("H2 pool closed");
                    }
                }

                CfH2Lane? lane;
                lock (_sync)
                {
                    _lanes.TryGetValue(host, out lane);
                }

                var laneFailedUntil = lane?.FailedUntil ?? 0;
                if (CfH2Lane.CurrentTime() < Math.Max(_failedUntil.GetValueOrDefault(host), laneFailedUntil))
                {
                    continue;
                }

                if (lane is null)
                {
                    lock (_sync)
                    {
                        _nextLane++;
                    }

                    lane = new CfH2Lane(host, _nextLane, stats, logger);
                    try
                    {
                        await lane.PreflightAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                                   or OperationCanceledException)
                    {
                        stats.IncH2Errors();
                        lock (_sync)
                        {
                            _failedUntil[host] = CfH2Lane.CurrentTime() + DomainCooldownSeconds;
                        }

                        logger.LogWarning(
                            "[{Label}] H2 lane={LaneId} preflight failed host={Host}: {Error}",
                            label,
                            lane.LaneId,
                            host,
                            ex.Message);
                        await lane.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    lock (_sync)
                    {
                        if (_closed)
                        {
                            _closed = true;
                        }
                        else
                        {
                            _lanes[host] = lane;
                        }
                    }

                    if (ClosedSnapshot())
                    {
                        await lane.DisposeAsync().ConfigureAwait(false);
                        throw new IOException("H2 pool closed during preflight");
                    }
                }

                int channelId;
                lock (_sync)
                {
                    _nextChannel++;
                    channelId = _nextChannel;
                }

                var channel = new CfH2Channel(lane, channelId, label, stats, logger);
                balancer.UpdateDomainForDc(dc, baseDomain);
                logger.LogInformation(
                    "[{Label}] ROUTE DC{Dc} media -> H2 lane={LaneId} channel={ChannelId} host={Host} "
                    + "channels={Channels} tcp_connections={Tcp}",
                    label,
                    dc,
                    lane.LaneId,
                    channelId,
                    host,
                    lane.SnapshotChannels().Count,
                    lane.TcpConnections);
                return channel;
            }
            finally
            {
                hostLock.Release();
            }
        }

        logger.LogDebug("[{Label}] H2 DC{Dc} unavailable; using WS", label, dc);
        return null;
    }

    private bool ClosedSnapshot()
    {
        lock (_sync)
        {
            return _closed;
        }
    }

    private SemaphoreSlim GetHostLock(string host)
    {
        lock (_sync)
        {
            if (!_locks.TryGetValue(host, out var hostLock))
            {
                hostLock = new SemaphoreSlim(1, 1);
                _locks[host] = hostLock;
            }

            return hostLock;
        }
    }
}
