using System.Reflection;

namespace TgWsProxy.Application;

/// <summary>
/// Факты о запущенном процессе, которые нужны и служебным эндпоинтам, и бизнес-логике:
/// версия сборки без суффикса «+hash» и календарный момент старта.
///
/// Регистрируется один раз при построении контейнера, поэтому <c>uptimeSeconds</c>
/// отсчитывается от старта приложения, а не от первого запроса.
/// </summary>
/// <param name="Version">Информационная версия сборки (например, <c>3.2.0-beta2</c>).</param>
/// <param name="StartedAt">Момент старта процесса (UTC).</param>
public sealed record AppRuntimeInfo(string Version, DateTimeOffset StartedAt)
{
    /// <summary>Время работы процесса в секундах.</summary>
    public long UptimeSeconds => (long)Math.Max(0, (DateTimeOffset.UtcNow - StartedAt).TotalSeconds);

    /// <summary>
    /// Снимает версию с входной сборки и фиксирует текущий момент как время старта.
    /// Если версия не проставлена, возвращается <c>unknown</c> вместо пустой строки.
    /// </summary>
    public static AppRuntimeInfo FromEntryAssembly()
    {
        var informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var version = informational?.Split('+')[0];
        return new AppRuntimeInfo(
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            DateTimeOffset.UtcNow);
    }
}