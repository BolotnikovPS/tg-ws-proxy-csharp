using System.Buffers.Binary;
using System.Text.Json;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.MtProto.TL;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;

namespace TgWsProxy.Test;

/// <summary>
/// Bot API допускает <c>chat_id</c> в виде <c>@channelusername</c> (Integer|String). Проверяем разбор
/// аргумента, wire-формат <c>contacts.resolveUsername</c> и перевод ответа в пару (chat_id, access_hash).
/// </summary>
public class BotGatewayUsernameTests
{
    [Fact]
    public void Contacts_ResolveUsername_Serialization_Layout()
    {
        var method = new Contacts_ResolveUsername { flags = 0, username = "durov" };

        var bytes = method.ToBytes();

        // ctor(4) | flags(4) | "durov" (len 1 + 5 байт + 2 padding = 8) = 16; referer не передан
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0x725AFBBCu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(5, bytes[8]); // длина строки: до 254 байт — один байт
        Assert.Equal("durov", System.Text.Encoding.UTF8.GetString(bytes, 9, 5));
    }

    [Fact]
    public void Contacts_ResolveUsername_Serialization_IncludesReferer_WhenFlagSet()
    {
        var method = new Contacts_ResolveUsername
        {
            flags = Contacts_ResolveUsername.Flags.has_referer,
            username = "durov",
            referer = "abc",
        };

        var bytes = method.ToBytes();

        // ctor(4) | flags=has_referer(4) | "durov"(8) | "abc"(len 1 + 3 байта = 4) = 20
        Assert.Equal(20, bytes.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(5, bytes[8]);
        Assert.Equal(3, bytes[16]);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(bytes, 17, 3));
    }

    [Theory]
    [InlineData("@durov", "durov")]
    [InlineData("durov", "durov")]
    [InlineData("  @durov  ", "durov")]
    [InlineData("@Channel_1", "Channel_1")]
    public void ReadUsernameArg_StripsAtSignAndWhitespace(string raw, string expected)
    {
        var args = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement(raw) };

        Assert.Equal(expected, BotGateway.ReadUsernameArg(args, "chat_id"));
    }

    [Fact]
    public void ReadUsernameArg_AcceptsPlainStringArgument()
    {
        var args = new Dictionary<string, object> { ["chat_id"] = "@durov" };

        Assert.Equal("durov", BotGateway.ReadUsernameArg(args, "chat_id"));
    }

    [Theory]
    [InlineData("153797471")]
    [InlineData("-1001234567890")]
    [InlineData("0")]
    public void ReadUsernameArg_ReturnsNull_ForNumericString(string raw)
    {
        var args = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement(raw) };

        Assert.Null(BotGateway.ReadUsernameArg(args, "chat_id"));
    }

    [Fact]
    public void ReadUsernameArg_ReturnsNull_ForNumberAndMissingArgument()
    {
        var numeric = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement(153797471) };
        var empty = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement("   ") };
        var missing = new Dictionary<string, object>();

        Assert.Null(BotGateway.ReadUsernameArg(numeric, "chat_id"));
        Assert.Null(BotGateway.ReadUsernameArg(empty, "chat_id"));
        Assert.Null(BotGateway.ReadUsernameArg(missing, "chat_id"));
        Assert.Null(BotGateway.ReadUsernameArg(null, "chat_id"));
    }

    [Theory]
    [InlineData("@")]
    [InlineData("@bad-name")]
    [InlineData("@with space")]
    [InlineData("@кириллица")]
    [InlineData("@abcdefghijklmnopqrstuvwxyzabcdefg")] // 33 символа
    public void ReadUsernameArg_Throws400_ForInvalidUsername(string raw)
    {
        var args = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement(raw) };

        var exception = Assert.Throws<BotGatewayException>(() => BotGateway.ReadUsernameArg(args, "chat_id"));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Contains("chat not found", exception.Message);
    }

    [Fact]
    public void ReadUsernameArg_Accepts32CharUsername()
    {
        var name = new string('a', 32);
        var args = new Dictionary<string, object> { ["chat_id"] = JsonSerializer.SerializeToElement("@" + name) };

        Assert.Equal(name, BotGateway.ReadUsernameArg(args, "chat_id"));
    }

    [Fact]
    public void MapResolvedPeer_MapsUser_AndCachesAccessHash()
    {
        // id намеренно «редкий»: кэши access_hash статические и общие на процесс.
        const long userId = 5_555_555_555;
        var resolved = new Contacts_ResolvedPeer
        {
            peer = new PeerUser { user_id = userId },
            users = new Dictionary<long, User> { [userId] = new() { id = userId, access_hash = 777 } },
            chats = [],
        };

        var (chatId, accessHash) = BotGateway.MapResolvedPeer(resolved);

        Assert.Equal(userId, chatId);
        Assert.Equal(777, accessHash);

        // access_hash попал в кэш: числовой chat_id без access_hash резолвится локально.
        var peer = BotGateway.ResolvePeer(new Dictionary<string, object> { ["chat_id"] = userId }, selfId: 1);
        var user = Assert.IsType<InputPeerUser>(peer);
        Assert.Equal(userId, user.user_id);
        Assert.Equal(777, user.access_hash);
    }

    [Fact]
    public void MapResolvedPeer_MapsChannel_ToMinus100ChatId_AndCachesAccessHash()
    {
        const long channelId = 7_777_777_777;
        var resolved = new Contacts_ResolvedPeer
        {
            peer = new PeerChannel { channel_id = channelId },
            users = [],
            chats = new Dictionary<long, ChatBase>
            {
                [channelId] = new Channel { id = channelId, access_hash = 555, title = "chan" },
            },
        };

        var (chatId, accessHash) = BotGateway.MapResolvedPeer(resolved);

        Assert.Equal(-1000000000000L - channelId, chatId);
        Assert.Equal(555, accessHash);

        var peer = BotGateway.ResolvePeer(new Dictionary<string, object> { ["chat_id"] = chatId }, selfId: 1);
        var channel = Assert.IsType<InputPeerChannel>(peer);
        Assert.Equal(channelId, channel.channel_id);
        Assert.Equal(555, channel.access_hash);
    }

    [Fact]
    public void MapResolvedPeer_MapsBasicChat_WithoutAccessHash()
    {
        var resolved = new Contacts_ResolvedPeer
        {
            peer = new PeerChat { chat_id = 99 },
            users = [],
            chats = [],
        };

        var (chatId, accessHash) = BotGateway.MapResolvedPeer(resolved);

        Assert.Equal(-99, chatId);
        Assert.Equal(0, accessHash);
    }

    [Fact]
    public void MapResolvedPeer_ReturnsZeroHash_WhenChatEntryMissing()
    {
        var resolved = new Contacts_ResolvedPeer
        {
            peer = new PeerChannel { channel_id = 5 },
            users = [],
            chats = [],
        };

        var (chatId, accessHash) = BotGateway.MapResolvedPeer(resolved);

        Assert.Equal(-1000000000000L - 5, chatId);
        Assert.Equal(0, accessHash);
    }

    [Fact]
    public void MapResolvedPeer_Throws400_WhenPeerIsMissing()
    {
        // В схеме проекта есть только peerUser/peerChat/peerChannel, поэтому «неизвестный пир» —
        // это отсутствующий peer (например, contacts.resolveUsername вернул пустой ответ).
        var nullResponse = Assert.Throws<BotGatewayException>(() => BotGateway.MapResolvedPeer(null));
        var emptyPeer = Assert.Throws<BotGatewayException>(
            () => BotGateway.MapResolvedPeer(new Contacts_ResolvedPeer { peer = null }));

        Assert.Equal(400, nullResponse.ErrorCode);
        Assert.Equal(400, emptyPeer.ErrorCode);
    }
}
