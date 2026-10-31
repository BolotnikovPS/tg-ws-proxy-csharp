using Serilog.Context;
using TgWsProxy.Infrastructure.Logging;

namespace TgWsProxy.Web;

/// <summary>
/// Кладёт идентификатор корреляции запроса в окружение логирования (<see cref="LogContext"/>) и
/// эхом возвращает его в заголовке X-Correlation-Id. Принимает входящий заголовок для сквозной
/// корреляции от вышестоящего компонента (реверс-прокси). Имена заголовков/свойств — единые
/// константы <see cref="LoggingConstants"/>.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Обрабатывает запрос: устанавливает/пробрасывает идентификатор корреляции.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[LoggingConstants.CorrelationIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items[LoggingConstants.CorrelationIdItemKey] = correlationId;
        context.Response.Headers[LoggingConstants.CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty(LoggingConstants.CorrelationIdProperty, correlationId))
        {
            await next(context);
        }
    }
}