using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.MtProto;

namespace TgWsProxy.Test;

public class BotFileDownloadTests
{
    private static BotGateway NewGateway(bool enabled = true)
        => new(new BotApiConfig { Enabled = enabled, DataDir = "./bot-sessions" }, NullLogger<BotGateway>.Instance);

    [Fact]
    public async Task GetFile_Returns400_WhenFileIdMissing()
    {
        var gateway = NewGateway();
        var result = await gateway.HandleAsync("123:AAH", "getFile", new Dictionary<string, object>(), 1, "hash", default);
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public async Task GetFile_Returns400_WhenFileIdBlank()
    {
        var gateway = NewGateway();
        var result = await gateway.HandleAsync(
            "123:AAH", "getFile", new Dictionary<string, object> { ["file_id"] = "   " }, 1, "hash", default);
        Assert.False(result.Ok);
        Assert.Equal(400, result.ErrorCode);
    }

    [Fact]
    public void TryGetFile_ReturnsFalse_WhenMissing()
    {
        var gateway = NewGateway();
        Assert.False(gateway.TryGetFile("no-such-file-path", out _, out _));
    }

    [Fact]
    public void TryGetFile_ReturnsData_WhenPresent()
    {
        var gateway = NewGateway();
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        gateway.StoreFile("abc-path", expected, "image/png");

        Assert.True(gateway.TryGetFile("abc-path", out var data, out var contentType));
        Assert.True(data.Span.SequenceEqual(expected));
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public void DecodeInputPhoto_Throws400_WhenTooShort()
    {
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.DecodeInputPhoto(Convert.ToBase64String(new byte[20])));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void DecodeInputPhoto_Throws400_WhenWrongType()
    {
        var bytes = new byte[22];
        bytes[0] = 4; // не фото (фото — тип 2 или 3)
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.DecodeInputPhoto(Convert.ToBase64String(bytes)));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void DecodePhotoLocation_ReadsThumbSize()
    {
        // Раскладка текущего формата: type=3 | dc | id | access_hash | len | thumb | file_reference.
        var blob = new byte[22 + 3];
        blob[0] = 3;
        BitConverter.GetBytes(2).CopyTo(blob, 1);
        BitConverter.GetBytes(1234L).CopyTo(blob, 5);
        BitConverter.GetBytes(-5678L).CopyTo(blob, 13);
        blob[21] = 1;
        blob[22] = (byte)'m';

        var (photo, thumbSize) = BotGateway.DecodePhotoLocation(Convert.ToBase64String(blob));

        Assert.Equal("m", thumbSize);
        Assert.Equal(1234L, photo.id);
        Assert.Equal(-5678L, photo.access_hash);
    }

    [Fact]
    public void DecodePhotoLocation_LegacyFileId_HasEmptyThumbSize()
    {
        // file_id старого формата (type=2, без thumb_size) остаётся читаемым;
        // пустой thumb_size подменяется на LegacyPhotoThumbSize при скачивании.
        var blob = new byte[21];
        blob[0] = 2;
        BitConverter.GetBytes(2).CopyTo(blob, 1);
        BitConverter.GetBytes(99L).CopyTo(blob, 5);
        BitConverter.GetBytes(-77L).CopyTo(blob, 13);

        var (photo, thumbSize) = BotGateway.DecodePhotoLocation(Convert.ToBase64String(blob));

        Assert.Equal("", thumbSize);
        Assert.Equal("x", AppConstants.BotApi.LegacyPhotoThumbSize);
        Assert.Equal(99L, photo.id);
        Assert.Equal(-77L, photo.access_hash);
    }

    [Fact]
    public void DecodeInputDocument_Throws400_WhenWrongType()
    {
        var bytes = new byte[22];
        bytes[0] = 2; // фото, а не документ (документ — тип 5|8)
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.DecodeInputDocument(Convert.ToBase64String(bytes)));
        Assert.Equal(400, ex.ErrorCode);
    }

    [Fact]
    public void DecodeInputDocument_Throws400_WhenTooShort()
    {
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.DecodeInputDocument(Convert.ToBase64String(new byte[21])));
        Assert.Equal(400, ex.ErrorCode);
    }
}
