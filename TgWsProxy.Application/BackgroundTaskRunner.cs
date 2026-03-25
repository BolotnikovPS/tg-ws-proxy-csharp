using Microsoft.Extensions.Logging;

namespace TgWsProxy.Application;

/// <summary>
/// Унифицированный запуск фоновых задач с безопасной обработкой исключений.
/// </summary>
public static class BackgroundTaskRunner
{
    /// <summary>
    /// Запускает фоновую задачу и гарантирует наблюдение ее исключений.
    /// </summary>
    /// <param name="work">Асинхронная операция, принимающая токен отмены.</param>
    /// <param name="logger">Логгер для записи ошибок.</param>
    /// <param name="description">Краткое описание задачи для логов.</param>
    /// <param name="cancellationToken">Токен отмены задачи.</param>
    /// <param name="logCancellation">Логировать ли штатную отмену задачи.</param>
    public static void RunDetachedSafe(
        Func<CancellationToken, Task> work,
        ILogger logger,
        string description,
        CancellationToken cancellationToken,
        bool logCancellation = false)
        => _ = Task.Run(async () =>
        {
            try
            {
                await work(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (logCancellation)
                {
                    logger.LogDebug("Background task canceled: {Description}", description);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background task failed: {Description}", description);
            }
        }, CancellationToken.None);
}
