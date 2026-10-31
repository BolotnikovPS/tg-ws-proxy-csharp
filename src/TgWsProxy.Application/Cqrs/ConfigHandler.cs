using Microsoft.Extensions.Logging;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using Wolverine.Attributes;

namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Обработчики запросов и команд конфигурации: HTTP-слой больше не знает,
/// как читается, маскируется и применяется конфигурация.
/// </summary>
/// <remarks>
/// Неудачная попытка обработки здесь — это штатный отказ (400/409), а не сбой сервиса,
/// поэтому Wolverine логирует её как Information: настоящие сбои (5xx) пишет
/// request-logging промежуточного слоя на уровне Error.
/// </remarks>
[WolverineLogging(FailureLogLevel = LogLevel.Information)]
public static class ConfigHandler
{
    /// <summary>Отдаёт маскированную конфигурацию для формы панели (F-2.10).</summary>
    /// <param name="query">Запрос конфигурации.</param>
    /// <param name="configService">Сервис живой конфигурации.</param>
    public static ConfigDto Handle(GetConfigQuery query, ConfigUpdateService configService)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(configService);

        return configService.GetMasked();
    }

    /// <summary>Отдаёт конфигурацию по умолчанию без записи файла (F-5.10).</summary>
    /// <param name="query">Запрос умолчаний.</param>
    public static ConfigDto Handle(GetDefaultConfigQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ConfigUpdateService.GetDefaults();
    }

    /// <summary>
    /// Применяет частичное изменение конфигурации транзакционно (F-2.9).
    /// Исключения (невалидные значения, ключи «только старт», сбой перепривязки)
    /// пробрасываются в HTTP-слой без изменений — их перевод в коды ответов остаётся
    /// обязанностью API.
    /// </summary>
    /// <param name="command">Команда с патчем конфигурации.</param>
    /// <param name="configService">Сервис живой конфигурации.</param>
    /// <param name="cancellationToken">Отмена запроса.</param>
    public static Task<ConfigUpdateResult> HandleAsync(
        UpdateConfigCommand command,
        ConfigUpdateService configService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(configService);

        return configService.ApplyAsync(command.Patch, cancellationToken);
    }
}
