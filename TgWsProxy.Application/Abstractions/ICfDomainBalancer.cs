namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Распределяет CF proxy домены по дата-центрам (аналог upstream <c>balancer</c>).
/// </summary>
public interface ICfDomainBalancer
{
    /// <summary>
    /// Текущий список активных CF proxy доменов.
    /// </summary>
    IReadOnlyList<string> Domains { get; }

    /// <summary>
    /// Обновляет список доменов; при фактическом изменении сбрасывает привязку DC→домен.
    /// </summary>
    /// <param name="domains">Новый список доменов.</param>
    void UpdateDomains(IReadOnlyList<string> domains);

    /// <summary>
    /// Возвращает домены для DC: текущий привязанный первым, остальные — в случайном порядке.
    /// </summary>
    /// <param name="dc">Номер дата-центра.</param>
    IReadOnlyList<string> DomainsForDc(int dc);

    /// <summary>
    /// Переключает активный домен для DC (вызывается после успешного подключения).
    /// </summary>
    /// <param name="dc">Номер дата-центра.</param>
    /// <param name="domain">Домен, который оказался рабочим.</param>
    /// <returns><c>true</c>, если привязка действительно изменилась (как в upstream).</returns>
    bool UpdateDomainForDc(int dc, string domain);
}
