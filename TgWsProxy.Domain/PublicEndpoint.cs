#nullable enable

using System.Globalization;

namespace TgWsProxy.Domain;

/// <summary>
/// Внешняя точка доступа прокси — адрес и порт, которые попадают в ссылку
/// <c>https://t.me/proxy</c>. Хранится отдельно от адреса привязки слушателя:
/// в контейнере слушатель привязан к <c>0.0.0.0</c>, а снаружи доступен по
/// публичному адресу хоста.
/// </summary>
public readonly record struct PublicEndpoint(string Host, int Port)
{
    /// <summary>
    /// Разбирает <c>host</c>, <c>host:port</c> или <c>[IPv6]:port</c>. Порт по умолчанию —
    /// <paramref name="fallbackPort"/>. Служебные адреса (<c>0.0.0.0</c>, <c>::</c>, <c>*</c>)
    /// и пустая строка считаются неразбираемыми: такой адрес недостижим снаружи.
    /// </summary>
    public static bool TryParse(string? value, int fallbackPort, out PublicEndpoint endpoint)
    {
        endpoint = default;

        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }

        string host;
        var port = fallbackPort;

        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryParsePort(rest[1..], out port))
                {
                    return false;
                }
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            if (!TryParsePort(text[(colon + 1)..], out port))
            {
                return false;
            }
        }
        else
        {
            // Имя хоста, IPv4 или IPv6-литерал без порта.
            host = text;
        }

        host = host.Trim();
        if (host.Length == 0 || host.Contains('[') || host.Contains(']') || IsWildcard(host))
        {
            return false;
        }

        endpoint = new PublicEndpoint(host, port);
        return true;
    }

    /// <summary>
    /// Адрес, по которому сервер недостижим извне: любая привязка «на все интерфейсы».
    /// </summary>
    public static bool IsWildcard(string? host)
    {
        var text = host?.Trim() ?? "";
        return text is "0.0.0.0" or "::" or "[::]" or "*" or "+";
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value is < 1 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }
}
