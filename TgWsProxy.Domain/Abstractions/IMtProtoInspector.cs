namespace TgWsProxy.Domain.Abstractions;

public interface IMtProtoInspector
{
    (int? Dc, bool? IsMedia) DcFromInit(byte[] data);
}
