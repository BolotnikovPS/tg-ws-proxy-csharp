#nullable disable

// Wire-format contracts shared by the generated TL schema types. These are not Application-layer
// ports — they surface concrete TL types (Peer, InputPeer), so they live in Infrastructure by design.

namespace TgWsProxy.Infrastructure.MtProto.TL.Abstractions;

public interface IObject { }
public interface IMethod<out TResult> : IObject { }
public interface IPeerResolver { IPeerInfo UserOrChat(Peer peer); }