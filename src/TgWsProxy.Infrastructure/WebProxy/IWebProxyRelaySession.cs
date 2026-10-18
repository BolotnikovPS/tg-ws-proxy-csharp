using System.Net;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Порт relay-сессии WEB proxy. Хост (Kestrel) вызывает <see cref="ResolveToken"/> для
/// авторизации bridge-страницы по <c>?bridge=&lt;capability&gt;</c>, а <see cref="OpenSession"/>
/// — при создании сессии (<c>POST /api/v1/session</c>) для получения фабрики мостов потоков
/// и аренды жизненного цикла (статистика, привязка (IP, токен), реестр живых сессий).
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
    /// Открывает жизненный цикл relay-сессии: ведёт счётчики соединений, регистрирует сессию
    /// в реестре живых сессий (разрыв по отзыву токена/перезапуску) и возвращает аренду с
    /// фабрикой мостов потоков. Аренда закрывается <see cref="WebProxyRelayLease.Dispose"/>.
    /// </summary>
    WebProxyRelayLease OpenSession(ProxyTokenEntry token, IPAddress? peer, string scope);
}
