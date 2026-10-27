using Serilog.Core;
using Serilog.Events;

namespace TgWsProxy.Test;

/// <summary>
/// Простой Serilog-sink для тестов: собирает события в список, чтобы проверять структурированные
/// свойства и результаты деструктуризации.
/// </summary>
internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}