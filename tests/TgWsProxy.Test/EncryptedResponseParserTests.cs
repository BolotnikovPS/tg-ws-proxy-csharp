using System.IO.Compression;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class EncryptedResponseParserTests
{
    private static MtProtoClient NewClient() => new("127.0.0.1", 1);

    private static GzipPacked Gzip(IObject obj)
    {
        var raw = obj.ToBytes();
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(raw, 0, raw.Length);
        }

        return new GzipPacked { packed_data = ms.ToArray() };
    }

    [Fact]
    public void MsgContainer_ExtractsMatchingRpcResult_AndUpdatesSalt()
    {
        var client = NewClient();
        var container = new MsgContainer
        {
            messages =
            [
                new _Message(1, 0, new NewSessionCreated { first_msg_id = 1, unique_id = 5, server_salt = 777 }),
                new _Message(2, 2, new RpcResult { req_msg_id = 999, result = new Pong { msg_id = 2, ping_id = 11 } }),
                new _Message(3, 4, new RpcResult { req_msg_id = 1000, result = new Pong { msg_id = 3, ping_id = 22 } }),
            ],
        };

        var result = client.ProcessServerObject(container, expectedMsgId: 1000);

        var pong = Assert.IsType<Pong>(result);
        Assert.Equal(22L, pong.ping_id);
        Assert.Equal(777L, client.ExportSession().Salt);
    }

    [Fact]
    public void GzipPacked_Result_IsUnwrapped()
    {
        var client = NewClient();
        var inner = new Pong { msg_id = 7, ping_id = 1234 };
        var rpc = new RpcResult { req_msg_id = 5, result = Gzip(inner) };

        var result = client.ProcessServerObject(rpc, expectedMsgId: 5);

        var pong = Assert.IsType<Pong>(result);
        Assert.Equal(1234L, pong.ping_id);
    }

    [Fact]
    public void RpcError_ThrowsRpcException()
    {
        var client = NewClient();
        var rpc = new RpcResult
        {
            req_msg_id = 5,
            result = new RpcError { error_code = 303, error_message = "SEE_OTHER" },
        };

        var ex = Assert.Throws<RpcException>(() => client.ProcessServerObject(rpc, expectedMsgId: 5));
        Assert.Equal(303, ex.Code);
    }

    [Fact]
    public void BadServerSalt_UpdatesSaltAndThrows()
    {
        var client = NewClient();
        var container = new MsgContainer
        {
            messages =
            [
                new _Message(1, 0, new BadServerSalt { bad_msg_id = 1, bad_msg_seqno = 0, error_code = 0, new_server_salt = 555 }),
            ],
        };

        Assert.Throws<MtProtoClient.ServerSaltChangedException>(() => client.ProcessServerObject(container, expectedMsgId: 1));
        Assert.Equal(555L, client.ExportSession().Salt);
    }

    [Fact]
    public void ReadEncryptedResponse_ParsesEnvelopeAndContainer()
    {
        var client = NewClient();
        const long expectedMsgId = 4242;
        var container = new MsgContainer
        {
            messages =
            [
                new _Message(1, 0, new NewSessionCreated { first_msg_id = 1, unique_id = 9, server_salt = 333 }),
                new _Message(2, 2, new RpcResult { req_msg_id = expectedMsgId, result = new Pong { msg_id = 2, ping_id = 77 } }),
            ],
        };

        // Build the inbound plaintext envelope: auth_key[32] salt session msg_id seqno data_len, then object.
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(new byte[32]);            // auth_key[88..120]
        writer.Write(0L);                       // server_salt
        writer.Write(123L);                     // session_id
        writer.Write(expectedMsgId);            // msg_id
        writer.Write(0);                        // seq_no
        writer.Write(0);                        // message_data_length
        writer.WriteTLObject(container);

        var result = client.ReadEncryptedResponse(ms.ToArray(), expectedMsgId);

        var pong = Assert.IsType<Pong>(result);
        Assert.Equal(77L, pong.ping_id);
        Assert.Equal(333L, client.ExportSession().Salt);
    }

    [Fact]
    public void GenerateMsgId_IsMultipleOfFour_AndMonotonic()
    {
        var client = NewClient();
        var first = client.GenerateMsgId();
        var second = client.GenerateMsgId();

        Assert.Equal(0, first % 4);
        Assert.Equal(0, second % 4);
        Assert.True(second > first);
    }
}
