using System.Buffers.Binary;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.MtProto.TL;
using TgWsProxy.Infrastructure.MtProto.TL.Methods;
using Xunit;

namespace TgWsProxy.Test;

public class BotApiSchemaTests
{
    [Fact]
    public void Users_GetUsers_Serialization_WritesVectorOfInputUserSelf()
    {
        var method = new Users_GetUsers
        {
            id = [new InputUserSelf()]
        };

        var bytes = method.ToBytes();

        // ctor users.getUsers#d91a548(4) | vector#1cb5c415(4) | count=1(4) | InputUserSelf#f7c1b13f(4)
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0x0D91A548u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(0x1CB5C415u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0xF7C1B13Fu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
    }

    [Fact]
    public void Updates_GetState_Serialization_IsJustCtor()
    {
        var bytes = new Updates_GetState().ToBytes();

        Assert.Equal(4, bytes.Length);
        Assert.Equal(0xEDD4882Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }

    [Fact]
    public void Updates_GetDifference_Serialization_SkipsUnsetFlags()
    {
        var method = new Updates_GetDifference
        {
            flags = Updates_GetDifference.Flags.has_pts_total_limit,
            pts = 100,
            pts_total_limit = 0,
            date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            qts = 0,
        };

        var bytes = method.ToBytes();

        // ctor(4) + flags(4) + pts(4) + pts_total_limit(4) + date(4) + qts(4) = 24
        Assert.Equal(24, bytes.Length);
        Assert.Equal(0x19C2F763u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));  // flags = has_pts_total_limit
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8))); // pts
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)));  // pts_total_limit
    }

    [Fact]
    public void ReadTLObject_ReadsVectorResult_AsTLVector()
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x1CB5C415u); // vector ctor
        w.Write(1);           // count = 1
        w.WriteTLObject(new User { id = 42, flags = 0, flags2 = 0 });
        w.Flush();
        ms.Position = 0;

        using var r = new BinaryReader(ms);
        var obj = r.ReadTLObject();

        var vector = Assert.IsType<TLVector>(obj);
        var user = Assert.IsType<User>(Assert.Single(vector.Items));
        Assert.Equal(42L, user.id);
    }

    [Fact]
    public void Messages_SendMessage_Serialization_Layout()
    {
        var method = new Messages_SendMessage
        {
            flags = 0,
            peer = new InputPeerUser { user_id = 123, access_hash = 456 },
            message = "hi",
            random_id = 0x1122334455667788L,
        };

        var bytes = method.ToBytes();

        // ctor(4) | flags(4) | peer inputPeerUser#dde8a54c(4) + user_id(8) + access_hash(8) | message "hi"(4) | random_id(8)
        Assert.Equal(40, bytes.Length);
        Assert.Equal(0xFEF48F62u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));   // flags
        Assert.Equal(0xDDE8A54Cu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8))); // peer ctor
        Assert.Equal(123L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12))); // user_id
        Assert.Equal(456L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20))); // access_hash
        Assert.Equal(0x1122334455667788L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32))); // random_id
    }
}
