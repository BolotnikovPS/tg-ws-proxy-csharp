using System.Net;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Основной сервер прокси: слушает клиентский порт и принимает сессии.
/// </summary>
public interface IProxyServer
{
    /// <summary>
    /// Признак того, что слушающий сокет открыт и цикл приёма работает.
    /// Используется служебным эндпоинтом /healthz. Расширение порта, в upstream отсутствует.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// Запускает сервер прокси и обрабатывает подключения до отмены токена.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены фоновой работы сервера.</param>
    Task Run(CancellationToken cancellationToken);

    /// <summary>
    /// Перепривязывает слушающий сокет на новый адрес без остановки процесса (F-7.1).
    /// Цикл приёма останавливается вместе с watchdog-таймером, затем вызывается
    /// <paramref name="betweenStopAndBind"/> — в этом окне контроллер останавливает пулы и
    /// разрывает активные сессии, чтобы новая привязка открылась уже «начисто» (F-7.2).
    /// Если привязка не удалась, сокет прежнего адреса открывается заново, а вызывающая
    /// сторона получает <see cref="System.Net.Sockets.SocketException"/> и откатывает конфигурацию.
    /// </summary>
    /// <param name="endpoint">Новый адрес привязки.</param>
    /// <param name="betweenStopAndBind">
    /// Действия между остановкой приёма и привязкой нового сокета; может быть <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Отмена операции перепривязки.</param>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// Новый адрес занят или недоступен; прежняя привязка восстановлена.
    /// </exception>
    /// <exception cref="InvalidOperationException">Цикл приёма не запущен.</exception>
    Task RebindAsync(
        IPEndPoint endpoint,
        Func<CancellationToken, Task>? betweenStopAndBind,
        CancellationToken cancellationToken);
}
