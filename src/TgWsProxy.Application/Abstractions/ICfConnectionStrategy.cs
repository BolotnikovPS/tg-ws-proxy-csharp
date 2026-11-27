namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Единая политика подключения к Cloudflare-доменам (порт upstream <c>proxy/cf_websocket.py</c>).
/// </summary>
/// <remarks>
/// До v1.12.0 перебор CF-доменов и переключение TLS/plain были размазаны по вызывающим
/// местам, а охлаждение упавших доменов отсутствовало: домен, чья квота исчерпана или чей
/// TLS блокируется, продолжал выбираться на каждой попытке. Реализация собирает политику
/// в одном месте и переиспользуется мостом, пулом воркеров и H2-полосами.
/// </remarks>
public interface ICfConnectionStrategy
{
    /// <summary>
    /// Сбрасывает охлаждения и статистику TLS/plain. Вызывается при старте листенера
    /// (upstream <c>reset_cf_timeouts()</c>).
    /// </summary>
    void Reset();

    /// <summary>
    /// Предпочитаемый режим: <see langword="true"/>, если TLS срабатывал не реже, чем plain
    /// (upstream <c>cf_prefers_tls()</c>). Нужен H2-пулу: H2 возможен только по TLS.
    /// </summary>
    bool PrefersTls();

    /// <summary>
    /// Признак того, что TLS-вариант домена в охлаждении после таймаута
    /// (upstream <c>cf_tls_in_cooldown</c>).
    /// </summary>
    /// <param name="name">Имя домена-кандидата.</param>
    bool TlsInCooldown(string name);

    /// <summary>
    /// Перебирает домены-кандидаты, пока не откроет соединение: упавшая пара
    /// «домен, режим TLS» уходит в охлаждение, а таймаут переключает режим и
    /// берёт следующий, более щедрый лимит.
    /// </summary>
    /// <param name="candidates">Домены-кандидаты в порядке перебора.</param>
    /// <param name="hostFor">Преобразование кандидата в хост подключения (для CF proxy — <c>kws{dc}.{domain}</c>).</param>
    /// <param name="label">Метка для логов (скоуп сессии).</param>
    /// <param name="secure"><see langword="false"/> — режим <c>--no-secure</c>: TLS не пробуется.</param>
    /// <param name="path">Путь WS-эндпоинта (для воркера — с query <c>dst</c>/<c>dc</c>).</param>
    /// <param name="timeout">Верхняя граница таймаута попытки (урезается лестницей лимитов).</param>
    /// <param name="cancellationToken">Проверяется между попытками: идущий дозвон не прерывается.</param>
    /// <returns>Открытое соединение и имя сработавшего кандидата, либо <see langword="null"/>.</returns>
    Task<(IRawWebSocket Ws, string Name)?> ConnectAsync(
        IReadOnlyList<string> candidates,
        Func<string, string> hostFor,
        string label,
        bool secure,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
