namespace TgWsProxy.Application.Logic;

/// <summary>
/// Общие переиспользуемые константы приложения — единый источник истины для значений,
/// которые раньше дублировались в разных классах (лимит учёта peer'ов и частота его
/// фоновой очистки).
/// </summary>
internal static class Constants
{
    /// <summary>Верхняя граница числа отслеживаемых адресов/пар в per-IP учёте и ограничителе частоты.</summary>
    public const int MaxTrackedPeers = 100_000;

    /// <summary>Период между фоновыми проходами очистки устаревших записей (секунды).</summary>
    public const double SweepIntervalSeconds = 30.0;
}
