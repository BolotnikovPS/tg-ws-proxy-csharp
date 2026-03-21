using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Exceptions;

namespace TgWsProxy.Infrastructure;

internal sealed class RawWebSocketFactory(ILogger<RawWebSocketFactory> logger) : IRawWebSocketFactory
{
    public async Task<IRawWebSocket> ConnectAsync(string ip, string domain, string path, string scope, TimeSpan? timeout = null)
    {
        var connectTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(ip, 443).WaitAsync(connectTimeout);
        }
        catch (TimeoutException ex)
        {
            LogConnectTimeout(scope, domain, "TCP connect", ex);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TCP connect failed {Ip}:443", scope, ip);
            throw;
        }

        client.NoDelay = true;
        client.ReceiveBufferSize = 256 * 1024;
        client.SendBufferSize = 256 * 1024;

        var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = domain,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }).WaitAsync(connectTimeout);
        }
        catch (TimeoutException ex)
        {
            LogConnectTimeout(scope, domain, "TLS handshake", ex);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TLS handshake failed for {Domain}", scope, domain);
            throw;
        }

        var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var req = $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                  $"Sec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n" +
                  "Origin: https://web.telegram.org\r\nUser-Agent: tg-ws-proxy2-csharp\r\n\r\n";
        try
        {
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(req)).AsTask().WaitAsync(connectTimeout);
        }
        catch (TimeoutException ex)
        {
            LogConnectTimeout(scope, domain, "WS request write", ex);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Failed to send WS handshake request", scope);
            throw;
        }

        var lines = new List<string>();
        while (true)
        {
            var line = await ReadLine(ssl).WaitAsync(connectTimeout);
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

        return new RawWebSocket(client, ssl, scope, logger);
    }

    /// <summary>
    /// Логирует таймаут этапа подключения с разным уровнем важности для warmup и боевого трафика.
    /// </summary>
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
    private static int ParseStatusCode(string statusLine)
    {
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }

    /// <summary>
    /// Считывает одну CRLF-строку из потока без завершающих символов перевода строки.
    /// </summary>
    private static async Task<string> ReadLine(Stream s)
    {
        var b = new List<byte>();
        while (true)
        {
            var one = await IoUtil.ReadExact(s, 1);
            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] != '\r')
            {
                b.Add(one[0]);
            }
        }
        return Encoding.ASCII.GetString(b.ToArray());
    }
}
