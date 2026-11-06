#nullable disable
using TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Infrastructure.MtProto;

public interface IObject { }
public interface IMethod<out TResult> : IObject { }
public interface IPeerResolver { IPeerInfo UserOrChat(Peer peer); }