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

        if (config.WsConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentException("WsConnectTimeoutSeconds (--ws-timeout) must be in range 1..300");
        }

        if (config.SocketBufferKb is < 4 or > 8192)
        {
            throw new ArgumentException("SocketBufferKb (--buf-kb) must be in range 4..8192");
        }

        if (config.WsPoolSize is < 0 or > 64)
        {
            throw new ArgumentException("WsPoolSize (--pool-size) must be in range 0..64");
        }

        if (config.LogMaxMegabytes is < 0 or > 4096)
        {
            throw new ArgumentException("LogMaxMegabytes (--log-max-mb) must be in range 0..4096");
        }

        if (config.LogRetainedFileCount is < 0 or > 1000)
        {
            throw new ArgumentException("LogRetainedFileCount (--log-backups) must be in range 0..1000");
        }
    }
}
