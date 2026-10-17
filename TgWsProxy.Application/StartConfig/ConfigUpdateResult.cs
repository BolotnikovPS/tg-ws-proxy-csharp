#nullable enable

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Итог применения новой конфигурации (F-2.11, F-2.12). Повторяет контракт §5 документа
/// требований: <c>{ saved, path, hotReloaded[], listenerRestarted, droppedSessions, warnings[] }</c>.
/// Понятия <c>requiresRestart</c> в этой редакции нет: ключи «только старт» отклоняются
/// с <c>409</c> ещё до записи файла.
/// </summary>
/// <param name="Saved">Файл записан на диск.</param>
/// <param name="Path">Куда записано.</param>
/// <param name="HotReloaded">Ключи, применённые к работающему процессу сразу.</param>
/// <param name="ListenerRestarted">Слушатель был перезапущен на новой привязке (F-7.1).</param>
/// <param name="DroppedSessions">Сколько активных сессий разорвано при перезапуске (F-7.4).</param>
/// <param name="Warnings">Некритичные замечания к сохранённой конфигурации.</param>
public sealed record ConfigUpdateResult(
    bool Saved,
    string Path,
    IReadOnlyList<string> HotReloaded,
    bool ListenerRestarted,
    int DroppedSessions,
    IReadOnlyList<string> Warnings);
