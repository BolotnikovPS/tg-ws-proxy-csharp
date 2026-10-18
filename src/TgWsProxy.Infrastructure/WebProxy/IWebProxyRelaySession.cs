using System.Net;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Порт relay-сессии WEB proxy. Хост (Kestrel/WebSocket) вызывает <see cref="ResolveToken"/>
/// для авторизации канала по <c>?bridge=&lt;capability&gt;</c> перед апгрейдом, а затем
/// <see cref="RunAsync"/> для запуска relay-цикла поверх транспорта.
/// </summary>
public interface IWebProxyRelaySession
{
    /// <summary>
    /// Подбирает токен, чей секрет даёт совпадение HMAC bridge-capability (F-W.6/F-W.7).
    /// Перебор идёт в порядке «недавно использованные первыми» (<c>HandshakeOrder</c>).
    /// </summary>
    /// <param name="host">Домен bridge-сайта без пути (заголовок Host).</param>
    /// <param name="capability">Значение параметра <c>bridge</c>.</param>
    /// <returns>Токен либо <see langword="null"/>, если capability не подошёл ни одному секрету.</returns>
    ProxyTokenEntry? ResolveToken(string host, string? capability);

    /// <summary>
    /// Выполняет relay-цикл: мультиплексирует потоки и для каждого потока поднимает MTProto-мост
    /// в Telegram. Возвращается по EOF транспорта или отмене. Ведёт счётчики соединений и
    /// привязку (IP, токен).
    /// </summary>
    Task RunAsync(
        IRelayTransport transport,
        ProxyTokenEntry token,
        IPAddress? peer,
        string scope,
        CancellationToken ct);
}
