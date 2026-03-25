#nullable enable

using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Infrastructure;

internal sealed class RawWebSocketFactory(ILogger<RawWebSocketFactory> logger, Config cfg) : IRawWebSocketFactory
{
    private const double TcpTlsHandshakeCapSeconds = 10;

    public async Task<IRawWebSocket> ConnectAsync(string ip, string domain, string path, string scope, TimeSpan? timeout = null)
    {
        var handshakeTimeout = timeout ?? TimeSpan.FromSeconds(cfg.WsConnectTimeoutSeconds);
        var establishTimeout = TimeSpan.FromSeconds(Math.Min(handshakeTimeout.TotalSeconds, TcpTlsHandshakeCapSeconds));
        var socketBuf = Math.Max(4, cfg.SocketBufferKb) * 1024;
        var client = new TcpClient();
        SslStream? ssl = null;
        try
        {
            try
            {
                using var connectCts = new CancellationTokenSource(establishTimeout);
                await client.ConnectAsync(ip, 443, connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                var ex = new TimeoutException($"TCP connect timed out for {domain}");
                LogConnectTimeout(scope, domain, "TCP connect", ex);
                throw ex;
            }
            catch (Exception ex)
            {
                if (IsWarmup(scope))
                {
                    logger.LogWarning(ex, "[{Scope}] TCP connect failed {Ip}:443 (проверьте сеть, файрвол и соответствие DC:IP в --dc-ip)", scope, ip);
                }
                else
                {
                    logger.LogError(ex, "[{Scope}] TCP connect failed {Ip}:443", scope, ip);
                }
                throw;
            }

            client.NoDelay = true;
            client.ReceiveBufferSize = socketBuf;
            client.SendBufferSize = socketBuf;

            ssl = new SslStream(client.GetStream(), false, (_, _, _, sslPolicyErrors) =>
            {
                if (cfg.AllowInvalidCertificates)
                {
                    return true;
                }

                return sslPolicyErrors == SslPolicyErrors.None;
            });
            try
            {
                using var tlsCts = new CancellationTokenSource(establishTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = domain,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, tlsCts.Token);
            }
            catch (OperationCanceledException)
            {
                var ex = new TimeoutException($"TLS handshake timed out for {domain}");
                LogConnectTimeout(scope, domain, "TLS handshake", ex);
                throw ex;
            }
            catch (Exception ex)
            {
                if (IsWarmup(scope))
                {
                    // RST / EOF на TLS при warmup — типично DPI, блокировка или неверный IP для
                    // SNI; не считаем это «аварией» приложения
                    if (IsConnectionReset(ex))
                    {
                        logger.LogDebug(ex,
                            "[{Scope}] TLS handshake: соединение сброшено (RST) для {Domain} — типично фильтрация/DPI или неверный IP для SNI; WSS может быть недоступен, остаётся TCP fallback",
                            scope, domain);
                    }
                    else if (IsPrematureTlsClose(ex))
                    {
                        logger.LogDebug(ex,
                            "[{Scope}] TLS handshake: неожиданный EOF для {Domain} — часто обрыв без ServerHello (DPI/фильтр, неверный --dc-ip); WSS недоступен → TCP fallback",
                            scope, domain);
                    }
                    else
                    {
                        logger.LogWarning(ex,
                            "[{Scope}] TLS handshake failed for {Domain} (часто: блокировка Telegram, неверный --dc-ip для этого DC, прокси/VPN на хосте; при недоступности WSS будет TCP fallback)",
                            scope, domain);
                    }
                }
                else
                {
                    logger.LogError(ex, "[{Scope}] TLS handshake failed for {Domain}", scope, domain);
                }
                throw;
            }

            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var req = $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n" +
                      "Origin: https://web.telegram.org\r\n" +
                      "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                      "Chrome/131.0.0.0 Safari/537.36\r\n\r\n";
            try
            {
                using var writeCts = new CancellationTokenSource(handshakeTimeout);
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(req), writeCts.Token);
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

            var lines = new List<string>();
            while (true)
            {
                string line;
                try
                {
                    using var readCts = new CancellationTokenSource(handshakeTimeout);
                    line = await ReadLine(ssl, readCts.Token);
                }
                catch (OperationCanceledException)
                {
                    var ex = new TimeoutException($"WS response read timed out for {domain}");
                    LogConnectTimeout(scope, domain, "WS response read", ex);
                    throw ex;
                }

                if (line.Length == 0)
                {
                    break;
                }

                lines.Add(line);
            }

            if (lines.Count == 0 || !lines[0].Contains(" 101 "))
            {
                var statusLine = lines.Count == 0 ? "empty handshake response" : lines[0];
                logger.LogWarning("[{Scope}] WS handshake rejected for {Domain}: {Response}", scope, domain, statusLine);
                throw new WsHandshakeException(ParseStatusCode(statusLine), statusLine);
            }

            return new RawWebSocket(client, ssl, scope, logger, cfg.WsMaxFrameBytes);
        }
        catch
        {
            try { ssl?.Dispose(); } catch { }
            try { client.Close(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Логирует таймаут этапа подключения с разным уровнем важности для warmup и боевого трафика.
    /// </summary>
    /// <param name="scope">Идентификатор скоупа логирования.</param>
    /// <param name="domain">Домен, к которому выполнялось подключение.</param>
    /// <param name="stage">Этап подключения (TCP, TLS, handshake).</param>
    /// <param name="ex">Исключение таймаута.</param>
    private static bool IsWarmup(string scope)
        => string.Equals(scope, "warmup", StringComparison.OrdinalIgnoreCase);

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
        if (string.Equals(scope, "warmup", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Считывает одну CRLF-строку из потока без завершающих символов перевода строки.
    /// </summary>
    /// <param name="s">Поток чтения.</param>
    /// <param name="cancellationToken">Токен отмены операции чтения.</param>
    /// <returns>Считанная строка без CRLF.</returns>
    private static async Task<string> ReadLine(Stream s, CancellationToken cancellationToken)
    {
        var b = new List<byte>();
        while (true)
        {
            var one = await IoUtil.ReadExact(s, 1, cancellationToken);
            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] != '\r')
            {
                b.Add(one[0]);
            }
        }
        return Encoding.ASCII.GetString([.. b]);
    }
}
