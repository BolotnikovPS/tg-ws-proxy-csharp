using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

public static class CliParser
{
    public static Config Parse(string[] args)
    {
        var cfg = new Config();

        static string NextValue(string[] argv, ref int index, string argName)
        {
            if (index + 1 >= argv.Length)
            {
                throw new ArgumentException($"Missing value for {argName}");
            }
            index++;
            return argv[index];
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
                    cfg.DcIp.Add(NextValue(args, ref i, "--dc-ip"));
                    break;

                case "--auth":
                    {
                        var auth = NextValue(args, ref i, "--auth");
                        var parts = auth.Split(':', 2);
                        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
                        {
                            throw new ArgumentException($"Invalid --auth format {auth}, expected LOGIN:PASSWORD");
                        }
                        cfg.Credentials.Add(new AuthCredential(parts[0], parts[1]));
                        break;
                    }
                case "-v":
                case "--verbose":
                    cfg.Verbose = true;
                    break;

                case "--log-path":
                    cfg.LogPath = NextValue(args, ref i, "--log-path");
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return cfg;
    }

    public static Dictionary<int, string> ParseDcIpList(List<string> dcIpList)
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in dcIpList)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new Exception($"Invalid --dc-ip format {entry}, expected DC:IP");
            }

            if (IPAddress.TryParse(parts[1], out _))
            {
                map[int.Parse(parts[0])] = parts[1];
            }
        }
        return map;
    }
}
