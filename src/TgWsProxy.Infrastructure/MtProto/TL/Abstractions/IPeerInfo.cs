#nullable disable

namespace TgWsProxy.Infrastructure.MtProto.TL.Abstractions;

/// <summary>Wire-level peer abstraction implemented by the generated TL types (User, Chat, Channel, …).
/// Not an Application port — it surfaces the concrete schema type <c>InputPeer</c>, so it stays in Infrastructure by design.</summary>
public interface IPeerInfo
{
    long ID { get; }
    bool IsActive { get; }
    string MainUsername { get; }
    InputPeer ToInputPeer();
}