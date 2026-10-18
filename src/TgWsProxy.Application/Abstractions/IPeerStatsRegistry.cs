using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Реестр статистики по парам «IP-адрес, токен доступа» (требования F-4 и F-9). Горячие пути
/// (пересылка байт) обязаны быть неблокирующими: реализации считают байты через
/// <see cref="System.Threading.Interlocked"/>, а словарь ключей — через
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public interface IPeerStatsRegistry
{
    /// <summary>Включён ли учёт (<c>enablePerIpStats</c>).</summary>
    bool IsEnabled { get; }

    /// <summary>Сколько пар (адрес, токен) сейчас отслеживается.</summary>
    long TrackedPeers { get; }

    /// <summary>Сколько раз новая пара не была учтена из-за предела <see cref="Logic.PeerStatsRegistry.MaxTrackedPeers"/>.</summary>
    long OverflowCount { get; }

    /// <summary>
    /// Регистрирует начало сессии до выбора токена: <c>active++</c>, <c>total++</c>, обновляет
    /// <c>lastSeen</c> записи <c>(peer, null)</c>. Токен на этапе accept ещё неизвестен (F-9.3),
    /// поэтому счётчики открытия переезжают в пару с токеном при вызове <see cref="BindToken"/>.
    /// </summary>
    void ConnectionOpened(IPAddress peer);

    /// <summary>
    /// Привязывает открытую сессию к токену: одно открытие и одно активное соединение переносятся
    /// из записи <c>(peer, null)</c> в запись <c>(peer, tokenId)</c> (F-9.1). Вызывается после
    /// успешного handshake; при <see langword="null"/> — ничего не делает.
    /// </summary>
    void BindToken(IPAddress? peer, string? tokenId);

    /// <summary>Регистрирует завершение сессии: <c>active--</c> (не уходит в минус).</summary>
    void ConnectionClosed(IPAddress? peer, string? tokenId);

    /// <summary>Сессия отклонена по IP ACL (учитывается без токена, F-9.3).</summary>
    void RecordBlocked(IPAddress peer);

    /// <summary>Сессия отклонена по общему лимиту одновременных соединений (без токена, F-9.3).</summary>
    void RecordLimited(IPAddress peer);

    /// <summary>Сессия отклонена по лимиту частоты подключений с этого адреса (без токена, F-9.3).</summary>
    void RecordRateLimited(IPAddress peer);

    /// <summary>Учитывает отправленные клиентом байты. <see langword="null"/> — учёт недоступен.</summary>
    void AddBytesUp(IPAddress? peer, string? tokenId, long bytes);

    /// <summary>Учитывает принятые клиентом байты. <see langword="null"/> — учёт недоступен.</summary>
    void AddBytesDown(IPAddress? peer, string? tokenId, long bytes);

    /// <summary>Ошибка WebSocket/рукопожатия по паре (адрес, токен).</summary>
    void RecordWsError(IPAddress? peer, string? tokenId);

    /// <summary>Попадание в пул WS-соединений.</summary>
    void RecordPoolHit(IPAddress? peer, string? tokenId);

    /// <summary>Промах пула WS-соединений (сессия ушла в fallback).</summary>
    void RecordPoolMiss(IPAddress? peer, string? tokenId);

    /// <summary>Снимок всех отслеживаемых пар (без гарантий порядка).</summary>
    IReadOnlyList<PeerStatsSnapshot> Snapshot();
}
