using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.WebProxy;

namespace TgWsProxy.Web;

/// <summary>
/// Bridge-листенер WEB proxy (browser-relay): статическая страница на <c>GET /</c> и
/// WebSocket-релей на <c>GET /transport</c> (F-W.1..F-W.15). Bridge делит один Kestrel-хост
/// с панелью, но слушает отдельный порт <see cref="Config.WebProxyPort"/>, поэтому маршруты
/// различаются по <see cref="ConnectionInfo.LocalPort"/>. Регистрируется до статических файлов
/// панели, чтобы перехватывать <c>GET /</c> на bridge-порту.
/// </summary>
internal static class WebProxyBridge
{
    private const string TransportSuffix = "/transport";

    private const string BridgePageHtml = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>WEB proxy bridge</title></head>
        <body>
        <h1>WEB proxy bridge</h1>
        <p>This endpoint terminates the Telegram WEB proxy (browser-relay) transport.</p>
        <p>The relay WebSocket is served at <code>{basePath}/transport</code>.</p>
        </body>
        </html>
        """;

    /// <summary>Регистрирует bridge-конвейер: WS-апгрейд и маршруты bridge-сайта.</summary>
    public static void Map(WebApplication app)
    {
        app.UseWebSockets();

        app.Use(async (context, next) =>
        {
            var cfg = context.RequestServices.GetRequiredService<Config>();
            if (!cfg.WebProxyEnabled || context.Connection.LocalPort != cfg.WebProxyPort)
            {
                await next();
                return;
            }

            var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
            var path = context.Request.Path;

            if (IsTransportPath(path, basePath))
            {
                await HandleTransportAsync(context, basePath);
                return;
            }

            if (IsRootPath(path, basePath))
            {
                await ServeBridgePageAsync(context);
                return;
            }

            // Bridge-порт обслуживает только bridge-маршруты; панель сюда не просачивается.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    private static async Task HandleTransportAsync(HttpContext context, string basePath)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("web-proxy");

        try
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            var capability = context.Request.Query["bridge"].ToString();
            var host = context.Request.Host.Host;
            var session = context.RequestServices.GetRequiredService<IWebProxyRelaySession>();
            var token = session.ResolveToken(host, capability);
            if (token is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("invalid bridge capability");
                return;
            }

            var peer = context.Connection.RemoteIpAddress;
            var scope = $"web-proxy|{context.Connection.Id}";
            var ws = await context.WebSockets.AcceptWebSocketAsync();
            logger.LogDebug("[{Scope}] web-proxy transport accepted token={Token}", scope, token.Name);

            using var transport = new WebSocketRelayTransport(ws);
            await session.RunAsync(transport, token, peer, scope, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "web-proxy transport failed");
            context.Abort();
        }
    }

    private static Task ServeBridgePageAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        var cfg = context.RequestServices.GetRequiredService<Config>();
        var basePath = BridgeCapabilityVerifier.NormalizeBasePath(cfg.WebProxyBasePath);
        return context.Response.WriteAsync(BridgePageHtml.Replace("{basePath}", basePath));
    }

    private static bool IsRootPath(PathString path, string basePath)
    {
        if (basePath.Length == 0)
        {
            return path == "/" || path == "";
        }

        return path == basePath || path == basePath + "/";
    }

    private static bool IsTransportPath(PathString path, string basePath)
        => path == basePath + TransportSuffix;

    /// <summary>Адаптер relay-транспорта поверх server-side <see cref="WebSocket"/>.</summary>
    private sealed class WebSocketRelayTransport(WebSocket ws) : IRelayTransport, IDisposable
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken ct)
            => ws.SendAsync(frameBytes, WebSocketMessageType.Binary, true, ct);

        public async ValueTask<RelayFrame?> ReceiveAsync(CancellationToken ct)
        {
            var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var message = new MemoryStream();
                while (true)
                {
                    var result = await ws.ReceiveAsync(scratch, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        throw new InvalidDataException("non-binary WebSocket message in relay transport");
                    }

                    message.Write(scratch, 0, result.Count);
                    if (message.Length > RelayFrameCodec.FrameSize(RelayFrameCodec.MaxFramePayload))
                    {
                        throw new InvalidDataException("relay frame exceeds maximum payload");
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                if (!message.TryGetBuffer(out var segment) || segment.Array is null)
                {
                    throw new InvalidDataException("relay frame buffer unavailable");
                }

                var buffer = new ReadOnlyMemory<byte>(segment.Array, segment.Offset, segment.Count);
                if (!RelayFrameCodec.TryParse(buffer, out var frame, out _))
                {
                    throw new InvalidDataException("malformed relay frame");
                }

                // Payload копируется: буфер MemoryStream освобождается при возврате, а кадр
                // обрабатывается мультиплексором уже после ReceiveAsync.
                return new RelayFrame(frame.Type, frame.StreamId, frame.Payload.ToArray());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public void Dispose() => ws.Dispose();
    }
}
