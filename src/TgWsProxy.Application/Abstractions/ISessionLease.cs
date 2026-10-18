namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Аренда одной активной сессии: пока она не освобождена, соединение видно в реестре
/// и может быть разорвано перезапуском слушателя или отзывом токена.
///
/// Наследует <see cref="ISessionTokenBinding"/>: обработчик сессии сообщает через неё токен,
/// которым прошло рукопожатие (F-8.4), не зная ничего о реестре сессий.
/// </summary>
public interface ISessionLease : IDisposable, ISessionTokenBinding
{
}
