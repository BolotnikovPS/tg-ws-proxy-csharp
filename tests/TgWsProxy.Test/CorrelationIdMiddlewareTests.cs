using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;
using TgWsProxy.Infrastructure.Logging;
using TgWsProxy.Web;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты <see cref="CorrelationIdMiddleware"/>: генерация/эхо идентификатора корреляции и
/// пробрасывание его в структурированные события Serilog через <see cref="Serilog.Context.LogContext"/>.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Generates_Id_When_Absent()
    {
        var context = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var id = context.Response.Headers[LoggingConstants.CorrelationIdHeader].ToString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, context.Items[LoggingConstants.CorrelationIdItemKey]);
    }

    [Fact]
    public async Task Echoes_Incoming_Id()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[LoggingConstants.CorrelationIdHeader] = "cid-42";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("cid-42", context.Response.Headers[LoggingConstants.CorrelationIdHeader]);
        Assert.Equal("cid-42", context.Items[LoggingConstants.CorrelationIdItemKey]);
    }

    [Fact]
    public async Task Attaches_Id_To_LogContext()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext();
        context.Request.Headers[LoggingConstants.CorrelationIdHeader] = "cid-42";
        var middleware = new CorrelationIdMiddleware(_ =>
        {
            logger.Information("inside");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        var evt = sink.Events.Single();
        var correlationId = Assert.IsType<ScalarValue>(evt.Properties[LoggingConstants.CorrelationIdProperty]);
        Assert.Equal("cid-42", correlationId.Value);
    }
}