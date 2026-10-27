namespace TgWsProxy.Infrastructure.Logging;

/// <summary>
/// Единые строковые константы логирования и корреляции. Собраны в одном месте, чтобы имена
/// структурированных свойств логов, ключи <c>HttpContext.Items</c> и HTTP-заголовки не
/// разъезжались между слоями: Infrastructure пушит те же имена через <c>ILogger.BeginScope</c>,
/// по которым Host раскладывает события в энричерах и шаблоне консоли.
/// </summary>
public static class LoggingConstants
{
    /// <summary>Имя HTTP-заголовка корреляции запроса.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>Ключ в <c>HttpContext.Items</c> для CorrelationId.</summary>
    public const string CorrelationIdItemKey = "CorrelationId";

    /// <summary>Имя структурированного свойства CorrelationId в событиях лога.</summary>
    public const string CorrelationIdProperty = "CorrelationId";

    /// <summary>Имя свойства идентификатора соединения (TCP-сессия).</summary>
    public const string ConnectionId = "ConnectionId";

    /// <summary>Имя свойства адреса клиента.</summary>
    public const string Peer = "Peer";

    /// <summary>Имя свойства идентификатора токена доступа.</summary>
    public const string TokenId = "TokenId";

    /// <summary>Имя свойства номера дата-центра (DC).</summary>
    public const string Dc = "Dc";

    /// <summary>Имя свойства признака media-DC.</summary>
    public const string Media = "Media";

    /// <summary>Имя свойства TraceId (W3C, из <c>Activity</c>).</summary>
    public const string TraceId = "TraceId";

    /// <summary>Имя свойства SpanId (W3C).</summary>
    public const string SpanId = "SpanId";

    /// <summary>Имя свойства хоста запроса (request-logging).</summary>
    public const string RequestHost = "RequestHost";

    /// <summary>Имя свойства User-Agent (request-logging).</summary>
    public const string UserAgent = "UserAgent";

    /// <summary>Имя глобального свойства «приложение».</summary>
    public const string Application = "Application";

    /// <summary>Имя глобального свойства «версия».</summary>
    public const string Version = "Version";

    /// <summary>Имя приложения, зашитое в глобальный энричер.</summary>
    public const string ApplicationName = "tg-ws-proxy";

    /// <summary>Значение, которым маскируются секреты при деструктуризации.</summary>
    public const string MaskedSecret = "***";

    /// <summary>Типы, чьи объекты целиком маскируются (защитная сетка <c>{@…}</c>).</summary>
    public static readonly IReadOnlySet<string> SensitiveTypeNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Config",
            "ConfigDto",
            "ProxyToken",
            "ProxyTokenEntry",
        };

    /// <summary>Префикс пути health-проверок (шум request-logging).</summary>
    public const string HealthzPathPrefix = "/healthz";

    /// <summary>Префикс пути метрик Prometheus (шум request-logging).</summary>
    public const string MetricsPathPrefix = "/metrics";

    /// <summary>Префикс пути Swagger (шум request-logging).</summary>
    public const string SwaggerPathPrefix = "/swagger";

    /// <summary>Сегмент пути WEB-proxy carrier (<c>/api/v1/*</c>, шум request-logging).</summary>
    public const string ApiV1PathSegment = "/api/v1/";
}