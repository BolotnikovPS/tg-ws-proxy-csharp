using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Web;

/// <summary>
/// HTTP-endpoints local Bot API-server. Register only when botApi.enabled=true.
/// Routes repeat official local Bot API: POST/GET /bot&lt;token&gt;/&lt;method&gt; and
/// GET /file/bot&lt;token&gt;/&lt;file_path&gt;. api_id/api_hash come from headers,
/// response uses the "{ok, result}" / "{ok:false, error_code, description}" contract.
/// </summary>
internal static class BotApiEndpoints
{
    /// <summary>Registers Bot API routes; does nothing when botApi is disabled.</summary>
    public static void MapBotApi(this IEndpointRouteBuilder endpoints, Config config)
    {
        if (!config.BotApi.Enabled)
        {
            return;
        }

        endpoints.MapGet("/bot{token}/{method}", HandleAsync);
        endpoints.MapPost("/bot{token}/{method}", HandleAsync);
        endpoints.MapGet("/file/bot{token}/{filePath}", HandleFileAsync);
        endpoints.MapPost("/file/bot{token}/{filePath}", HandleFileAsync);
    }

    private static async Task HandleAsync(HttpContext ctx, string token, string method, BotGateway gateway, Config config, ProxyMetrics metrics, BotApiRateLimiter rateLimiter)
    {
        metrics.IncBotApiRequest();

        // Лимит на бота применяется до разбора тела: поверх лимита multipart-загрузка не парсится.
        if (config.BotApi.RequestsPerSecondPerToken > 0
            && BotIdFromToken(token) is { } botId
            && !rateLimiter.TryAcquire(botId, config.BotApi.RequestsPerSecondPerToken))
        {
            metrics.IncBotApiRequestError();
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsJsonAsync(
                new { ok = false, error_code = 429, description = "Too Many Requests: rate limit exceeded" },
                ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        Dictionary<string, object> args;
        try
        {
            args = await ReadArgsAsync(ctx, config.BotApi.MaxUploadBytes).ConfigureAwait(false);
        }
        catch (BotGatewayException e) when (e.ErrorCode == StatusCodes.Status413PayloadTooLarge)
        {
            metrics.IncBotApiRequestError();
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(
                new { ok = false, error_code = e.ErrorCode, description = e.Message },
                ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        var apiId = ReadApiId(ctx);
        var apiHash = ctx.Request.Headers[BotGateway.ApiHashHeader].ToString();

        var result = await gateway.HandleAsync(token, method, args, apiId, apiHash, ctx.RequestAborted)
            .ConfigureAwait(false);

        if (result.Ok)
        {
            await ctx.Response.WriteAsJsonAsync(new { ok = true, result = result.Result }, ctx.RequestAborted)
                .ConfigureAwait(false);
        }
        else
        {
            metrics.IncBotApiRequestError();
            await ctx.Response.WriteAsJsonAsync(
                new { ok = false, error_code = result.ErrorCode, description = result.Description },
                ctx.RequestAborted).ConfigureAwait(false);
        }
    }

    private static int? ReadApiId(HttpContext ctx)
    {
        var raw = ctx.Request.Headers[BotGateway.ApiIdHeader].ToString();
        return int.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Извлекает числовой botId из токена (число до «:»). <c>null</c>, если токен пуст или
    /// префикс не числовой — тогда запрос не лимитируется, шлюз отклонит его своей логикой.
    /// </summary>
    private static long? BotIdFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var colon = token.IndexOf(':');
        var numberPart = colon > 0 ? token[..colon] : token;
        return long.TryParse(numberPart, out var botId) ? botId : null;
    }

    /// <summary>Reads arguments: GET - query string (strings), POST JSON - body as dictionary, multipart - form files.</summary>
    private static async Task<Dictionary<string, object>> ReadArgsAsync(HttpContext ctx, long maxUploadBytes)
    {
        var args = new Dictionary<string, object>();

        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            foreach (var kv in ctx.Request.Query)
            {
                args[kv.Key] = kv.Value.ToString();
            }

            return args;
        }

        if (ctx.Request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // Некорректный multipart: словарь пуст — шлюз вернёт 400/501 по логике метода.
                return [];
            }

            long totalBytes = 0;
            foreach (var file in form.Files)
            {
                totalBytes += file.Length;
            }

            if (maxUploadBytes > 0 && totalBytes > maxUploadBytes)
            {
                throw new BotGatewayException(StatusCodes.Status413PayloadTooLarge, "Payload too large");
            }

            foreach (var kv in form)
            {
                args[kv.Key] = kv.Value.ToString();
            }

            foreach (var file in form.Files)
            {
                args[file.Name] = new BotUploadFile(file.FileName, file.ContentType, file.OpenReadStream());
            }

            return args;
        }

        // Прочие Content-Type (не multipart и не JSON) — аргументов нет, шлюз вернёт 400/501 по логике метода.
        if (!ctx.Request.HasJsonContentType())
        {
            return args;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>(options, ctx.RequestAborted)
                .ConfigureAwait(false);
            if (body != null)
            {
                args = body;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON: gateway returns 400 (args are unused for getMe).
            return new Dictionary<string, object> { ["__bad_json__"] = true };
        }

        return args;
    }

    private static async Task HandleFileAsync(HttpContext ctx, string token, string filePath, BotGateway gateway)
    {
        // file_path — случайная неотгадываемая строка, поэтому token здесь не проверяется.
        _ = token;

        if (gateway.TryGetFile(filePath, out var data, out var contentType))
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = contentType ?? "application/octet-stream";
            ctx.Response.ContentLength = data.Length;
            await ctx.Response.Body.WriteAsync(data, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(
            new { ok = false, error_code = 404, description = "file not found" },
            ctx.RequestAborted).ConfigureAwait(false);
    }
}
