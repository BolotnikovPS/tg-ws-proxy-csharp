using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class MsgContainerRoundTripTests
{
    [Fact]
    public void MsgContainer_RoundTrip_PreservesMessages()
    {
        var container = new MsgContainer
        {
            messages =
            [
                new _Message(1000, 1, new NewSessionCreated { first_msg_id = 1000, unique_id = 12345, server_salt = 0xDEADBEEF }),
                new _Message(2000, 3, new RpcResult { req_msg_id = 999, result = new Pong { msg_id = 2000, ping_id = 42 } }),
            ],
        };

        var bytes = container.ToBytes();

        // header must be msg_container#73f1f8dc
        Assert.Equal(0x73F1F8DCu, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(bytes));
        var obj = reader.ReadTLObject();

        var parsed = Assert.IsType<MsgContainer>(obj);
        Assert.Equal(2, parsed.messages.Count);

        var m0 = parsed.messages[0];
        Assert.Equal(1000L, m0.msg_id);
        Assert.Equal(1, m0.seqno);
        var ns = Assert.IsType<NewSessionCreated>(m0.body);
        Assert.Equal(1000L, ns.first_msg_id);
        Assert.Equal(12345L, ns.unique_id);
        Assert.Equal(0xDEADBEEFL, ns.server_salt);

        var m1 = parsed.messages[1];
        Assert.Equal(2000L, m1.msg_id);
        Assert.Equal(3, m1.seqno);
        var rr = Assert.IsType<RpcResult>(m1.body);
        Assert.Equal(999L, rr.req_msg_id);
        var pong = Assert.IsType<Pong>(rr.result);
        Assert.Equal(42L, pong.ping_id);
    }
}
