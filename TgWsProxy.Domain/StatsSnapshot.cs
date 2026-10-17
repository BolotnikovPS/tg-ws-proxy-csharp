namespace TgWsProxy.Domain;

/// <summary>
/// Согласованный снимок счётчиков прокси для выдачи во внешние системы мониторинга
/// (Prometheus, healthcheck). Значения читаются по одному разу и далее не меняются.
/// </summary>
public sealed record StatsSnapshot
{
    /// <summary>Всего принятых подключений, включая отклонённые.</summary>
    public long ConnectionsTotal { get; init; }

    /// <summary>Активные клиентские сессии в момент снятия снимка.</summary>
    public long ConnectionsActive { get; init; }

    /// <summary>Подключения, обслуженные через прямой WebSocket.</summary>
    public long ConnectionsWs { get; init; }

    /// <summary>Media-подключения, обслуженные через HTTP/2.</summary>
    public long ConnectionsH2 { get; init; }

    /// <summary>Установленные TCP-соединения HTTP/2 транспорта.</summary>
    public long H2TcpConnections { get; init; }

    /// <summary>Выполненные HTTP/2 запросы.</summary>
    public long H2Requests { get; init; }

    /// <summary>Ошибки HTTP/2.</summary>
    public long H2Errors { get; init; }

    /// <summary>Подключения, переведённые на TCP fallback.</summary>
    public long ConnectionsTcpFallback { get; init; }

    /// <summary>Подключения через Cloudflare proxy fallback.</summary>
    public long ConnectionsCfProxy { get; init; }

    /// <summary>Подключения через домен-фронтинг.</summary>
    public long ConnectionsFronting { get; init; }

    /// <summary>Подключения с некорректным MTProto handshake.</summary>
    public long ConnectionsBad { get; init; }

    /// <summary>Подключения, отклонённые списком IP-доступа.</summary>
    public long ConnectionsBlocked { get; init; }

    /// <summary>Подключения, замаскированные под чужой домен.</summary>
    public long ConnectionsMasked { get; init; }

    /// <summary>Подключения, отклонённые из-за лимита --max-connections.</summary>
    public long ConnectionsLimited { get; init; }

    /// <summary>Подключения, отклонённые ограничителем частоты --rate-limit.</summary>
    public long ConnectionsRateLimited { get; init; }

    /// <summary>Ошибки WebSocket.</summary>
    public long WsErrors { get; init; }

    /// <summary>Байты, принятые от клиентов (клиент → апстрим).</summary>
    public long BytesUp { get; init; }

    /// <summary>Байты, отправленные клиентам (апстрим → клиент).</summary>
    public long BytesDown { get; init; }

    /// <summary>Попадания в пул WebSocket.</summary>
    public long PoolHits { get; init; }

    /// <summary>Промахи по пулу WebSocket.</summary>
    public long PoolMisses { get; init; }

    /// <summary>Попадания в пул CF worker соединений.</summary>
    public long CfPoolHits { get; init; }

    /// <summary>Промахи по пулу CF worker соединений.</summary>
    public long CfPoolMisses { get; init; }
}
