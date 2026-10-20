using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Устанавливает TCP(+TLS) соединение и выполняет WebSocket-рукопожатие.
/// Порт upstream <c>RawWebSocket.connect</c>: поддержка <c>--no-secure</c> (ws:// на 80)
/// и domain fronting через параметр <paramref name="sni"/>.
/// </summary>
internal sealed class RawWebSocketFactory(ILogger<RawWebSocketFactory> logger, Config cfg) : IRawWebSocketFactory
{
    /// <summary>Верхняя граница на TCP+TLS фазу (как upstream <c>min(timeout, 10)</c>).</summary>
    private const double TcpTlsHandshakeCapSeconds = 10;

    /// <summary>Максимальный размер блока чтения заголовков ответа.</summary>
    private const int HeaderReadChunk = 8 * 1024;

    public async Task<IRawWebSocket> Connect(
        string ip,
        string domain,
        string path,
        string scope,
        TimeSpan? timeout = null,
        bool secure = true,
        string? sni = null)
    {
        var handshakeTimeout = timeout ?? TimeSpan.FromSeconds(cfg.WsConnectTimeoutSeconds);
        var establishTimeout = TimeSpan.FromSeconds(Math.Min(handshakeTimeout.TotalSeconds, TcpTlsHandshakeCapSeconds));
        var socketBuf = Math.Max(4, cfg.SocketBufferKb) * 1024;
        var port = secure ? Protocol.WsPort : Protocol.WsPlainPort;

        // Upstream отдаёт host прямо в asyncio.open_connection, который резолвит DNS сам.
        // Здесь host — это либо литеральный IP (--dc-ip, пул WS), либо домен
        // (CF proxy/CF worker, то есть fronting через Cloudflare), поэтому резолвим по необходимости.
        var address = await ResolveAddressAsync(ip, domain, scope, establishTimeout).ConfigureAwait(false);

        var ipEndPoint = new IPEndPoint(address, port);
        var client = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        Stream? transport = null;
        try
        {
            try
            {
                using var connectCts = new CancellationTokenSource(establishTimeout);
                await client.ConnectAsync(ipEndPoint, connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                var ex = new TimeoutException($"TCP connect timed out for {domain}");
                LogConnectTimeout(scope, domain, "TCP connect", ex);
                throw ex;
            }
            catch (Exception ex)
            {
                if (IsBackgroundConnect(scope))
                {
                    logger.LogWarning(ex, "[{Scope}] TCP connect failed {Ip}:{Port} (проверьте сеть, файрвол и соответствие DC:IP в --dc-ip)", scope, ipEndPoint, port);
                    throw;
                }

                logger.LogError(ex, "[{Scope}] TCP connect failed {Ip}:{Port}", scope, ipEndPoint, port);
                throw;
            }

            client.NoDelay = true;
            client.ReceiveBufferSize = socketBuf;
            client.SendBufferSize = socketBuf;

            var networkStream = new NetworkStream(client, ownsSocket: false);
            if (secure)
            {
                var targetHost = sni ?? domain;
                var fronting = sni is not null;
                var ssl = new SslStream(networkStream, false, (_, _, _, sslPolicyErrors) =>
                {
                    if (cfg.AllowInvalidCertificates)
                    {
                        return true;
                    }

                    // При fronting имя в сертификате заведомо другое — проверяем только цепочку
                    // (аналог upstream check_hostname=False).
                    if (fronting)
                    {
                        return (sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) == 0;
                    }

                    return sslPolicyErrors == SslPolicyErrors.None;
                });

                try
                {
                    using var tlsCts = new CancellationTokenSource(establishTimeout);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = targetHost,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, tlsCts.Token);
                }
                catch (OperationCanceledException)
                {
                    var ex = new TimeoutException($"TLS handshake timed out for {targetHost}");
                    LogConnectTimeout(scope, domain, "TLS handshake", ex);
                    throw ex;
                }
                catch (Exception ex)
                {
                    LogTlsFailure(scope, domain, ex);
                    throw;
                }

                transport = ssl;
            }
            else
            {
                transport = networkStream;
            }

            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var req = $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
            try
            {
                using var writeCts = new CancellationTokenSource(handshakeTimeout);
                await transport.WriteAsync(Encoding.ASCII.GetBytes(req), writeCts.Token);
            }
            catch (OperationCanceledException)
            {
                var ex = new TimeoutException($"WS request write timed out for {domain}");
                LogConnectTimeout(scope, domain, "WS request write", ex);
                throw ex;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Scope}] Failed to send WS handshake request", scope);
                throw;
            }

            var (lines, leftover) = await ReadResponseHead(transport, handshakeTimeout, scope, domain);

            if (lines.Count == 0 || !lines[0].Contains(" 101 "))
            {
                var statusLine = lines.Count == 0 ? "empty handshake response" : lines[0];
                var statusCode = ParseStatusCode(statusLine);
                var headers = ParseHeaders(lines);
                logger.LogWarning("[{Scope}] WS handshake rejected for {Domain}: {Response}", scope, domain, statusLine);
                throw new WsHandshakeException(statusCode, statusLine, headers);
            }

            // Байты, попавшие в буфер вместе с заголовками, отдаём обратно потоку,
            // иначе потеряем начало первых WS-фреймов.
            Stream wsTransport = leftover.Length > 0
                ? new PrebufferedStream(transport, leftover)
                : transport;

            return new RawWebSocket(client, wsTransport, scope, logger, cfg.WsMaxFrameBytes);
        }
        catch
        {
            try { transport?.Dispose(); } catch { }
            try { client.Close(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Читает блок заголовков HTTP-ответа до пустой строки, возвращая строки и «хвост» байтов,
    /// который относится уже к WebSocket-потоку.
    /// </summary>
    private static async Task<(List<string> Lines, byte[] Leftover)> ReadResponseHead(
        Stream transport,
        TimeSpan timeout,
        string scope,
        string domain)
    {
        // Заголовки читаются на каждом новом WS-соединении (в том числе при прогреве пула),
        // поэтому буфер арендуется в пуле, а не создаётся заново: List<byte> с побайтовым
        // Add на каждый ответ заметно дороже по аллокациям и копированию.
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderReadChunk);
        var pending = ArrayPool<byte>.Shared.Rent(HeaderReadChunk);
        var count = 0;

        try
        {
            while (true)
            {
                var headEnd = FindHeaderEnd(pending, count);
                if (headEnd >= 0)
                {
                    var text = Encoding.ASCII.GetString(pending, 0, headEnd);
                    var leftover = pending.AsSpan(headEnd, count - headEnd).ToArray();
                    return (SplitLines(text), leftover);
                }

                using var readCts = new CancellationTokenSource(timeout);
                int read;
                try
                {
                    read = await transport.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"WS response read timed out for {domain}");
                }

                if (read <= 0)
                {
                    // Соединение закрыто до конца заголовков: отдаём то, что успели прочитать.
                    var text = Encoding.ASCII.GetString(pending, 0, count);
                    return (SplitLines(text), []);
                }

                if (count + read > pending.Length)
                {
                    // Блок заголовков вырос: расширяем буфер с запасом и возвращаем старый в пул.
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Max(count + read, pending.Length * 2));
                    Buffer.BlockCopy(pending, 0, grown, 0, count);
                    ArrayPool<byte>.Shared.Return(pending);
                    pending = grown;
                }

                Buffer.BlockCopy(buffer, 0, pending, count, read);
                count += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(pending);
        }
    }

    /// <summary>
    /// Ищет конец блока заголовков (<c>\r\n\r\n</c> либо <c>\n\n</c>).
    /// </summary>
    /// <returns>Индекс первого байта после заголовков либо -1, если блок ещё не завершён.</returns>
    private static int FindHeaderEnd(byte[] data, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (data[i] != (byte)'\n')
            {
                continue;
            }

            if (i + 1 < count && data[i + 1] == (byte)'\n')
            {
                return i + 2;
            }

            if (i + 2 < count && data[i + 1] == (byte)'\r' && data[i + 2] == (byte)'\n')
            {
                return i + 3;
            }
        }

        return -1;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// Разбирает заголовки ответа (нужен <c>Location</c> для диагностики редиректов).
    /// </summary>
    private static Dictionary<string, string> ParseHeaders(List<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Count; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx > 0)
            {
                headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
            }
        }

        return headers;
    }

    /// <summary>
    /// Резолвит хост подключения. Литеральный IP берётся как есть; домен резолвится через DNS
    /// (IPv4 в приоритете), как это делает upstream <c>asyncio.open_connection</c>. Нужно для
    /// маршрутов CF proxy/CF worker, где целевой хост — это домен, а не адрес DC.
    /// </summary>
    private async Task<IPAddress> ResolveAddressAsync(
        string host,
        string domain,
        string scope,
        TimeSpan timeout)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        try
        {
            using var dnsCts = new CancellationTokenSource(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, dnsCts.Token).ConfigureAwait(false);

            var chosen = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (chosen is null)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            logger.LogDebug("[{Scope}] Resolved {Host} -> {Address} ({Domain})", scope, host, chosen, domain);
            return chosen;
        }
        catch (OperationCanceledException)
        {
            var ex = new TimeoutException($"DNS resolution timed out for {host}");
            LogConnectTimeout(scope, domain, "DNS lookup", ex);
            throw ex;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            logger.LogWarning(ex, "[{Scope}] DNS resolution failed for {Host} ({Domain})", scope, host, domain);
            throw;
        }
    }

    /// <summary>
    /// Определяет фоновые соединения пулов (WS-пул, CF worker pool). Их ошибки — ожидаемый фон
    /// (аналог upstream <c>log.debug("WS pool connect failed: %r")</c>), поэтому не шумят как аварии.
    /// </summary>
    private static bool IsBackgroundConnect(string scope)
        => scope.StartsWith("pool ", StringComparison.OrdinalIgnoreCase) ||
           scope.StartsWith("cf-worker ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Логирует падение TLS-рукопожатия, отделяя типовые сетевые причины (DPI, неверный IP).
    /// </summary>
    private void LogTlsFailure(string scope, string domain, Exception ex)
    {
        if (!IsBackgroundConnect(scope))
        {
            logger.LogError(ex, "[{Scope}] TLS handshake failed for {Domain}", scope, domain);
            return;
        }

        // RST / EOF на TLS при фоновом соединении пула — типично DPI, блокировка или неверный IP для SNI;
        // не считаем это «аварией» приложения
        if (IsConnectionReset(ex))
        {
            logger.LogDebug(ex,
                "[{Scope}] TLS handshake: соединение сброшено (RST) для {Domain} — типично фильтрация/DPI или неверный IP для SNI; WSS может быть недоступен, остаётся TCP fallback",
                scope, domain);
            return;
        }

        if (IsPrematureTlsClose(ex))
        {
            logger.LogDebug(ex,
                "[{Scope}] TLS handshake: неожиданный EOF для {Domain} — часто обрыв без ServerHello (DPI/фильтр, неверный --dc-ip); WSS недоступен → TCP fallback",
                scope, domain);
            return;
        }

        logger.LogWarning(ex,
            "[{Scope}] TLS handshake failed for {Domain} (часто: блокировка Telegram, неверный --dc-ip для этого DC, прокси/VPN на хосте; при недоступности WSS будет TCP fallback)",
            scope, domain);
    }

    private static bool IsConnectionReset(Exception ex)
    {
        if (ex is SocketException se)
        {
            return se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
        }

        return ex.InnerException is SocketException inner &&
               inner.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
    }

    /// <summary>
    /// Проверяет обрыв TLS до завершения рукопожатия (EOF / 0 bytes) — как у RST, часто сеть, а не
    /// баг кода.
    /// </summary>
    private static bool IsPrematureTlsClose(Exception ex)
    {
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
        {
            if (e is EndOfStreamException)
            {
                return true;
            }

            if (e is IOException io)
            {
                var msg = io.Message;
                if (msg.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("0 bytes from the transport", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void LogConnectTimeout(string scope, string domain, string stage, TimeoutException ex)
    {
        if (IsBackgroundConnect(scope))
        {
            logger.LogDebug(ex, "[{Scope}] {Stage} timed out for {Domain}", scope, stage, domain);
            return;
        }

        logger.LogWarning(ex, "[{Scope}] {Stage} timed out for {Domain}", scope, stage, domain);
    }

    /// <summary>
    /// Извлекает числовой HTTP-статус из первой строки ответа.
    /// </summary>
    /// <param name="statusLine">Строка статуса HTTP-ответа.</param>
    /// <returns>Код HTTP-статуса или 0, если распарсить не удалось.</returns>
    private static int ParseStatusCode(string statusLine)
    {
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }
}
