namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Состояние прокси-листенера, которое видят <c>/healthz/ready</c> и страница (F-3.9, F-5.18).
/// </summary>
public enum ListenerState
{
    /// <summary>Слушающий сокет не открыт: сессии не принимаются.</summary>
    Unavailable,

    /// <summary>Слушающий сокет открыт, цикл приёма работает.</summary>
    Listening,

    /// <summary>Идёт перезапуск привязки: приём приостановлен, сессии разрываются (F-7.7).</summary>
    Restarting,
}
