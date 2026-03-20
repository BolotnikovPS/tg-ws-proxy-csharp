namespace TgWsProxy.Domain;

public sealed record ClientContext(string Scope, string Peer, string ConnectionId);
