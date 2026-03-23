namespace TgWsProxy.Domain.Exceptions;

public sealed class WsHandshakeException(int statusCode, string statusLine) : Exception($"HTTP {statusCode}: {statusLine}")
{
    public int StatusCode { get; } = statusCode;
    public string StatusLine { get; } = statusLine;
    public bool IsRedirect => StatusCode is 301 or 302 or 303 or 307 or 308;
}
