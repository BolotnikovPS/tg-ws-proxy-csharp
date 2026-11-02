using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class SerializationDiagnosticTests
{
    [Fact]
    public void Dump_ReqPqMulti_Serialization()
    {
        var nonce = new TgWsProxy.Infrastructure.MtProto.Int128([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        var req = new TgWsProxy.Infrastructure.MtProto.TL.Methods.ReqPqMulti { nonce = nonce };
        var bytes = req.ToBytes();
        var hex = Convert.ToHexString(bytes);
        // Expected: F1 8E 7E BE + nonce (00 01 02 ... 0F)
        Assert.Equal("F18E7EBE000102030405060708090A0B0C0D0E0F", hex);
    }

    [Fact]
    public void Dump_ResPQ_Readable_Type()
    {
        var nonce = new TgWsProxy.Infrastructure.MtProto.Int128([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);
        var req = new TgWsProxy.Infrastructure.MtProto.TL.Methods.ReqPqMulti { nonce = nonce };
        var bytes = req.ToBytes();
        // ctor 0xBE7E8EF1 is a method (ReqPqMulti); serialization must produce exactly 20 bytes.
        Assert.Equal(20, bytes.Length);
    }
}
