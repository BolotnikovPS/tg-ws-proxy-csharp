#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Чтение и запись файла конфигурации <c>config.json</c>.
///
/// Модель приоритетов (F-2.2): встроенные умолчания → файл. Аргументы командной строки
/// конфигурацию не задают — от CLI остался только путь к файлу (<see cref="BootstrapOptions"/>).
///
/// Файл читается в два шага. Сначала документ разбирается как дерево
/// <see cref="JsonDocument"/>: это позволяет выдать точные ошибки по неизвестным ключам и
/// ключам с неверным типом, вместо общего исключения десериализатора. Затем отфильтрованное
/// дерево десериализуется в <see cref="ConfigDto"/>.
///
/// Формат: JSON с комментариями (<c>//</c> и <c>/* */</c>) и висячими запятыми. Это делает
/// файл пригодным для ручного редактирования — основной сценарий для тома Docker.
/// </summary>
public static class ConfigFileLoader
{
    /// <summary>Имя файла конфигурации, если путь не задан явно.</summary>
    public const string DefaultFileName = "config.json";

    /// <summary>
    /// Ключ прежних версий с единственным секретом (F-8.3). Принимается при чтении и
    /// переносится в <c>tokens</c>; при записи больше не создаётся.
    /// </summary>
    public const string LegacySecretKey = "secret";

    /// <summary>
    /// Разрешает путь к файлу конфигурации.
    /// Явно указанный путь (CLI или <c>TGWS_CONFIG</c>) используется как есть, без проверки
    /// существования: вызывающая сторона решает, ошибка это или нет.
    /// Без явного пути берётся <c>config.json</c> в текущем каталоге.
    /// </summary>
    /// <param name="explicitPath">Путь из CLI.</param>
    /// <returns>Абсолютный или относительный путь и признак того, что он задан явно.</returns>
    public static (string Path, bool IsExplicit) ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return (explicitPath.Trim(), true);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("TGWS_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment.Trim(), true);
        }

        return (Path.Combine(Directory.GetCurrentDirectory(), DefaultFileName), false);
    }

    /// <summary>
    /// Загружает конфигурацию из файла и накладывает её на умолчания.
    ///
    /// Отсутствующий файл при неявном пути — норма: прокси работает на умолчаниях.
    /// Отсутствующий файл при явном пути — ошибка: пользователь явно указал файл, и молча
    /// проигнорировать опечатку в пути хуже, чем упасть при старте.
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    /// <param name="isExplicit">Задан ли путь явно (<c>--config</c> или <c>TGWS_CONFIG</c>).</param>
    /// <returns>Конфигурация: умолчания, перекрытые файлом.</returns>
    /// <exception cref="ArgumentException">Файл задан явно, но не найден; файл некорректен.</exception>
    public static Config Load(string path, bool isExplicit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var config = new Config();
        ConfigDefaults.Apply(config);

        if (!File.Exists(path))
        {
            if (isExplicit)
            {
                throw new ArgumentException($"Config file not found: {path}");
            }

            return config;
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Cannot read config file {path}: {ex.Message}");
        }

        var dto = Deserialize(json, path);
        ConfigDtoMapper.Apply(dto, config);
        return config;
    }

    /// <summary>
    /// Разбирает JSON-текст конфигурации. Комментарии и висячие запятые разрешены.
    /// Неизвестные ключи приводят к ошибке: опечатка в имени ключа иначе молча игнорировалась бы.
    /// </summary>
    /// <param name="json">Текст файла конфигурации.</param>
    /// <param name="sourcePath">Путь для сообщений об ошибках.</param>
    /// <exception cref="ArgumentException">JSON некорректен или содержит неподдерживаемые ключи.</exception>
    public static ConfigDto Deserialize(string json, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, CreateReaderOptions());
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber is { } line
                ? $" (line {line + 1}, position {ex.BytePositionInLine + 1})"
                : string.Empty;
            throw new ArgumentException($"Invalid JSON in {sourcePath}{location}: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"Invalid config in {sourcePath}: the root element must be a JSON object, but found {document.RootElement.ValueKind}.");
            }

            ValidateKeys(document.RootElement, sourcePath);

            try
            {
                var dto = document.RootElement.Deserialize<ConfigDto>(CreateSerializerOptions()) ?? new ConfigDto();

                // F-8.3: прежний единственный ключ secret переносится в список токенов.
                // Миграция выполняется при чтении, а не отдельным шагом: иначе пришлось бы
                // держать в модели два источника правды и решать, какой из них главнее.
                if (dto.Tokens is null or { Count: 0 }
                    && TryReadLegacySecret(document.RootElement, out var legacySecret))
                {
                    dto.Tokens = [TokenBootstrap.CreateDefaultToken(legacySecret)];
                }

                return dto;
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Invalid config in {sourcePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Сериализует полное представление конфигурации в JSON для записи в файл.
    /// </summary>
    public static string Serialize(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Serialize(ConfigDtoMapper.FromConfig(config));
    }

    /// <summary>
    /// Сериализует уже собранный DTO. Нужен откату (F-2.13): снимок прежнего файла снимается
    /// до записи кандидата, чтобы при неудачной перепривязке вернуть ровно то же содержимое.
    /// </summary>
    public static string Serialize(ConfigDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return JsonSerializer.Serialize(dto, CreateSerializerOptions());
    }

    /// <summary>
    /// Атомарно записывает конфигурацию в файл: сначала во временный файл рядом с целевым,
    /// затем переименование. Так оборванная запись не оставляет полупустой config.json,
    /// из которого прокси не сможет стартовать.
    /// </summary>
    /// <param name="path">Целевой путь.</param>
    /// <param name="config">Конфигурация для записи.</param>
    public static void Save(string path, Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        WriteRaw(path, Serialize(config));
    }

    /// <summary>
    /// Атомарно записывает готовый JSON. Используется и обычным сохранением, и откатом
    /// после неудачной перепривязки слушателя (F-2.13).
    /// </summary>
    /// <param name="path">Целевой путь.</param>
    /// <param name="json">Содержимое файла.</param>
    public static void WriteRaw(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // File.Move с overwrite атомарен в пределах одной файловой системы на Windows и Unix.
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Настройки чтения: комментарии и висячие запятые — для ручного редактирования.</summary>
    private static JsonDocumentOptions CreateReaderOptions() => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    /// <summary>Настройки десериализации: snake_case имена ключей, нечувствительность к регистру.</summary>
    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Читает устаревший ключ <c>secret</c> из корня файла (F-8.3).
    /// </summary>
    /// <param name="root">Корневой объект конфигурации.</param>
    /// <param name="secret">Значение ключа или <see langword="null"/>, если ключ отсутствует.</param>
    /// <returns><see langword="true"/>, если ключ присутствует (даже пустым).</returns>
    private static bool TryReadLegacySecret(JsonElement root, out string? secret)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, LegacySecretKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            secret = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            return true;
        }

        secret = null;
        return false;
    }

    /// <summary>
    /// Проверяет, что каждый ключ верхнего уровня известен DTO.
    /// Проверка выполняется по именам свойств DTO, поэтому список ключей не дублируется вручную
    /// и не рассинхронизируется при добавлении новых параметров.
    /// </summary>
    private static void ValidateKeys(JsonElement root, string sourcePath)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(ConfigDto).GetProperties())
        {
            if (property.CanWrite)
            {
                known.Add(property.Name);
            }
        }

        // F-8.3: secret убран из модели, но файл с ним обязан читаться — иначе обновление
        // версии падало бы на «Unknown config key» вместо тихой миграции.
        known.Add(LegacySecretKey);

        List<string>? unknown = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                unknown ??= [];
                unknown.Add(property.Name);
            }
        }

        if (unknown is null)
        {
            return;
        }

        var names = string.Join(", ", unknown.Select(static name => $"\"{name}\""));
        throw new ArgumentException($"Unknown config key(s) in {sourcePath}: {names}.");
    }
}
