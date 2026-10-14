#nullable enable

using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

public static class CliParser
{
    /// <summary>
    /// Дефолтные DC-редиректы upstream: без --dc-ip прокси работает через эти два DC.
    /// </summary>
    public static readonly string[] DefaultDcIp = ["2:149.154.167.220", "4:149.154.167.220"];

    public static Config Parse(string[] args)
    {
        var cfg = new Config();
        var dcIpProvided = false;
        var dcIpCleared = false;

        static string NextValue(string[] argv, ref int index, string argName)
        {
            if (index + 1 >= argv.Length)
            {
                throw new ArgumentException($"Missing value for {argName}");
            }
            index++;
            return argv[index].Trim();
        }

        // Значение опционально, но в отличие от argparse мы не можем отличить "--dc-ip 2:1.2.3.4"
        // от "--dc-ip --verbose" иначе как по префиксу "--".
        static string? NextOptionalValue(string[] argv, ref int index)
        {
            if (index + 1 >= argv.Length)
            {
                return null;
            }
            var candidate = argv[index + 1];
            if (candidate.StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }
            index++;
            return candidate.Trim();
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    cfg.Port = int.Parse(NextValue(args, ref i, "--port"));
                    break;

                case "--host":
                    cfg.Host = NextValue(args, ref i, "--host");
                    break;

                case "--dc-ip":
                    {
                        dcIpProvided = true;
                        var value = NextOptionalValue(args, ref i);
                        if (string.IsNullOrEmpty(value))
                        {
                            // Голый --dc-ip очищает список: WS-пул отключается, весь трафик в fallback.
                            cfg.DcIp.Clear();
                            dcIpCleared = true;
                        }
                        else if (!dcIpCleared)
                        {
                            cfg.DcIp.Add(value);
                        }
                        break;
                    }

                case "-v":
                case "--verbose":
                    cfg.Verbose = true;
                    break;

                case "--allow-invalid-certs":
                    cfg.AllowInvalidCertificates = true;
                    break;

                case "--log-file":
                case "--log-path":
                    cfg.LogPath = NextValue(args, ref i, args[i]);
                    break;

                case "--ws-timeout":
                    cfg.WsConnectTimeoutSeconds = int.Parse(NextValue(args, ref i, "--ws-timeout"));
                    break;

                case "--ws-max-frame-bytes":
                    cfg.WsMaxFrameBytes = int.Parse(NextValue(args, ref i, "--ws-max-frame-bytes"));
                    break;

                case "--buf-kb":
                    cfg.SocketBufferKb = int.Parse(NextValue(args, ref i, "--buf-kb"));
                    break;

                case "--pool-size":
                    cfg.WsPoolSize = int.Parse(NextValue(args, ref i, "--pool-size"));
                    break;

                case "--log-max-mb":
                    cfg.LogMaxMegabytes = double.Parse(NextValue(args, ref i, "--log-max-mb"), System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case "--log-backups":
                    cfg.LogRetainedFileCount = int.Parse(NextValue(args, ref i, "--log-backups"));
                    break;

                case "--no-cfproxy":
                    cfg.CfProxyEnabled = false;
                    break;

                case "--cfproxy-domain":
                    AddDomainList(cfg.CfProxyDomains, NextValue(args, ref i, "--cfproxy-domain"));
                    break;

                case "--cfproxy-worker-domain":
                    AddDomainList(cfg.CfProxyWorkerDomains, NextValue(args, ref i, "--cfproxy-worker-domain"));
                    break;

                case "--no-h2":
                    cfg.CfProxyH2Media = false;
                    break;

                case "--cfproxy-h2-media":
                    // Скрытый флаг upstream для совместимости; H2 включён по умолчанию.
                    cfg.CfProxyH2Media = true;
                    break;

                case "--no-secure":
                    cfg.DisableSecure = true;
                    break;

                case "--fake-tls-domain":
                    cfg.FakeTlsDomain = NextValue(args, ref i, "--fake-tls-domain");
                    break;

                case "--force-test-dc":
                    cfg.ForceTestDc = true;
                    break;

                case "--proxy-protocol":
                    cfg.ProxyProtocol = true;
                    break;

                case "--secret":
                    cfg.Secret = NextValue(args, ref i, "--secret");
                    break;

                case "--allow-ip":
                    AddIpList(cfg.AllowIp, NextValue(args, ref i, "--allow-ip"));
                    break;

                case "--deny-ip":
                    AddIpList(cfg.DenyIp, NextValue(args, ref i, "--deny-ip"));
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (!dcIpProvided)
        {
            cfg.DcIp.AddRange(DefaultDcIp);
        }

        return cfg;
    }

    /// <summary>
    /// Повторяемый флаг домена: значение разбивается по запятым/точкам с запятой/пробелам,
    /// дубликаты (без учёта регистра) отбрасываются. Соответствует upstream coerce_domain_list.
    /// </summary>
    private static void AddDomainList(List<string> target, string raw)
    {
        foreach (var item in raw.Replace(',', ' ').Replace(';', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var domain = item.Trim();
            if (domain.Length == 0)
            {
                continue;
            }
            if (target.Exists(existing => string.Equals(existing, domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            target.Add(domain);
        }
    }

    /// <summary>
    /// Повторяемый флаг IP/CIDR: значение разбивается по запятым/точкам с запятой/пробелам,
    /// дубликаты (без учёта регистра) отбрасываются.
    /// </summary>
    private static void AddIpList(List<string> target, string raw)
    {
        foreach (var item in raw.Replace(',', ' ').Replace(';', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ip = item.Trim();
            if (ip.Length == 0)
            {
                continue;
            }
            if (target.Exists(existing => string.Equals(existing, ip, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            target.Add(ip);
        }
    }

    /// <summary>
    /// Обрабатывает информационные флаги (--help/-h и --version) до разбора конфигурации.
    /// Upstream использует argparse, который печатает справку по -h; здесь поведение повторено.
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="version">Версия приложения (берётся из сборки exe-проекта).</param>
    /// <param name="output">Текст для вывода в stdout, если флаг распознан.</param>
    /// <returns>true, если нужно напечатать текст и завершиться с кодом 0.</returns>
    public static bool TryHandleInfo(string[] args, string version, out string output)
    {
        foreach (var arg in args)
        {
            if (arg is "-h" or "--help")
            {
                output = HelpText;
                return true;
            }
            if (arg is "--version")
            {
                output = $"tg-ws-proxy {version}";
                return true;
            }
        }

        output = string.Empty;
        return false;
    }

    /// <summary>Справка по всем поддерживаемым параметрам командной строки.</summary>
    public const string HelpText =
        """
        usage: TgWsProxy [--port PORT] [--host HOST] [--secret HEX] [--dc-ip DC:IP]
                         [--cfproxy-domain DOMAIN] [--cfproxy-worker-domain DOMAIN]
                         [--no-cfproxy] [--no-h2] [--no-secure] [--fake-tls-domain DOMAIN]
                         [--force-test-dc] [--proxy-protocol] [-v] [--log-file PATH]
                         [--log-max-mb MB] [--log-backups N] [--buf-kb KB] [--pool-size N]
                         [--ws-timeout SEC] [--ws-max-frame-bytes BYTES] [--allow-invalid-certs]
                         [--allow-ip IP/CIDR] [--deny-ip IP/CIDR]

        Telegram MTProto WebSocket Bridge Proxy

        options:
          -h, --help            показать эту справку и выйти
          --version             показать версию и выйти
          --port PORT           порт прослушивания (по умолчанию 1443)
          --host HOST           адрес прослушивания (по умолчанию 127.0.0.1)
          --secret HEX          32-символьный hex-секрет; при отсутствии генерируется
          --dc-ip DC:IP         DC-редирект, можно повторять; без значения очищает список
                                (по умолчанию 2:149.154.167.220 и 4:149.154.167.220)
          --cfproxy-domain D    домен Cloudflare-прокси, можно повторять
          --cfproxy-worker-domain D
                                домен CF Worker, можно повторять
          --no-cfproxy          отключить Cloudflare-фолбэк
          --no-h2               отключить мультиплексирование media через HTTP/2
          --no-secure           использовать ws:// вместо wss://
          --fake-tls-domain D   домен для Fake TLS (маскировка ClientHello)
          --force-test-dc       использовать тестовые DC
          --proxy-protocol      ожидать заголовок PROXY protocol v1
          -v, --verbose         подробный лог (Debug)
          --log-file PATH       файл лога с ротацией (алиас --log-path)
          --log-max-mb MB       лимит размера файла лога (по умолчанию 5)
          --log-backups N       число хранимых файлов лога (по умолчанию 1)
          --buf-kb KB           размер буфера сокета в КБ (по умолчанию 256)
          --pool-size N         размер пула WS-соединений на DC (по умолчанию 4)
          --ws-timeout SEC      таймаут установки WS-соединения (по умолчанию 10)
          --ws-max-frame-bytes BYTES
                                максимальный размер WS-кадра (по умолчанию 16 МиБ)
          --allow-invalid-certs не проверять сертификаты TLS
          --allow-ip IP/CIDR   разрешить подключение только с этих IP/подсетей, можно
                                повторять (пусто — пускаем всех)
          --deny-ip IP/CIDR    запретить подключение с этих IP/подсетей, можно
                                повторять (пусто — никого не блокируем)
        """;

    public static Dictionary<int, string> ParseDcIpList(List<string> dcIpList)
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in dcIpList)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid --dc-ip format {entry}, expected DC:IP");
            }

            if (!int.TryParse(parts[0], out var dc) || !IPAddress.TryParse(parts[1], out _))
            {
                throw new ArgumentException($"Invalid --dc-ip {entry}");
            }

            map[dc] = parts[1];
        }
        return map;
    }
}
