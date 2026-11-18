using System;
using System.IO;
using System.Text.Json;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.MtProto;
using Xunit;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Проверки альбомного пути (<c>sendMediaGroup</c>): элементы с локальной загрузкой обязаны
/// проходить через <c>messages.uploadMedia</c>, поэтому важна корректная раскладка этого запроса.
/// </summary>
public sealed class BotGatewayAlbumMediaTests
{
    private static byte[] Serialize(MtProtoTL.Messages_UploadMedia request)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteTLObject(request);
        }

        return stream.ToArray();
    }

    [Fact]
    public void Messages_UploadMedia_Serialization_Layout()
    {
        var request = new MtProtoTL.Messages_UploadMedia
        {
            flags = 0,
            peer = new MtProtoTL.InputPeerUser { user_id = 1, access_hash = 2 },
            media = new MtProtoTL.InputMediaPhoto
            {
                flags = 0,
                id = new MtProtoTL.InputPhoto { id = 3, access_hash = 4, file_reference = [] },
            },
        };

        var bytes = Serialize(request);

        // ctor(4) | flags(4) | peer: inputPeerUser(4) + user_id(8) + access_hash(8)
        // | media: inputMediaPhoto(4) + flags(4) + inputPhoto: ctor(4) + id(8) + access_hash(8) + file_reference(4)
        Assert.Equal(60, bytes.Length);
        Assert.Equal(unchecked((int)0x14967978), BitConverter.ToInt32(bytes, 0));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(unchecked((int)0xDDE8A54C), BitConverter.ToInt32(bytes, 8));
        Assert.Equal(1L, BitConverter.ToInt64(bytes, 12));
        Assert.Equal(2L, BitConverter.ToInt64(bytes, 20));
        Assert.Equal(unchecked((int)0xE3AF4434), BitConverter.ToInt32(bytes, 28));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 32));
        Assert.Equal(unchecked((int)0x3BB3B94A), BitConverter.ToInt32(bytes, 36));
        Assert.Equal(3L, BitConverter.ToInt64(bytes, 40));
        Assert.Equal(4L, BitConverter.ToInt64(bytes, 48));
        Assert.Equal(0, bytes[56]);
    }

    [Fact]
    public void MessageMediaPhoto_Serialization_ReadsBackAsPhoto()
    {
        // Ровно этот ответ разбирает альбомный путь после uploadMedia.
        var response = new MtProtoTL.MessageMediaPhoto
        {
            flags = MtProtoTL.MessageMediaPhoto.Flags.has_photo,
            photo = new MtProtoTL.Photo { id = 5465507302375891167, access_hash = -42 },
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteTLObject(response);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var parsed = reader.ReadTLObject() as MtProtoTL.MessageMedia;

        var mediaPhoto = Assert.IsType<MtProtoTL.MessageMediaPhoto>(parsed);
        var photo = Assert.IsType<MtProtoTL.Photo>(mediaPhoto.photo);
        Assert.Equal(5465507302375891167, photo.id);
        Assert.Equal(-42, photo.access_hash);
    }

    private static Dictionary<string, object> Media(string json) => new() { ["media"] = json };

    [Fact]
    public void ValidateMediaGroupItems_AcceptsTwoPhotos()
    {
        var (error, items) = BotGateway.ValidateMediaGroupItems(
            Media("""[{"type":"photo","media":"attach://p1"},{"type":"photo","media":"AwIAAACF"}]"""));

        Assert.Null(error);
        Assert.Equal(2, items.Length);
    }

    [Fact]
    public void ValidateMediaGroupItems_AcceptsPhotoWithVideo()
    {
        // Фото и видео Telegram собирает в один альбом.
        var (error, items) = BotGateway.ValidateMediaGroupItems(
            Media("""[{"type":"photo","media":"AwIAAACF"},{"type":"video","media":"BQACAAAA"}]"""));

        Assert.Null(error);
        Assert.Equal(2, items.Length);
    }

    [Theory]
    [InlineData("""[{"type":"photo","media":"AwIAAACF"},{"type":"document","media":"BQACAAAA"}]""")]
    [InlineData("""[{"type":"document","media":"BQACAAAA"},{"type":"audio","media":"BQACAAAA"}]""")]
    public void ValidateMediaGroupItems_RejectsMixedKinds(string json)
    {
        // Смесь фото с документом Telegram отклоняет как MEDIA_INVALID — отсекаем сами.
        var (error, _) = BotGateway.ValidateMediaGroupItems(Media(json));

        Assert.Equal("media group must contain only one kind of media: photos/videos, documents or audio", error);
    }

    [Theory]
    [InlineData("""[{"type":"photo","media":"AwIAAACF"}]""")]
    [InlineData("""[{"type":"photo","media":"a"},{"type":"photo","media":"b"},{"type":"photo","media":"c"},{"type":"photo","media":"d"},{"type":"photo","media":"e"},{"type":"photo","media":"f"},{"type":"photo","media":"g"},{"type":"photo","media":"h"},{"type":"photo","media":"i"},{"type":"photo","media":"j"},{"type":"photo","media":"k"}]""")]
    public void ValidateMediaGroupItems_RequiresTwoToTenItems(string json)
    {
        var (error, _) = BotGateway.ValidateMediaGroupItems(Media(json));

        Assert.Equal("media group must include 2-10 items", error);
    }

    [Fact]
    public void ValidateMediaGroupItems_RequiresObjectsAndMediaField()
    {
        var (notObject, _) = BotGateway.ValidateMediaGroupItems(Media("""["attach://p1","attach://p2"]"""));
        Assert.Equal("each sendMediaGroup item must be an object", notObject);

        var (missing, _) = BotGateway.ValidateMediaGroupItems(new Dictionary<string, object>());
        Assert.Equal("media is required for sendMediaGroup", missing);
    }

    [Fact]
    public void ValidateMediaGroupItems_RejectsUnsupportedKind()
    {
        var exception = Assert.Throws<BotGatewayException>(() => BotGateway.ValidateMediaGroupItems(
            Media("""[{"type":"sticker","media":"a"},{"type":"sticker","media":"b"}]""")));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Equal("unsupported media type 'sticker' in sendMediaGroup", exception.Message);
    }

    [Fact]
    public void ValidateMediaGroupItems_AcceptsJsonElementArray()
    {
        // multipart-запрос приходит строкой, POST с JSON-телом — уже разобранным JsonElement.
        using var doc = JsonDocument.Parse(
            """[{"type":"document","media":"attach://d1"},{"type":"document","media":"attach://d2"}]""");
        var args = new Dictionary<string, object> { ["media"] = doc.RootElement.Clone() };

        var (error, items) = BotGateway.ValidateMediaGroupItems(args);

        Assert.Null(error);
        Assert.Equal(2, items.Length);
    }
}
