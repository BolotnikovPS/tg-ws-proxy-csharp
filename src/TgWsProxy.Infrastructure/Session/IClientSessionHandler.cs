using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.Session;

public interface IClientSessionHandler
{
    /// <summary>
    /// Обрабатывает одну клиентскую SOCKS/MTProto-сессию.
    /// </summary>
    /// <param name="client">Подключенный клиент TCP.</param>
    /// <param name="context">Контекст подключения для трассировки и логирования.</param>
    /// <param name="session">
    /// Привязка сессии к токену доступа прокси (F-8.4). После успешного рукопожатия обработчик
    /// сообщает сюда идентификатор токена, чтобы отзыв токена мог разорвать именно эту сессию,
    /// а статистика трафика отнесла байты нужному токену. Может быть <see langword="null"/>,
    /// если сессия не зарегистрирована (например, в тестах).
    /// </param>
    /// <param name="cancellationToken">Токен отмены обработки сессии.</param>
    Task Handle(
        Socket client,
        ClientContext context,
        ISessionTokenBinding? session,
        CancellationToken cancellationToken);

    /// <summary>
    /// Выполняет фоновый прогрев соединений и внутренних пулов.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции прогрева.</param>
    void Warmup(CancellationToken cancellationToken);
}
