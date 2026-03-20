using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Application;

internal sealed class MtProtoInspector : IMtProtoInspector
{
    public (int? Dc, bool? IsMedia) DcFromInit(byte[] data) => MtProtoUtil.DcFromInit(data);
}
