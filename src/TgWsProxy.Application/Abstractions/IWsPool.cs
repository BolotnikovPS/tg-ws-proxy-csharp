namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Пул заранее открытых WebSocket-соединений к Telegram (аналог upstream <c>_WsPool</c>).
/// Клиентские сессии только забирают готовое соединение; наполнение пула идёт в фоне.
/// </summary>
public interface IWsPool
{
    /// <summary>
    /// Пытается получить готовое WS-соединение из пула.
    /// При промахе планирует фоновое наполнение и возвращает <see langword="null"/>.
    /// </summary>
    /// <param name="dc">Номер дата-центра.</param>
    /// <param name="isMedia">Признак media-маршрута.</param>
    /// <param name="isTestDc">Признак тестового DC (используется путь <c>/apiws_test</c>).</param>
    /// <returns>Готовое соединение либо <see langword="null"/>.</returns>
    IRawWebSocket? TryGet(int dc, bool isMedia, bool isTestDc);

    /// <summary>
    /// Запускает предварительное наполнение пула для всех настроенных DC.
    /// </summary>
    void Warmup();

    /// <summary>
    /// Сбрасывает пул: закрывает все соединения и отменяет фоновые задачи.
    /// </summary>
    void Reset();

    /// <summary>
    /// Краткая строка для логов: количество готовых соединений по ключам.
    /// </summary>
    string FormatPoolSummary();
}
