using System.Buffers.Binary;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.MtProto.TL.Methods;

namespace TgWsProxy.Test;

/// <summary>
/// Regression tests for the outbound MTProto 2.0 frame layout.
/// </summary>
/// <remarks>
/// The original implementation wrote the <c>message_data_length</c> field as the length of the
/// whole encrypted_data section (32 bytes too many), so the server read past the end of the TL
/// body and answered <c>400 INPUT_REQUEST_TOO_LONG</c> to every RPC — including
/// <c>auth.importBotAuthorization</c>, which made the first bot login impossible.
/// </remarks>
public class MtProtoOutboundFrameTests
{
    private const int AuthKeyPartSize = 32;  // auth_key[88..120], hashed but not encrypted
    private const int EnvelopeHeaderSize = 32; // salt(8) + session_id(8) + msg_id(8) + seq_no(4) + length(4)
    private const int MessageDataLengthOffset = 60;

    private static byte[] TestAuthKey()
    {
        var authKey = new byte[256];
        for (var i = 0; i < authKey.Length; i++)
        {
            authKey[i] = (byte)(i * 7 + 3);
        }

        return authKey;
    }

    /// <summary>
    /// Builds a synthetic <c>auth.importBotAuthorization</c> query.
    /// </summary>
    /// <remarks>
    /// The values here are deliberately fake placeholders shaped like real credentials
    /// (32-hex api_hash, "&lt;botId&gt;:&lt;secret&gt;" token). Never use real Telegram
    /// credentials in tests: this repository is public.
    /// </remarks>
    private static ImportBotAuthorization TestQuery() => new()
    {
        flags = 0,
        api_id = 12345,
        api_hash = "0123456789abcdef0123456789abcdef",
        bot_auth_token = "123456789:AAHfakeTestTokenDoNotUse123456789",
    };

    [Fact]
    public void BuildClearTextFrame_MessageDataLength_CountsOnlyTlBody()
    {
        var msg = TestQuery();
        var bodyLength = msg.ToBytes().Length;

        var frame = MtProtoClient.BuildClearTextFrame(
            TestAuthKey(), salt: 0x0123456789ABCDEF, sessionId: 0x0FEDCBA987654321,
            msgId: 7704675974342784068, seqno: 1, msg);

        // The declared length must be exactly the serialized TL body length.
        Assert.Equal(bodyLength, frame.MessageDataLength);
        Assert.Equal(AuthKeyPartSize + bodyLength, frame.ClearLength);

        var declared = BinaryPrimitives.ReadInt32LittleEndian(frame.Buffer.AsSpan(MessageDataLengthOffset));
        Assert.Equal(bodyLength, declared);

        // The bug: clearLength (header + body) was declared as the message data length.
        Assert.NotEqual(frame.ClearLength, declared);
        Assert.NotEqual(AuthKeyPartSize + bodyLength, declared);
    }

    [Fact]
    public void BuildClearTextFrame_WritesEnvelopeHeaderFields()
    {
        var msg = TestQuery();
        var salt = 0x0123456789ABCDEFL;
        var sessionId = 0x0FEDCBA987654321L;
        var msgId = 7704675974342784068L;
        var seqno = 1;

        var frame = MtProtoClient.BuildClearTextFrame(TestAuthKey(), salt, sessionId, msgId, seqno, msg);
        var buffer = frame.Buffer;

        // salt, session_id, msg_id, seq_no are written right after the 32-byte auth_key part.
        Assert.Equal(salt, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(AuthKeyPartSize)));
        Assert.Equal(sessionId, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(AuthKeyPartSize + 8)));
        Assert.Equal(msgId, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(AuthKeyPartSize + 16)));
        Assert.Equal(seqno, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(AuthKeyPartSize + 24)));

        // Content-related messages (RPC queries) MUST use an odd seq_no.
        Assert.True(seqno % 2 == 1);
    }

    [Fact]
    public void BuildClearTextFrame_TlBody_StartsRightAfterHeader_AndIsUnmodified()
    {
        var msg = TestQuery();
        var body = msg.ToBytes();

        var frame = MtProtoClient.BuildClearTextFrame(TestAuthKey(), 1, 2, 4, 1, msg);

        // The TL body begins at offset 32 (auth_key part) + 32 (envelope header) = 64.
        const int bodyOffset = AuthKeyPartSize + EnvelopeHeaderSize;
        Assert.Equal(body.Length, frame.MessageDataLength);
        Assert.Equal(body, frame.Buffer.AsSpan(bodyOffset, frame.MessageDataLength).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12345)]
    public void BuildClearTextFrame_IsPaddedTo16Bytes(long msgIdSeed)
    {
        var frame = MtProtoClient.BuildClearTextFrame(TestAuthKey(), 7, 8, 4 * (msgIdSeed + 1), 1, TestQuery());

        // MTProto 2.0: random padding of at least 12 bytes, encrypted total a multiple of 16.
        Assert.InRange(frame.Padding, 12, int.MaxValue);
        Assert.Equal(0, (AuthKeyPartSize + frame.ClearLength + frame.Padding) % 16);
        Assert.True(frame.Buffer.Length >= AuthKeyPartSize + frame.ClearLength + frame.Padding);
    }
}
