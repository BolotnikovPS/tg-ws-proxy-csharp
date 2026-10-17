#nullable enable

using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

/// <summary>
/// Умолчания конфигурации, которые нельзя выразить инициализатором свойства, и производные
/// разборы её значений.
///
/// <see cref="Config.DcIp"/> — get-only коллекция, поэтому её начальное значение задаётся
/// явно, а не значением по умолчанию. Единая точка нужна ещё и потому, что список DC
/// применяется и загрузчиком файла, и тестами, и сбросом конфигурации к умолчаниям.
/// </summary>
public static class ConfigDefaults
{
    /// <summary>
    /// Дефолтные DC-редиректы upstream: без явного <c>dcIp</c> прокси работает через эти два DC.
    /// </summary>
    public static readonly string[] DefaultDcIp = ["2:149.154.167.220", "4:149.154.167.220"];

    /// <summary>
    /// Применяет к конфигурации умолчания, зависящие от источника.
    ///
    /// Вызывается сразу после создания <see cref="Config"/> и до чтения файла: так файл
    /// переопределяет умолчания, а не наоборот.
    /// </summary>
    /// <param name="cfg">Конфигурация, к которой применяются умолчания.</param>
    public static void Apply(Config cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        cfg.DcIp.Clear();
        cfg.DcIp.AddRange(DefaultDcIp);
    }

    /// <summary>
    /// Преобразует список записей <c>DC:IP</c> в отображение «номер DC → адрес».
    ///
    /// Формат проверяется здесь, а не в <see cref="ConfigValidator"/>, потому что карта
    /// нужна роутингу: без неё прокси не знает, куда подключаться для каждого DC.
    /// </summary>
    /// <param name="dcIpList">Записи вида <c>2:149.154.167.220</c>.</param>
    /// <returns>Отображение номера DC на IP-адрес.</returns>
    /// <exception cref="ArgumentException">Запись не соответствует формату <c>DC:IP</c>.</exception>
    public static Dictionary<int, string> ParseDcIpList(IEnumerable<string> dcIpList)
    {
        ArgumentNullException.ThrowIfNull(dcIpList);

        var map = new Dictionary<int, string>();
        foreach (var entry in dcIpList)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid dcIp entry \"{entry}\": expected \"DC:IP\"");
            }

            if (!int.TryParse(parts[0], out var dc) || !IPAddress.TryParse(parts[1], out _))
            {
                throw new ArgumentException($"Invalid dcIp entry \"{entry}\": expected \"DC:IP\"");
            }

            map[dc] = parts[1];
        }

        return map;
    }
}
