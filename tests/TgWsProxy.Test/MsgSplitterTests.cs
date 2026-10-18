using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты разборщика MTProto-пакетов: abridged (1/4-байтовый заголовок) и
/// intermediate/padded (LE32). Соответствует upstream <c>MsgSplitter</c>.
/// </summary>
public class MsgSplitterTests
{
    [Fact]
    public void Split_Abridged_SingleMessage_ReturnsOnePart()
    {
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);
        var frame = BuildAbridgedMessage(16);

        var parts = splitter.Split(frame, frame);

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_Abridged_TwoMessages_ReturnsTwoParts()
    {
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);
        var first = BuildAbridgedMessage(8);
        var second = BuildAbridgedMessage(12);
        var buffer = first.Concat(second).ToArray();

        var parts = splitter.Split(buffer, buffer);

        Assert.Equal(2, parts.Count);
        Assert.Equal(first, parts[0].ToArray());
        Assert.Equal(second, parts[1].ToArray());
    }

    [Fact]
    public void Split_Abridged_PartialMessage_ReturnsNothing_ThenCompletes()
    {
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);
        var frame = BuildAbridgedMessage(32);

        var head = frame.AsSpan(0, 10).ToArray();
        var tail = frame.AsSpan(10).ToArray();

        Assert.Empty(splitter.Split(head, head));

        var parts = splitter.Split(tail, tail);

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_Abridged_ExtendedLength_IsParsed()
    {
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);
        // 0x7F + LE24(len/4): 300 байт payload требуют расширенного заголовка.
        var frame = BuildAbridgedExtendedMessage(300);

        var parts = splitter.Split(frame, frame);

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_Intermediate_SingleMessage_ReturnsOnePart()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        var frame = BuildIntermediateMessage(24);

        var parts = splitter.Split(frame, frame);

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_Intermediate_TwoMessages_ReturnsTwoParts()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        var first = BuildIntermediateMessage(4);
        var second = BuildIntermediateMessage(64);
        var buffer = first.Concat(second).ToArray();

        var parts = splitter.Split(buffer, buffer);

        Assert.Equal(2, parts.Count);
        Assert.Equal(first, parts[0].ToArray());
        Assert.Equal(second, parts[1].ToArray());
    }

    [Fact]
    public void Split_Intermediate_PartialMessage_ReturnsNothing_ThenCompletes()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        var frame = BuildIntermediateMessage(48);

        Assert.Empty(splitter.Split(frame.AsSpan(0, 6).ToArray(), frame.AsSpan(0, 6).ToArray()));

        var parts = splitter.Split(frame.AsSpan(6).ToArray(), frame.AsSpan(6).ToArray());

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_PaddedIntermediate_UsesSameFraming_AsIntermediate()
    {
        var splitter = new MsgSplitter(Protocol.ProtoPaddedIntermediateInt);
        var frame = BuildIntermediateMessage(20);

        var parts = splitter.Split(frame, frame);

        Assert.Single(parts);
        Assert.Equal(frame, parts[0].ToArray());
    }

    [Fact]
    public void Split_ZeroLength_DisablesSplitting_AndReturnsRemainder()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        // Нулевая длина — некорректный поток: остаток отдаётся целиком, разбор отключается.
        var buffer = new byte[] { 0, 0, 0, 0, 1, 2, 3 };

        var parts = splitter.Split(buffer, buffer);

        Assert.Single(parts);
        Assert.Equal(buffer, parts[0].ToArray());
        Assert.True(splitter.Disabled);
    }

    [Fact]
    public void Split_WhenDisabled_PassesChunksThrough()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        splitter.Split(new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 0 });

        var chunk = new byte[] { 9, 9, 9 };
        var parts = splitter.Split(chunk, chunk);

        Assert.Single(parts);
        Assert.Equal(chunk, parts[0].ToArray());
    }

    [Fact]
    public void Flush_ReturnsUnparsedRemainder_AndResets()
    {
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);
        var frame = BuildAbridgedMessage(16);
        var head = frame.AsSpan(0, 5).ToArray();

        Assert.Empty(splitter.Split(head, head));

        var tail = splitter.Flush();

        Assert.Single(tail);
        Assert.Equal(head, tail[0].ToArray());
        // После flush буфер пуст.
        Assert.Empty(splitter.Flush());
    }

    [Fact]
    public void Split_UsesCiphertextForOutput_AndPlainForBoundaries()
    {
        var splitter = new MsgSplitter(Protocol.ProtoIntermediateInt);
        var plain = BuildIntermediateMessage(8);
        var cipher = plain.Select(b => (byte)(b ^ 0xFF)).ToArray();

        var parts = splitter.Split(cipher, plain);

        Assert.Single(parts);
        Assert.Equal(cipher, parts[0].ToArray());
    }

    private static byte[] BuildAbridgedMessage(int payloadLen)
    {
        var header = new byte[] { (byte)(payloadLen / 4) };
        return header.Concat(new byte[payloadLen]).ToArray();
    }

    private static byte[] BuildAbridgedExtendedMessage(int payloadLen)
    {
        var words = payloadLen / 4;
        var header = new byte[]
        {
            0x7F,
            (byte)(words & 0xFF),
            (byte)((words >> 8) & 0xFF),
            (byte)((words >> 16) & 0xFF),
        };
        return header.Concat(new byte[payloadLen]).ToArray();
    }

    private static byte[] BuildIntermediateMessage(int payloadLen)
    {
        var header = BitConverter.GetBytes(payloadLen);
        return header.Concat(new byte[payloadLen]).ToArray();
    }
}
