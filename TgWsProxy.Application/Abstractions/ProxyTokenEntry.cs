#nullable enable

namespace TgWsProxy.Application.Abstractions;

/// <summary>
/// Токен доступа прокси, подготовленный для горячего пути рукопожатия (F-8.4). Hex-разбор
/// секрета выполняется один раз при построении снимка, а не на каждой попытке: рукопожатие
/// может перебрать до 64 токенов, и разбор hex там был бы лишней работой на каждое соединение.
/// </summary>
public sealed class ProxyTokenEntry(
    string id,
    string name,
    string secretHex,
    byte[] secret,
    bool enabled,
    DateTimeOffset createdAt,
    DateTimeOffset? lastUsedAt)
{
    /// <summary>Стабильный идентификатор токена.</summary>
    public string Id { get; } = id;

    /// <summary>Имя токена (для журнала, метрик и панели).</summary>
    public string Name { get; } = name;

    /// <summary>Секрет в виде 32 hex-символов (из него строится ссылка подключения).</summary>
    public string SecretHex { get; } = secretHex;

    /// <summary>Секрет в байтах: ключ вывода MTProto-рукопожатия.</summary>
    public byte[] Secret { get; } = secret;

    /// <summary>Включён ли токен на момент построения снимка.</summary>
    public bool Enabled { get; } = enabled;

    /// <summary>Момент создания токена.</summary>
    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>
    /// Момент последнего успешного рукопожатия. Обновляется на месте (<c>Touch</c>), чтобы
    /// следующий перебор начинался с самого «свежего» токена без пересборки снимка.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; internal set; } = lastUsedAt;
}
