namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// HTTP-сервер служебных эндпоинтов <c>/metrics</c> (формат Prometheus) и <c>/healthz</c>
/// для систем мониторинга и Docker HEALTHCHECK. Расширение порта, в upstream отсутствует.
/// </summary>
public interface IMetricsServer
{
    /// <summary>
    /// Поднимает служебный listener и обрабатывает запросы до отмены токена.
    /// Ошибка привязки порта не прерывает работу прокси: сервер лишь логирует причину и выходит.
    /// </summary>
    /// <param name="cancellationToken">Токен остановки процесса.</param>
    Task Run(CancellationToken cancellationToken);
}
