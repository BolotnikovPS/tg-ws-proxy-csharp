using TgWsProxy.Domain;

namespace TgWsProxy.Application.Cqrs;

/// <summary>
/// Запрос конфигурации для формы панели (F-2.10). Возвращает маскированную копию:
/// открытых секретов в ответе нет.
/// </summary>
public sealed record GetConfigQuery;

/// <summary>
/// Запрос конфигурации по умолчанию для кнопки «Сбросить» (F-5.10).
/// Ничего не пишет на диск: пользователь подтверждает сброс обычным сохранением.
/// </summary>
public sealed record GetDefaultConfigQuery;

/// <summary>
/// Команда сохранения конфигурации (F-2.9, F-2.11, F-2.12): транзакционное применение
/// изменений с записью файла, перезапуском слушателя и откатом при неудаче.
/// </summary>
/// <param name="Patch">Частичное изменение: <c>null</c> в поле означает «не менять».</param>
public sealed record UpdateConfigCommand(ConfigDto Patch);