namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Второй одновременный перезапуск листенера запрещён (F-7.6): вызывающая сторона превращает
/// это исключение в <c>409</c> с <c>Retry-After</c>.
/// </summary>
public sealed class ListenerRestartInProgressException()
    : InvalidOperationException("A listener restart is already in progress.");
