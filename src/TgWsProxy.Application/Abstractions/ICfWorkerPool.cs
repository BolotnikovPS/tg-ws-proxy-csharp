namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Пул заранее открытых соединений через Cloudflare Worker (аналог upstream <c>_CfWorkerPool</c>).
/// </summary>
public interface ICfWorkerPool
{
    /// <summary>
    /// Пытается получить готовое соединение воркера для указанного DC.
    /// </summary>
    /// <param name="dc">Номер дата-центра.</param>
    /// <param name="fallbackDst">Целевой IP Telegram, который воркер должен проксировать.</param>
    /// <param name="workerDomains">Настроенные домены воркеров.</param>
    /// <returns>Соединение и домен, через который оно открыто, либо <see langword="null"/>.</returns>
    (IRawWebSocket Ws, string Domain)? TryGet(int dc, string fallbackDst, IReadOnlyList<string> workerDomains);

    /// <summary>
    /// Возвращает домены воркеров, доступные для новых попыток (перемешанные, без «исчерпанных»).
    /// </summary>
    /// <param name="workerDomains">Настроенные домены воркеров.</param>
    IReadOnlyList<string> AvailableDomains(IReadOnlyList<string> workerDomains);

    /// <summary>
    /// Регистрирует неудачную попытку подключения к домену воркера.
    /// </summary>
    /// <param name="workerDomain">Домен воркера.</param>
    /// <param name="exception">Исключение, из-за которого попытка не удалась.</param>
    void ReportFailure(string workerDomain, Exception exception);

    /// <summary>
    /// Запускает предварительное наполнение пула воркеров для DC, которых нет в <c>--dc-ip</c>.
    /// </summary>
    void Warmup();

    /// <summary>
    /// Сбрасывает пул: закрывает соединения и отменяет фоновые задачи.
    /// </summary>
    void Reset();
}
