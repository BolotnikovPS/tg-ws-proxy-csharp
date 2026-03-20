namespace TgWsProxy.Domain;

public sealed record ParsedTarget(string Host, int Port, int? Dc, bool? IsMedia);
