using TgWsProxy.Application.ReadModels;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Чтение временного ряда счётчиков для <c>GET /api/metrics/timeseries</c> (F-3.3).
///
/// Кольцевой буфер живёт в Infrastructure (его наполняет фоновый сэмплер хоста), поэтому
/// обработчик запроса в слое Application зависит от этой абстракции, а не от класса буфера.
/// </summary>
public interface ITimeseriesReader
{
    /// <summary>
    /// Возвращает точки ряда за окно <paramref name="window"/> с шагом <paramref name="step"/>.
    /// </summary>
    /// <param name="window">Запрошенное окно истории.</param>
    /// <param name="step">Запрошенный шаг агрегации.</param>
    /// <param name="now">Момент отсчёта (передаётся явно ради воспроизводимости в тестах).</param>
    TimeseriesDto Query(TimeSpan window, TimeSpan step, DateTimeOffset now);
}