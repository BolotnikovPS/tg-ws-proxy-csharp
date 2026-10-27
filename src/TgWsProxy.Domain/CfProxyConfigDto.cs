namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление <see cref="CfProxyConfig"/> для файла <c>config.json</c> и Web API.
///
/// Скалярные поля намеренно nullable: это позволяет отличить «ключ отсутствует в файле»
/// (<c>null</c> — значение остаётся от предыдущего источника) от «ключ задан явно равным
/// значению по умолчанию», по тому же принципу, что и <see cref="ConfigDto"/>.
/// </summary>
public sealed class CfProxyConfigDto
{
    public bool? Enabled { get; set; }

    public bool? H2Media { get; set; }

    /// <summary><c>null</c> — не трогать; пустой массив — очистить список доменов.</summary>
    public List<string>? Domains { get; set; }

    /// <summary><c>null</c> — не трогать; пустой массив — очистить список worker-доменов.</summary>
    public List<string>? WorkerDomains { get; set; }
}