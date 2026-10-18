namespace TgWsProxy.Infrastructure.Metrics;

/// <summary>Одна точка временного ряда для графиков страницы (F-3.3).</summary>
/// <param name="Timestamp">Момент сэмпла (UTC).</param>
/// <param name="ConnectionsActive">Активные соединения в этот момент.</param>
/// <param name="BytesUpPerSecond">Скорость отдачи в Telegram, байт/с.</param>
/// <param name="BytesDownPerSecond">Скорость приёма от Telegram, байт/с.</param>
/// <param name="Errors">Прирост ошибок WebSocket за интервал шага.</param>
public sealed record TimeseriesPoint(
    DateTimeOffset Timestamp,
    long ConnectionsActive,
    double BytesUpPerSecond,
    double BytesDownPerSecond,
    long Errors);

/// <summary>Ответ <c>GET /api/metrics/timeseries</c>.</summary>
/// <param name="StepSeconds">Шаг агрегации в секундах.</param>
/// <param name="WindowSeconds">Фактически покрытое окно в секундах.</param>
/// <param name="Points">Точки в хронологическом порядке.</param>
public sealed record TimeseriesDto(int StepSeconds, int WindowSeconds, IReadOnlyList<TimeseriesPoint> Points);
