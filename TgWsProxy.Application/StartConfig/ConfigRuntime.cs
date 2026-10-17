#nullable enable

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Сведения о файле конфигурации, которые нужны слою Web API: путь, с которым стартовал
/// процесс, и признак «путь задан явно». Снимаются один раз при старте, чтобы PUT
/// <c>/api/config</c> писал ровно туда, откуда читал (F-2.3, F-2.4).
/// </summary>
public sealed class ConfigRuntime
{
    /// <summary>Создаёт описание источника конфигурации.</summary>
    /// <param name="path">Путь к файлу конфигурации.</param>
    /// <param name="isExplicit">
    /// <c>true</c>, если путь задан пользователем (<c>--config</c> или <c>TGWS_CONFIG</c>);
    /// <c>false</c> для неявного <c>./config.json</c>.
    /// </param>
    public ConfigRuntime(string path, bool isExplicit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = path;
        IsExplicit = isExplicit;
    }

    /// <summary>Абсолютный путь к файлу конфигурации.</summary>
    public string Path { get; }

    /// <summary>Путь задан явно, а не найден по умолчанию.</summary>
    public bool IsExplicit { get; }

    /// <summary>
    /// Сериализует параллельные сохранения: два одновременных PUT не должны перемешивать
    /// временные файлы и оставлять на диске чужую версию.
    /// </summary>
    public SemaphoreSlim Sync { get; } = new(1, 1);
}
