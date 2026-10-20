using TgWsProxy.Application.Logic;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Проверяет достижимость дата-центров Telegram с хоста прокси по нескольким маршрутам
/// (прямой WebSocket, прямой TCP, Cloudflare-фронтинг, Cloudflare Worker). Реализация —
/// сетевой ввод-вывод, поэтому живёт в Infrastructure; здесь только порт приложения.
/// </summary>
public interface IDcReachabilityChecker
{
    /// <summary>
    /// Выполняет проверку всех DC по всем маршрутам и возвращает сводку.
    /// </summary>
    /// <param name="ct">Токен отмены запроса.</param>
    Task<DcCheckReport> CheckAsync(CancellationToken ct = default);
}
