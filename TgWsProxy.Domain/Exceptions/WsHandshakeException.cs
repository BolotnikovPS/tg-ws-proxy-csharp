#nullable enable

namespace TgWsProxy.Domain.Exceptions;

/// <summary>
/// Ошибка WebSocket-рукопожатия: сервер ответил не 101. Порт upstream <c>WsHandshakeError</c>.
/// </summary>
public sealed class WsHandshakeException : Exception
{
    public WsHandshakeException(
        int statusCode,
        string statusLine,
        IReadOnlyDictionary<string, string>? headers = null)
        : base($"HTTP {statusCode}: {statusLine}")
    {
        StatusCode = statusCode;
        StatusLine = statusLine;
        Headers = headers ?? new Dictionary<string, string>();
        Location = Headers.TryGetValue("Location", out var location) ? location : null;
    }

    public int StatusCode { get; }

    public string StatusLine { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Заголовок <c>Location</c>, если сервер ответил редиректом.</summary>
    public string? Location { get; }

    public bool IsRedirect => StatusCode is 301 or 302 or 303 or 307 or 308;
}
