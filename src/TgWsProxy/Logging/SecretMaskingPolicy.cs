using Serilog.Core;
using Serilog.Events;
using TgWsProxy.Infrastructure.Logging;

namespace TgWsProxy.Logging;

/// <summary>
/// Глобальная политика деструктуризации Serilog: заменяет целые объекты, которые могут содержать
/// секреты (конфигурация, токены доступа), значком <c>***</c>. Это защитная сетка на случай,
/// если в лог когда-нибудь попадёт <c>{@Config}</c>, <c>{@ConfigDto}</c>, <c>{@ProxyToken}</c>
/// или <c>{@ProxyTokenEntry}</c> — пароли/секреты до файла/консоли не дойдут. Остальные объекты
/// деструктурируются как обычно. Имена чувствительных типов и значение-заглушка — в
/// <see cref="LoggingConstants"/>.
/// </summary>
internal sealed class SecretMaskingPolicy : IDestructuringPolicy
{
    /// <inheritdoc />
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        if (!IsSensitive(value))
        {
            // Не наш тип — возвращаем false, чтобы Serilog применил деструктуризацию по умолчанию.
            // Рекурсивный вызов propertyValueFactory здесь привёл бы к повторному входу в эту же
            // политику и в итоге к урезанию объекта до ScalarValue.
            result = null!;
            return false;
        }

        result = new ScalarValue(LoggingConstants.MaskedSecret);
        return true;
    }

    private static bool IsSensitive(object value) =>
        value is not null && LoggingConstants.SensitiveTypeNames.Contains(value.GetType().Name);
}