namespace TgWsProxy.Application.Abstractions;

public interface IProxyServer
{
    /// <summary>
    /// Запускает сервер прокси и обрабатывает подключения до отмены токена.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены фоновой работы сервера.</param>
    Task RunAsync(CancellationToken cancellationToken);
}
