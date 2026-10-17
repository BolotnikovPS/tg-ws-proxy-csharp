#nullable enable

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Отказ применить конфигурацию из-за ключей категории «только старт» (F-2.12).
/// Вызывающая сторона превращает это исключение в <c>409</c> с полем <c>startupOnly</c>.
/// </summary>
/// <param name="StartupOnly">Ключи, которые можно изменить только перезапуском процесса.</param>
public sealed class StartupOnlyKeysException(IReadOnlyList<string> startupOnly)
    : Exception(
        "These keys are read once at startup and cannot be changed at runtime: " +
        string.Join(", ", startupOnly))
{
    /// <summary>Ключи, отклонённые запросом.</summary>
    public IReadOnlyList<string> StartupOnly { get; } = startupOnly;
}

/// <summary>
/// Отказ применить конфигурацию из-за неудачной перепривязки слушателя (F-7.5).
/// Файл и память уже возвращены в прежнее состояние: <c>rolledBack = true</c>.
/// </summary>
/// <param name="Detail">Причина (например, занятый порт).</param>
public sealed class ListenerRebindException(string detail) : Exception(detail);
