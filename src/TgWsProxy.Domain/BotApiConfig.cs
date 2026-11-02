namespace TgWsProxy.Domain;

/// <summary>
/// Настройки локального Telegram Bot API-сервера (вложенный блок <c>botApi</c>): HTTP-эндпоинты
/// <c>/bot&lt;token&gt;/&lt;method&gt;</c> и автономное MTProto-ядро для ботов. Все ключи блока —
/// «только старт». Расширение порта, в upstream отсутствует.
/// </summary>
public sealed class BotApiConfig
{
    /// <summary>
    /// Включить Bot API-сервер. При <c>false</c> маршруты <c>/bot*</c> и <c>/file/bot*</c>
    /// не регистрируются. Все ключи блока — «только старт».
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Каталог хранения сессий ботов (MTProto auth_key + состояние обновлений). По одному файлу
    /// <c>{botId}.bin</c> на бота; <c>botId</c> — число до «:» в bot token. Путь относительно
    /// каталога запуска. По умолчанию <c>./bot-sessions</c>.
    /// </summary>
    public string DataDir { get; set; } = "./bot-sessions";

    /// <summary>
    /// Хост MTProto-подключения бота. По умолчанию — DC2 напрямую; при заданном <see cref="Secret"/>
    /// — адрес собственного MTProxy-обхода (например, <c>127.0.0.1</c>).
    /// </summary>
    public string Host { get; set; } = "149.154.167.50";

    /// <summary>Порт MTProto-подключения бота (по умолчанию 443).</summary>
    public int Port { get; set; } = 443;

    /// <summary>
    /// Секрет MTProxy в hex: 17 байт (префикс <c>dd</c>/<c>ee</c> + 16 байт секрета) или 16 байт без
    /// префикса. Пусто/null — прямое подключение к DC (без обфускации).
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Максимальный суммарный размер multipart-загрузки одного Bot API-запроса в байтах
    /// (sendPhoto/sendDocument/...). По умолчанию 20 МиБ. Превышение отклоняется с 413.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Максимальный суммарный размер in-memory кэша скачанных через <c>getFile</c> файлов в байтах.
    /// По умолчанию 64 МиБ. При превышении — LRU-вытеснение; единичный файл крупнее лимита
    /// отклоняется с 413. Нужен, чтобы /file/bot* не рос без ограничений.
    /// </summary>
    public long MaxFileCacheBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Максимум принимаемых Bot API-запросов в секунду на одного бота (ключ — botId из токена).
    /// По умолчанию 30. <c>0</c> или меньше — ограничение выключено. Превышение отклоняется
    /// с 429 до разбора тела запроса. Подсчёт — in-memory, окно фиксированное 1 секунда.
    /// </summary>
    public int RequestsPerSecondPerToken { get; set; } = 30;
}
