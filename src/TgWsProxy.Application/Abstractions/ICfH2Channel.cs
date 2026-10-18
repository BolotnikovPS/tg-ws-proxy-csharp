namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Один мультиплексированный HTTP/2-канал к Cloudflare <c>/api</c> для media-трафика
/// (порт upstream <c>_HttpChannel</c> из <c>proxy/cf_h2.py</c>).
/// </summary>
public interface ICfH2Channel : IAsyncDisposable
{
    /// <summary>Признак того, что канал закрыт и больше не принимает запросы.</summary>
    bool Closed { get; }

    /// <summary>
    /// Код транспортной ошибки MTProto, который нужно вернуть клиенту
    /// (отрицательный HTTP-статус или отрицательный код из 4-байтного ответа).
    /// </summary>
    int? TransportError { get; }

    /// <summary>
    /// Отправляет один MTProto-пакет в Cloudflare и ставит ответ в очередь выдачи клиенту.
    /// </summary>
    /// <param name="body">Тело пакета (без padding).</param>
    /// <param name="quick">Признак quick-ack исходного пакета.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><see langword="true"/>, если пакет принят к отправке.</returns>
    Task<bool> SendAsync(byte[] body, bool quick, CancellationToken cancellationToken);

    /// <summary>
    /// Ожидает следующий ответ Cloudflare.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Тело ответа Telegram.</returns>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Отмечает, что полученный ответ уже записан клиенту (снимает native backpressure,
    /// из-за которого приостанавливается повторная отправка пакетов).
    /// </summary>
    void MarkDelivered();

    /// <summary>
    /// Отмечает прогресс обмена (используется для решения о повторной отправке
    /// «зависших» пакетов, порт upstream <c>channel.last_progress</c>).
    /// </summary>
    void MarkProgress();

    /// <summary>
    /// Запускает (один раз) цикл восстановления: повторную отправку неподтверждённых пакетов,
    /// когда канал простаивает (порт upstream <c>_HttpChannel._recover</c>).
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача цикла восстановления; завершается при закрытии канала.</returns>
    Task RecoverAsync(CancellationToken cancellationToken);
}
