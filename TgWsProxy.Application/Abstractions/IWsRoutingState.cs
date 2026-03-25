#nullable enable

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Управляет состоянием маршрутизации WS/TCP для DC (cooldown, blacklist и пул WS).
/// </summary>
public interface IWsRoutingState
{
    /// <summary>
    /// Проверяет активность cooldown для заданного DC-ключа.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <param name="now">Текущее время для сравнения с окончанием cooldown.</param>
    /// <returns><see langword="true"/>, если cooldown активен.</returns>
    bool IsInFailCooldown((int Dc, bool IsMedia) dcKey, DateTimeOffset now);

    /// <summary>
    /// Устанавливает cooldown до указанного времени.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <param name="until">Момент времени, до которого действует cooldown.</param>
    void SetFailCooldown((int Dc, bool IsMedia) dcKey, DateTimeOffset until);

    /// <summary>
    /// Сбрасывает cooldown для DC-ключа.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    void ClearFailCooldown((int Dc, bool IsMedia) dcKey);

    /// <summary>
    /// Проверяет наличие DC-ключа в black list.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <returns><see langword="true"/>, если ключ находится в black list.</returns>
    bool IsBlacklisted((int Dc, bool IsMedia) dcKey);

    /// <summary>
    /// Добавляет DC-ключ в black list.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    void AddBlacklist((int Dc, bool IsMedia) dcKey);

    /// <summary>
    /// Пытается получить живой WS из пула, удаляя устаревшие записи.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <param name="now">Текущее время для проверки возраста записей в пуле.</param>
    /// <param name="maxAge">Максимально допустимый возраст WS-соединения в пуле.</param>
    /// <returns>Живое WS-соединение либо <see langword="null"/>, если подходящего нет.</returns>
    IRawWebSocket? TryTakePooledWs((int Dc, bool IsMedia) dcKey, DateTimeOffset now, TimeSpan maxAge);

    /// <summary>
    /// Пытается пометить ключ как «в процессе refill».
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <returns><see langword="true"/>, если флаг refill установлен текущим вызовом.</returns>
    bool TryBeginRefill((int Dc, bool IsMedia) dcKey);

    /// <summary>
    /// Снимает флаг refill для ключа.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    void EndRefill((int Dc, bool IsMedia) dcKey);

    /// <summary>
    /// Добавляет WS в пул и возвращает вытесненные соединения.
    /// </summary>
    /// <param name="dcKey">Ключ дата-центра и типа маршрута (media/regular).</param>
    /// <param name="ws">Новая WS-сессия для помещения в пул.</param>
    /// <param name="createdAt">Момент создания/добавления WS-сессии.</param>
    /// <param name="maxPoolSize">Максимальный размер пула для данного ключа.</param>
    /// <returns>Список вытесненных соединений, которые следует закрыть вызывающей стороне.</returns>
    IReadOnlyList<IRawWebSocket> AddToPool((int Dc, bool IsMedia) dcKey, IRawWebSocket ws, DateTimeOffset createdAt, int maxPoolSize);

    /// <summary>
    /// Краткая строка для логов: отсортированные ключи blacklist.
    /// </summary>
    string FormatBlacklistSummary();
}
