namespace TgWsProxy.Domain;

/// <summary>
/// Токен доступа прокси (F-8.1): именованный секрет MTProto, из которого строится
/// <c>tg://</c>-ссылка подключения. Заменяет единственный <c>Config.Secret</c> прежних версий:
/// каждое устройство или группа устройств получает собственный секрет, что позволяет считать
/// трафик по паре (IP, токен) и отзывать доступ точечно, не меняя секрет остальных клиентов.
/// </summary>
public sealed class ProxyToken
{
    /// <summary>Стабильный идентификатор, генерируется сервером и не меняется через API.</summary>
    public string Id { get; set; } = "";

    /// <summary>Отображаемое имя (1…64 символа, уникально среди токенов).</summary>
    public string Name { get; set; } = "";

    /// <summary>Секрет доступа прокси: ровно 32 hex-символа. В журнал не пишется (N-8) — только маска.</summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Выключенный токен не участвует в рукопожатии, а его живые сессии разрываются (F-8.6).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Момент создания. Только чтение через API.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Момент последнего успешного рукопожатия (F-8.4). Только чтение через API.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// Копия токена. Нужна там, где список отдаётся наружу (DTO, снимок реестра):
    /// изменение копии не должно менять живую конфигурацию.
    /// </summary>
    public ProxyToken Clone() => new()
    {
        Id = Id,
        Name = Name,
        Secret = Secret,
        Enabled = Enabled,
        CreatedAt = CreatedAt,
        LastUsedAt = LastUsedAt,
    };
}
