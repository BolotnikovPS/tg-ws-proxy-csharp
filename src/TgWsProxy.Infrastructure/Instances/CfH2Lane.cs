using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain.Exceptions;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Одно HTTP/2-соединение к Cloudflare-эндпоинту <c>https://{host}/api</c>
/// (порт upstream <c>_HttpLane</c> из <c>proxy/cf_h2.py</c>). Мультиплексированием,
/// управлением потоками и ALPN занимается <see cref="SocketsHttpHandler"/>.
/// </summary>
internal sealed class CfH2Lane : IAsyncDisposable
{
    private const int MaxLaneRequests = 64;
    private const long MaxLaneBytes = 32L * 1024 * 1024;
    private const long MaxLaneReplyBytes = 32L * 1024 * 1024;
    private const double DomainCooldownSeconds = 30.0;
    private const int RequestTimeoutSeconds = 65;
    private const int ConnectTimeoutSeconds = 8;
    private const int PreflightTimeoutSeconds = 10;
    private const double SlowResponseSeconds = 3.0;

    /// <summary>Размер буфера чтения тела H2-ответа (арендуется в пуле на запрос).</summary>
    private const int ReadChunkBytes = 64 * 1024;

    private readonly AsyncCapacityGate _capacity = new(MaxLaneRequests, MaxLaneBytes);
    private readonly object _sync = new();
    private readonly HashSet<CfH2Channel> _channels = [];
    private readonly IProxyStats _stats;
    private readonly ILogger _logger;
    private readonly HttpClient _client;

    private long _replyBufferBytes;
    private long _requests;
    private long _responses;
    private long _errors;
    private long _tcpConnections;
    private long _replays;
    private long _slowHeaders;
    private long _slowBodies;
    private bool _closed;

    public CfH2Lane(string host, int laneId, IProxyStats stats, ILogger logger)
    {
        Host = host;
        LaneId = laneId;
        _stats = stats;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
            EnableMultipleHttp2Connections = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Cloudflare-эндпоинты проверяются по SNI, цепочку upstream тоже не валидирует.
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };

        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("tg-ws-proxy");
    }

    /// <summary>Идентификатор lane для логов.</summary>
    public int LaneId { get; }

    /// <summary>Хост вида <c>kws{dc}.{baseDomain}</c>.</summary>
    public string Host { get; }

    /// <summary>Момент времени, до которого домен считается в cooldown после ошибки.</summary>
    public double FailedUntil { get; private set; }

    /// <summary>Признак закрытия lane.</summary>
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

    /// <summary>Число установленных TCP-соединений HTTP/2 (для диагностики).</summary>
    public long TcpConnections
    {
        get
        {
            lock (_sync)
            {
                return _tcpConnections;
            }
        }
    }

    /// <summary>Проверяет, есть ли в lane место под запрос указанного размера.</summary>
    /// <param name="length">Размер запроса в байтах.</param>
    public bool HasCapacity(int length) => _capacity.HasCapacity(length);

    /// <summary>Текущее число активных запросов в lane.</summary>
    public int ReservedRequests => _capacity.ReservedRequests;

    /// <summary>Резервирует место в lane под запрос.</summary>
    /// <param name="length">Размер запроса в байтах.</param>
    /// <param name="wait">Ожидать освобождения места.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public Task<bool> ReserveAsync(int length, bool wait, CancellationToken cancellationToken) =>
        _capacity.ReserveAsync(length, wait, cancellationToken);

    /// <summary>Освобождает место в lane.</summary>
    /// <param name="length">Размер завершённого запроса в байтах.</param>
    public void Release(int length) => _capacity.Release(length);

    /// <summary>Регистрирует канал в lane (для диагностики и «освобождения места»).</summary>
    /// <param name="channel">Канал клиентской сессии.</param>
    public void Register(CfH2Channel channel)
    {
        lock (_sync)
        {
            _channels.Add(channel);
        }
    }

    /// <summary>Убирает канал из lane.</summary>
    /// <param name="channel">Канал клиентской сессии.</param>
    public void Unregister(CfH2Channel channel)
    {
        lock (_sync)
        {
            _channels.Remove(channel);
        }
    }

    /// <summary>Возвращает снимок каналов lane.</summary>
    public List<CfH2Channel> SnapshotChannels()
    {
        lock (_sync)
        {
            return [.. _channels];
        }
    }

    /// <summary>Помечает домен в cooldown после транспортной ошибки.</summary>
    /// <param name="now">Текущее время в секундах монотонных часов.</param>
    public void MarkFailed(double now)
    {
        lock (_sync)
        {
            FailedUntil = now + DomainCooldownSeconds;
        }
    }

    /// <summary>
    /// Проверяет, что эндпоинт отвечает по HTTP/2 (порт upstream <c>_preflight</c>).
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <exception cref="IOException">Эндпоинт ответил недопустимым статусом.</exception>
    /// <exception cref="InvalidDataException">Соединение деградировало до HTTP/1.1.</exception>
    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(PreflightTimeoutSeconds));
        var started = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{Host}{Protocol.H2ApiPath}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (response.Version.Major < 2)
        {
            throw new InvalidDataException($"CF /api preflight negotiated HTTP/{response.Version}");
        }

        var status = (int)response.StatusCode;
        if (status is not (>= 200 and < 300) and not (405 or 501))
        {
            throw new IOException($"CF /api preflight HTTP {status} cf_ray={GetCfRay(response)}");
        }

        lock (_sync)
        {
            _tcpConnections++;
        }

        _stats.IncH2TcpConnections();
        _logger.LogDebug(
            "H2 lane={LaneId} ready host={Host} status={Status} ms={Elapsed:F0}",
            LaneId,
            Host,
            status,
            (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }

    /// <summary>
    /// Отправляет один MTProto-пакет через HTTP/2 и возвращает тело ответа
    /// (порт upstream <c>_HttpLane._post</c>).
    /// </summary>
    /// <param name="body">Тело пакета.</param>
    /// <param name="channel">Канал-владелец запроса (для отметок прогресса).</param>
    /// <param name="replay">Признак повторной отправки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Тело ответа Telegram (может быть пустым).</returns>
    /// <exception cref="MTProtoTransportException">Cloudflare вернул транспортный код ошибки.</exception>
    public async Task<byte[]> PostAsync(
        byte[] body,
        CfH2Channel channel,
        bool replay,
        CancellationToken cancellationToken)
    {
        if (Closed)
        {
            throw new IOException("H2 lane closed");
        }

        lock (_sync)
        {
            _requests++;
            _replays += replay ? 1 : 0;
        }

        _stats.IncH2Requests();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        var started = DateTimeOffset.UtcNow;
        long buffered = 0;
        var receiving = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}{Protocol.H2ApiPath}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.TryAddWithoutValidation("accept-encoding", "identity");

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var headersAt = DateTimeOffset.UtcNow;
            var headersSeconds = (headersAt - started).TotalSeconds;
            lock (_sync)
            {
                _slowHeaders += headersSeconds >= SlowResponseSeconds ? 1 : 0;
            }

            if (response.Version.Major < 2)
            {
                throw new InvalidDataException($"CF /api downgraded to HTTP/{response.Version}");
            }

            var status = (int)response.StatusCode;
            if (status is 403 or 404 or 429 or 444)
            {
                throw new MTProtoTransportException(-status, $"HTTP {status} cf_ray={GetCfRay(response)}");
            }

            if (status != 200)
            {
                throw new IOException($"CF /api HTTP {status} cf_ray={GetCfRay(response)}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("CF /api returned HTML instead of MTProto");
            }

            receiving = true;
            channel.MarkHttpReceiving(true);

            var declared = response.Content.Headers.ContentLength;
            if (declared is > Protocol.H2MaxPacket)
            {
                throw new InvalidDataException("H2 response Content-Length exceeds native packet bound");
            }

            var content = await ReadBoundedAsync(response, channel, timeout.Token).ConfigureAwait(false);
            buffered = content.Length;

            if (declared is not null && content.Length != declared)
            {
                throw new InvalidDataException("incomplete H2 response body");
            }

            if (content.Length > 0 && content.Length % 4 != 0)
            {
                throw new InvalidDataException("CF /api response is not an aligned MTProto packet");
            }

            if (content.Length == 4)
            {
                var code = BinaryPrimitives.ReadInt32LittleEndian(content);
                if (code < 0)
                {
                    throw new MTProtoTransportException(code, "HTTP 200 payload");
                }
            }

            lock (_sync)
            {
                _responses++;
            }

            channel.OnHttpResponse(content.Length);

            var bodySeconds = (DateTimeOffset.UtcNow - headersAt).TotalSeconds;
            if (headersSeconds + bodySeconds >= SlowResponseSeconds)
            {
                lock (_sync)
                {
                    _slowBodies++;
                }

                _logger.LogDebug(
                    "H2 SLOW lane={LaneId} host={Host} bytes={Bytes} replay={Replay} headers_ms={Headers:F0} body_ms={Body:F0}",
                    LaneId,
                    Host,
                    content.Length,
                    replay,
                    headersSeconds * 1000,
                    bodySeconds * 1000);
            }

            return content;
        }
        catch (MTProtoTransportException)
        {
            lock (_sync)
            {
                _errors++;
            }

            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            lock (_sync)
            {
                _errors++;
            }

            MarkFailed(CurrentTime());
            throw;
        }
        finally
        {
            if (buffered > 0)
            {
                lock (_sync)
                {
                    _replyBufferBytes -= buffered;
                }
            }

            if (receiving)
            {
                channel.MarkHttpReceiving(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closed = true;
        }

        _capacity.Close();
        _client.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Текущее монотонное время в секундах (аналог <c>time.monotonic()</c>).</summary>
    internal static double CurrentTime() => Environment.TickCount64 / 1000.0;

    private static string GetCfRay(HttpResponseMessage response) =>
        response.Headers.TryGetValues("cf-ray", out var values) ? string.Concat(values) : "-";

    /// <summary>
    /// Читает тело ответа с ограничением размера и учётом буфера lane.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CfH2Channel channel,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        // Буфер чтения арендуется: один H2-запрос = один ответ, и на media-потоке такие
        // запросы идут непрерывно, поэтому 64 KiB на запрос из GC — лишняя нагрузка.
        var chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > Protocol.H2MaxPacket)
                {
                    throw new InvalidDataException("H2 response exceeds native packet bound");
                }

                lock (_sync)
                {
                    if (_replyBufferBytes + read > MaxLaneReplyBytes)
                    {
                        throw new InvalidDataException("H2 lane response buffers exceeded bound");
                    }

                    _replyBufferBytes += read;
                }

                buffer.Write(chunk, 0, read);
                channel.MarkProgress();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
