#nullable enable

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Пул HTTP/2-каналов Cloudflare для media-трафика (порт upstream <c>CfH2Pool</c>).
/// </summary>
public interface ICfH2Pool
{
    /// <summary>
    /// Открывает канал для указанного DC, перебирая домены балансировщика.
    /// </summary>
    /// <param name="dc">Номер дата-центра.</param>
    /// <param name="label">Идентификатор сессии для логов.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Канал либо <see langword="null"/>, если H2 недоступен (нужен WS/TCP маршрут).</returns>
    Task<ICfH2Channel?> OpenAsync(int dc, string label, CancellationToken cancellationToken);

    /// <summary>Закрывает все каналы и HTTP/2-соединения.</summary>
    Task CloseAsync();
}
