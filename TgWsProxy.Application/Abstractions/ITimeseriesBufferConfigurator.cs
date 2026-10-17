#nullable enable

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Позволяет применить новые <c>timeseriesWindowSeconds</c>/<c>timeseriesStepSeconds</c>
/// кольцевого буфера без перезапуска процесса (F-2.11: ключи категории «горячо»).
///
/// Абстракция живёт в слое Application, потому что <c>ConfigUpdateService</c> не имеет права
/// ссылаться на Infrastructure, где находится сам буфер.
/// </summary>
public interface ITimeseriesBufferConfigurator
{
    /// <summary>Текущий шаг сэмплирования в секундах.</summary>
    int StepSeconds { get; }

    /// <summary>Текущее окно по умолчанию для запросов временного ряда в секундах.</summary>
    int WindowSeconds { get; }

    /// <summary>
    /// Пересоздаёт буфер под новые параметры. История сбрасывается: точки, снятые с прежним
    /// шагом, нельзя честно пересчитать под новый.
    /// </summary>
    /// <param name="stepSeconds">Новый шаг сэмплирования в секундах.</param>
    /// <param name="windowSeconds">Новое окно по умолчанию в секундах.</param>
    void Reconfigure(int stepSeconds, int windowSeconds);
}
