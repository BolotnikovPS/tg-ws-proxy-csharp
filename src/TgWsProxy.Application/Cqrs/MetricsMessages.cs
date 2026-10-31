namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Запрос сводки о процессе и подсистемах прокси для <c>GET /api/info</c> (F-3.6, F-7.8).
/// </summary>
public sealed record GetInfoQuery;

/// <summary>
/// Запрос снимка глобальных счётчиков для <c>GET /api/metrics</c> (F-3.2).
/// </summary>
public sealed record GetMetricsQuery;

/// <summary>
/// Запрос временного ряда для <c>GET /api/metrics/timeseries</c> (F-3.3).
/// </summary>
/// <param name="WindowSeconds">Желаемое окно в секундах; <c>null</c> — взять из конфигурации.</param>
/// <param name="StepSeconds">Желаемый шаг в секундах; <c>null</c> — взять из конфигурации.</param>
public sealed record GetTimeseriesQuery(int? WindowSeconds, int? StepSeconds);

/// <summary>
/// Запрос страницы статистики по парам «адрес, токен» для <c>GET /api/per-ip</c> (F-3.5, F-4.7, F-9.4).
/// </summary>
/// <param name="Offset">Смещение страницы; отрицательное значение трактуется как ноль.</param>
/// <param name="Limit">Размер страницы; <c>null</c> — умолчание, значение вне диапазона зажимается.</param>
/// <param name="IpPrefix">Префикс адреса для фильтрации (регистронезависимо).</param>
/// <param name="Group">Разрез: <c>pair</c>, <c>ip</c> или <c>token</c>.</param>
/// <param name="TokenId">Идентификатор токена для фильтрации.</param>
public sealed record GetPerIpQuery(
    int? Offset,
    int? Limit,
    string? IpPrefix,
    string? Group,
    string? TokenId);

/// <summary>
/// Запрос агрегатов по токенам целиком для <c>GET /api/metrics/by-token</c> (F-9.5).
/// </summary>
public sealed record GetTokenUsageQuery;

/// <summary>
/// Запрос проверки доступности дата-центров с хоста прокси для <c>POST /api/dc-check</c>.
/// </summary>
public sealed record RunDcCheckQuery;