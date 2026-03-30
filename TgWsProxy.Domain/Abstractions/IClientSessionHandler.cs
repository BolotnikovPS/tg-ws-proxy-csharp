using System.Net.Sockets;

namespace TgWsProxy.Domain.Abstractions;

public interface IClientSessionHandler
{
    /// <summary>
    /// Обрабатывает одну клиентскую SOCKS/MTProto-сессию.
    /// </summary>
    /// <param name="client">Подключенный клиент TCP.</param>
    /// <param name="context">Контекст подключения для трассировки и логирования.</param>
    /// <param name="cancellationToken">Токен отмены обработки сессии.</param>
    Task Handle(TcpClient client, ClientContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Выполняет фоновый прогрев соединений и внутренних пулов.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции прогрева.</param>
    void Warmup(CancellationToken cancellationToken);
}
