namespace TgWsProxy.Domain;

/// <summary>
/// Маскирование секретов для журнала и Web API (N-8, F-8.8). Секрет токена доступа прокси
/// и bearer-токен панели не должны покидать процесс в открытом виде: журнал и
/// <c>GET /api/config</c> / <c>GET /api/tokens</c> показывают только маску.
/// </summary>
public static class SecretMask
{
    /// <summary>
    /// Маскирует секрет как <c>abcd…ef01</c>: первые и последние четыре символа. Пустой секрет
    /// остаётся пустым, чтобы страница понимала, что он не задан.
    /// </summary>
    /// <param name="secret">Секрет в открытом виде либо <see langword="null"/>.</param>
    /// <returns>Маска, пустая строка либо <c>…</c> для коротких значений.</returns>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "";
        }

        return secret.Length <= 8
            ? "…"
            : string.Concat(secret.AsSpan(0, 4), "…", secret.AsSpan(secret.Length - 4));
    }

    /// <summary>
    /// Проверяет, является ли значение маской, полученной от страницы. Такое значение в запросе
    /// означает «не менять»: иначе маска затёрла бы настоящий секрет в файле.
    /// </summary>
    /// <param name="value">Значение из запроса.</param>
    /// <returns><see langword="true"/>, если значение выглядит как маска.</returns>
    public static bool IsMasked(string? value) =>
        value is not null && (value.Contains('…') || value.Contains('*'));
}
