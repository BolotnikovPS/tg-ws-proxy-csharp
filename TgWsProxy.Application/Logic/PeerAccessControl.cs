using System.Net;
using System.Net.Sockets;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Проверка доступа клиента по IP-адресу: allow/deny списки с поддержкой CIDR.
/// Пустой allow — пускаем всех; пустой deny — никого не блокируем; deny имеет приоритет над allow.
/// Расширение порта, в upstream отсутствует.
/// </summary>
public sealed class PeerAccessControl(Config config)
{
    private readonly IPNetwork[] _allow = ParseAll(config.AllowIp);
    private readonly IPNetwork[] _deny = ParseAll(config.DenyIp);

    /// <summary>Число записей в allow-списке.</summary>
    public int AllowCount => _allow.Length;

    /// <summary>Число записей в deny-списке.</summary>
    public int DenyCount => _deny.Length;

    /// <summary>Разбирает одиночную запись вида IP или CIDR (например <c>192.168.1.5</c> или <c>10.0.0.0/8</c>).</summary>
    public static bool TryParseEntry(string entry, out IPNetwork network)
    {
        if (IPNetwork.TryParse(entry, out network))
        {
            return true;
        }
        if (IPAddress.TryParse(entry, out var ip))
        {
            var prefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            network = new IPNetwork(ip, prefix);
            return true;
        }
        return false;
    }

    /// <summary>Возвращает <c>true</c>, если адрес разрешён (не попадает под deny и входит в allow, если allow задан).</summary>
    public bool IsAllowed(IPAddress ip)
    {
        // IPv4-mapped IPv6 (например "::ffff:1.2.3.4") приводим к IPv4 для корректного сравнения.
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (_deny.Length > 0 && MatchesAny(_deny, ip))
        {
            return false;
        }
        return _allow.Length == 0 || MatchesAny(_allow, ip);
    }

    private static IPNetwork[] ParseAll(IEnumerable<string> entries)
    {
        var result = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (TryParseEntry(entry, out var network))
            {
                result.Add(network);
            }
        }
        return [.. result];
    }

    private static bool MatchesAny(IPNetwork[] networks, IPAddress ip) => networks.Any(z => z.Contains(ip));
}
