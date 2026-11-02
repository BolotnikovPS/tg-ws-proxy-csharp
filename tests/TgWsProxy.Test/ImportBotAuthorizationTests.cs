using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.MtProto.TL.Methods;

namespace TgWsProxy.Test;

public class ImportBotAuthorizationTests
{
    [Fact]
    public void ImportBotAuthorization_Serialization_Layout()
    {
        var req = new ImportBotAuthorization
        {
            flags = 0,
            api_id = 12345,
            api_hash = "abc",
            bot_auth_token = "123456789:AAHxyz",
        };

        var bytes = req.ToBytes();

        // ctor 0x67A3FF2C (little-endian), затем flags=0, api_id=12345
        Assert.Equal(36, bytes.Length);
        Assert.Equal(0x67A3FF2Cu, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(12345, BitConverter.ToInt32(bytes, 8));

        // api_hash = "abc": длина 3 (1 байт) + 3 байта, без паддинга (уже кратно 4)
        Assert.Equal(0x03, bytes[12]);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(bytes, 13, 3));

        // bot_auth_token = "123456789:AAHxyz": длина 16 + 16 байт + 3 байта паддинга
        Assert.Equal(0x10, bytes[16]);
        Assert.Equal("123456789:AAHxyz", System.Text.Encoding.UTF8.GetString(bytes, 17, 16));
        Assert.Equal(0, bytes[33]);
        Assert.Equal(0, bytes[34]);
        Assert.Equal(0, bytes[35]);
    }
}
