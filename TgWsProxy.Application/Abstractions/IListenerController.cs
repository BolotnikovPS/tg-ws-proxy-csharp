#nullable enable

using TgWsProxy.Domain;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Перезапуск прокси-листенера внутри работающего процесса (F-7.1). Нужен ключам категории
/// «слушатель» (§ 4): смена <c>host</c>, <c>port</c>, <c>dcIp</c>, TLS- и CF-настроек
/// применяется без перезапуска процесса и без пересоздания DI-контейнера.
/// </summary>
public interface IListenerController
{
    /// <summary>Текущее состояние листенера.</summary>
    ListenerState State { get; }

    /// <summary>Сколько раз листенер перезапускался с начала работы процесса.</summary>
    int RestartCount { get; }

    /// <summary>Время последнего перезапуска (UTC) или <see langword="null"/>.</summary>
    DateTimeOffset? LastRestartAt { get; }

    /// <summary>
    /// Перезапускает листенер на привязке из <paramref name="candidate"/> в порядке F-7.2
    /// и переносит значения кандидата в живой <see cref="Config"/>.
    /// </summary>
    /// <param name="candidate">Полная проверенная конфигурация, готовая к применению.</param>
    /// <param name="cancellationToken">Отмена операции (таймаут задаёт вызывающая сторона).</param>
    /// <returns>Итог: успех с числом разорванных сессий либо откат с причиной.</returns>
    /// <exception cref="ListenerRestartInProgressException">
    /// Перезапуск уже идёт: второй запрос отклоняется (F-7.6).
    /// </exception>
    Task<ListenerRestartResult> RestartAsync(Config candidate, CancellationToken cancellationToken);
}
