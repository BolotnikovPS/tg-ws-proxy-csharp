using System.Net.Sockets;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Реестр принятых, но ещё не завершённых клиентских сессий (F-7.4). Нужен перезапуску
/// слушателя (§ 2.7): при смене привязки старые сессии разрываются немедленно, и страница
/// должна показать, сколько именно соединений это затронуло. Тот же реестр обслуживает
/// отзыв токена доступа прокси (F-8.7, <see cref="CloseByToken"/>).
/// </summary>
public interface IActiveSessionRegistry
{
    /// <summary>Текущее число активных сессий.</summary>
    int Count { get; }

    /// <summary>
    /// Регистрирует принятое соединение. Освобождение возвращённой аренды снимает сессию
    /// с учёта; забыть её нельзя — <c>using</c> в обработчике сессии делает это автоматически.
    /// </summary>
    /// <param name="client">Сокет клиента: разрыв сессии выполняется его закрытием.</param>
    /// <param name="scope">Метка сессии для журнала (<c>peer|connectionId</c>).</param>
    ISessionLease Register(Socket client, string scope);

    /// <summary>
    /// Разрывает все активные сессии. Возвращает число разорванных соединений.
    /// </summary>
    int CloseAll();

    /// <summary>
    /// Разрывает сессии, установленные с указанным токеном доступа прокси.
    /// </summary>
    /// <param name="tokenId">Идентификатор отозванного токена.</param>
    /// <returns>Число разорванных соединений.</returns>
    int CloseByToken(string tokenId);

    /// <summary>
    /// Считает активные сессии, установленные с указанным токеном (F-8.11): страница
    /// показывает это число рядом с каждым токеном, не разрывая соединения.
    /// </summary>
    /// <param name="tokenId">Идентификатор токена.</param>
    /// <returns>Число живых сессий с этим токеном.</returns>
    int CountByToken(string tokenId);
}
