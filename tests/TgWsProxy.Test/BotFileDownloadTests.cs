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
        var bytes = new byte[21];
        bytes[0] = 3; // не фото (фото — тип 2)
        var ex = Assert.Throws<BotGatewayException>(() => BotGateway.DecodeInputPhoto(Convert.ToBase64String(bytes)));
        Assert.Equal(400, ex.ErrorCode);
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