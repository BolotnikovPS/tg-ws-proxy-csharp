namespace TgWsProxy.Application.Logic;

/// <summary>Результат проверки одного дата-центра по одному маршруту.</summary>
/// <param name="Dc">Номер дата-центра (1–5, 203 — media).</param>
/// <param name="Ok"><see langword="true"/>, если маршрут до DC открыт.</param>
/// <param name="LatencyMs">Задержка установления соединения в мс либо <see langword="null"/> при неудаче.</param>
/// <param name="Error">Краткая причина неудачи либо <see langword="null"/>.</param>
/// <param name="Detail">Уточнение (какой адрес ответил, сколько адресов проверено).</param>
/// <param name="Ip">IP-адрес, к которому подключался маршрут «Прямой TCP» (из dcIp или дефолт Telegram); для остальных маршрутов <see langword="null"/>.</param>
public sealed record DcResult(int Dc, bool Ok, int? LatencyMs, string? Error, string? Detail, string? Ip);

/// <summary>Сводка проверки по одному маршруту (столбец матрицы «маршрут × DC»).</summary>
/// <param name="Route">Машинное имя маршрута (directWs/directTcp/cfProxy/cfWorker).</param>
/// <param name="Label">Человекочитаемое название маршрута.</param>
/// <param name="Results">Результаты по каждому DC (в порядке DC).</param>
/// <param name="Note">Примечание к маршруту (например, «домены не настроены»).</param>
public sealed record DcRouteCheck(string Route, string Label, IReadOnlyList<DcResult> Results, string? Note);

/// <summary>Полный отчёт проверки достижимости дата-центров.</summary>
/// <param name="CheckedAt">Момент начала проверки (UTC).</param>
/// <param name="Dcs">Список проверенных дата-центров.</param>
/// <param name="Routes">Сводки по каждому маршруту.</param>
public sealed record DcCheckReport(DateTimeOffset CheckedAt, IReadOnlyList<int> Dcs, IReadOnlyList<DcRouteCheck> Routes);
