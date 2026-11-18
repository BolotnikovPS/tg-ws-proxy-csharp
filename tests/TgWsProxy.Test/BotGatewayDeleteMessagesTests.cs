using System.Text;
using System.Text.Json;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Bot API <c>deleteMessage</c>/<c>deleteMessages</c>: разбор <c>message_ids</c> и wire-формат
/// <c>channels.deleteMessages</c> (в каналах <c>messages.deleteMessages</c> не удаляет сообщения).
/// </summary>
public class BotGatewayDeleteMessagesTests
{
    private static Dictionary<string, object> Args(params (string Key, object Value)[] pairs)
    {
        var args = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            args[key] = value;
        }

        return args;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadIntListArg_ParsesJsonArrayOfNumbers()
    {
        var ids = BotGateway.ReadIntListArg(Args(("message_ids", Json("[1, 2, 3]"))), "message_ids");

        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public void ReadIntListArg_ParsesJsonArrayOfStringNumbers()
    {
        var ids = BotGateway.ReadIntListArg(Args(("message_ids", Json("[\"10\", \"20\"]"))), "message_ids");

        Assert.Equal([10, 20], ids);
    }

    [Fact]
    public void ReadIntListArg_SkipsNonNumericItems()
    {
        var ids = BotGateway.ReadIntListArg(
            Args(("message_ids", Json("[1, \"abc\", 3, true, null]"))), "message_ids");

        Assert.Equal([1, 3], ids);
    }

    [Fact]
    public void ReadIntListArg_ParsesSingleNumber()
    {
        Assert.Equal([42], BotGateway.ReadIntListArg(Args(("message_ids", 42L)), "message_ids"));
        Assert.Equal([7], BotGateway.ReadIntListArg(Args(("message_ids", Json("7"))), "message_ids"));
    }

    [Fact]
    public void ReadIntListArg_ParsesCommaSeparatedString()
    {
        var ids = BotGateway.ReadIntListArg(Args(("message_ids", "5, 6,7")), "message_ids");

        Assert.Equal([5, 6, 7], ids);
    }

    [Fact]
    public void ReadIntListArg_ReturnsNull_WhenMissingOrEmpty()
    {
        Assert.Null(BotGateway.ReadIntListArg(Args(), "message_ids"));
        Assert.Null(BotGateway.ReadIntListArg(Args(("message_ids", null!)), "message_ids"));
        Assert.Null(BotGateway.ReadIntListArg(Args(("message_ids", " ")), "message_ids"));
        Assert.Null(BotGateway.ReadIntListArg(Args(("message_ids", "abc")), "message_ids"));
        Assert.Null(BotGateway.ReadIntListArg(null, "message_ids"));
    }

    /// <summary>channels.deleteMessages: ctor(4) | inputChannel ctor(4)+id(8)+hash(8) | vector ctor(4)+count(4)+ids(4×n).</summary>
    [Fact]
    public void Channels_DeleteMessages_Serialization_Layout()
    {
        var request = new MtProtoTL.Channels_DeleteMessages
        {
            channel = new MtProtoTL.InputChannel(777L, 999L),
            id = [11, 22],
        };

        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteTLObject(request);
        }

        var bytes = memory.ToArray();
        Assert.Equal(40, bytes.Length);
        Assert.Equal(unchecked((int)0x84C1FD4E), BitConverter.ToInt32(bytes, 0));
        Assert.Equal(unchecked((int)0xF35AEC28), BitConverter.ToInt32(bytes, 4));
        Assert.Equal(777L, BitConverter.ToInt64(bytes, 8));
        Assert.Equal(999L, BitConverter.ToInt64(bytes, 16));
        Assert.Equal(0x1CB5C415, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(2, BitConverter.ToInt32(bytes, 28));
        Assert.Equal(11, BitConverter.ToInt32(bytes, 32));
        Assert.Equal(22, BitConverter.ToInt32(bytes, 36));
    }

    [Fact]
    public void Channels_DeleteMessages_SingleMessage_Layout()
    {
        var request = new MtProtoTL.Channels_DeleteMessages
        {
            channel = new MtProtoTL.InputChannel(1L, 2L),
            id = [5],
        };

        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteTLObject(request);
        }

        var bytes = memory.ToArray();
        Assert.Equal(36, bytes.Length);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 28));
        Assert.Equal(5, BitConverter.ToInt32(bytes, 32));
    }
}
