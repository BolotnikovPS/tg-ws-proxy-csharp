using TgWsProxy.Domain;

namespace TgWsProxy.Application.StartConfig;

public static class ConfigValidator
{
    public static void Validate(Config config)
    {
        if (config.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be in range 1..65535");
        }

        if (string.IsNullOrWhiteSpace(config.Host))
        {
            throw new ArgumentException("Host cannot be empty");
        }
    }
}
