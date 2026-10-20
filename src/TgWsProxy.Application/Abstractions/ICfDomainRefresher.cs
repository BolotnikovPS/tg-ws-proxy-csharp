namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Инициализирует пул базовых доменов CF proxy и периодически обновляет его
/// (аналог upstream <c>start_cfproxy_domain_refresh</c>).
/// </summary>
public interface ICfDomainRefresher
{
    /// <summary>
    /// Подставляет стартовый список доменов и запускает фоновое обновление.
    /// Если домены заданы пользователем через <c>--cfproxy-domain</c>, обновление не запускается.
    /// </summary>
    /// <param name="cancellationToken">Токен остановки прокси.</param>
    void Start(CancellationToken cancellationToken);

    /// <summary>
    /// Останавливает фоновое обновление списка доменов.
    /// </summary>
    void Stop();
}
