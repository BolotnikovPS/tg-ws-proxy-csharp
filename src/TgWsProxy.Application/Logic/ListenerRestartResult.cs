namespace TgWsProxy.Application.Logic;

/// <summary>
/// Итог перезапуска слушателя (§ 2.7). При неудаче привязки контроллер сам возвращает
/// подсистемы и прежнюю привязку в рабочее состояние, а вызывающая сторона откатывает файл.
/// </summary>
/// <param name="Restarted">Новая привязка открыта и пулы подняты.</param>
/// <param name="DroppedSessions">Сколько активных сессий было разорвано (F-7.4).</param>
/// <param name="RolledBack">Привязка не удалась, выполнен откат на прежнюю (F-7.5).</param>
/// <param name="Error">Причина неудачи при <see cref="RolledBack"/>.</param>
public sealed record ListenerRestartResult(
    bool Restarted,
    int DroppedSessions,
    bool RolledBack,
    string? Error)
{
    /// <summary>Успешный перезапуск.</summary>
    public static ListenerRestartResult Ok(int droppedSessions) =>
        new(true, droppedSessions, false, null);

    /// <summary>Неудачная привязка с откатом на прежний адрес.</summary>
    public static ListenerRestartResult Failed(string error) =>
        new(false, 0, true, error);
}
