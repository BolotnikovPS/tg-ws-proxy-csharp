namespace TgWsProxy.Domain;

/// <summary>
/// DTO для блока <c>botApi</c> в config.json. Все поля nullable — <c>null</c> означает
/// «ключ не задан, оставить текущее значение».
/// </summary>
public sealed class BotApiConfigDto
{
    public bool? Enabled { get; set; }

    /// <summary>Каталог сессий ботов; <c>null</c> — оставить текущее значение.</summary>
    public string? DataDir { get; set; }

    /// <summary>Хост MTProto-подключения бота; <c>null</c> — оставить текущее значение.</summary>
    public string? Host { get; set; }

    /// <summary>Порт MTProto-подключения бота; <c>null</c> — оставить текущее значение.</summary>
    public int? Port { get; set; }

    /// <summary>Секрет MTProxy в hex; <c>null</c> — оставить текущее значение.</summary>
    public string? Secret { get; set; }

    /// <summary>Максимальный размер multipart-загрузки бота в байтах; <c>null</c> — оставить текущее значение.</summary>
    public long? MaxUploadBytes { get; set; }

    /// <summary>Максимальный суммарный размер in-memory кэша скачанных файлов; <c>null</c> — оставить текущее значение.</summary>
    public long? MaxFileCacheBytes { get; set; }

    /// <summary>Максимум Bot API-запросов в секунду на бота; <c>null</c> — оставить текущее значение.</summary>
    public int? RequestsPerSecondPerToken { get; set; }
}
