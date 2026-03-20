using System.Net;
using TgWsProxy.Domain;

namespace TgWsProxy.Application;

public static class CliParser
{
    public static Config Parse(string[] args)
    {
        var cfg = new Config();
        string? authLogin = null;
        string? authPassword = null;

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
                case "--auth-login":
                    authLogin = NextValue(args, ref i, "--auth-login");
                    break;
                case "--auth-password":
                    authPassword = NextValue(args, ref i, "--auth-password");
                    break;
                case "-v":
                case "--verbose":
                    cfg.Verbose = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (!string.IsNullOrEmpty(authLogin) || !string.IsNullOrEmpty(authPassword))
        {
            if (string.IsNullOrWhiteSpace(authLogin) || authPassword is null)
            {
                throw new ArgumentException("Both --auth-login and --auth-password must be provided");
            }
            cfg.Credentials.Add(new AuthCredential(authLogin, authPassword));
        }

        return cfg;
    }

    public static Dictionary<int, string> ParseDcIpList(List<string> dcIpList)
    {
        var map = new Dictionary<int, string>();
        foreach (var entry in dcIpList)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2) throw new Exception($"Invalid --dc-ip format {entry}, expected DC:IP");
            _ = IPAddress.Parse(parts[1]);
            map[int.Parse(parts[0])] = parts[1];
        }
        return map;
    }
}
