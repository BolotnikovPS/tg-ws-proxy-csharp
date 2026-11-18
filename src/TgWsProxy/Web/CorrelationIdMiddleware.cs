using Serilog.Context;
using TgWsProxy.Domain;

namespace TgWsProxy.Web;

/// <summary>
/// Кладёт идентификатор корреляции запроса в окружение логирования (<see cref="LogContext"/>) и
/// эхом возвращает его в заголовке X-Correlation-Id. Принимает входящий заголовок для сквозной
/// корреляции от вышестоящего компонента (реверс-прокси). Имена заголовков/свойств — единые
/// константы <see cref="AppConstants.Logging"/>.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Обрабатывает запрос: устанавливает/пробрасывает идентификатор корреляции.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[AppConstants.Logging.CorrelationIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items[AppConstants.Logging.CorrelationIdItemKey] = correlationId;
        context.Response.Headers[AppConstants.Logging.CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty(AppConstants.Logging.CorrelationIdProperty, correlationId))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
