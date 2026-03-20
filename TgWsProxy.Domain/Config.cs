namespace TgWsProxy.Domain;

public sealed class Config
{
    public int Port { get; set; } = 1080;
    public string Host { get; set; } = "127.0.0.1";
    public List<string> DcIp { get; } = [];
    public List<AuthCredential> Credentials { get; } = [];
    public bool Verbose { get; set; }
}
