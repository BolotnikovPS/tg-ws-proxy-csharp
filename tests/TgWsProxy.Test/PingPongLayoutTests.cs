using System;
using System.IO;
using TgWsProxy.Infrastructure;
using TgWsProxy.Infrastructure.MtProto.TL;
using TgWsProxy.Infrastructure.MtProto.TL.Abstractions;
using TgWsProxy.Infrastructure.MtProto.TL.MtProtos;
using Xunit;

namespace TgWsProxy.Test;

/// <summary>
/// Сервисные ping/pong транспортного уровня. Они не входят в Bot API layer, объявлены вручную
/// (ping#7abe77ec / ping_delay_disconnect#f3427b8c / pong#347773c5), поэтому раскладку легко
/// случайно испортить: сервер закрывает соединение, если на его ping не ответить pong,
/// а неверный порядок полей даёт молчаливый мусор вместо ответа.
/// </summary>
public sealed class PingPongLayoutTests
{
    private static byte[] Serialize(IObject obj)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteTLObject(obj);
        }

        return stream.ToArray();
    }

    private static IObject? Deserialize(byte[] bytes) =>
        new BinaryReader(new MemoryStream(bytes), System.Text.Encoding.UTF8).ReadTLObject();

    [Fact]
    public void Ping_Serialization_Layout()
    {
        var bytes = Serialize(new Ping { ping_id = 0x0102030405060708L });

        Assert.Equal(12, bytes.Length);
        Assert.Equal(new byte[] { 0xEC, 0x77, 0xBE, 0x7A }, bytes[..4]);
        Assert.Equal(0x0102030405060708L, BitConverter.ToInt64(bytes, 4));
    }

    [Fact]
    public void Pong_Serialization_Layout()
    {
        // pong: msg_id — это msg_id принятого ping, ping_id — ping_id из ping.
        var bytes = Serialize(new Pong { msg_id = 111, ping_id = 222 });

        Assert.Equal(20, bytes.Length);
        Assert.Equal(new byte[] { 0xC5, 0x73, 0x77, 0x34 }, bytes[..4]);
        Assert.Equal(111L, BitConverter.ToInt64(bytes, 4));
        Assert.Equal(222L, BitConverter.ToInt64(bytes, 12));
    }

    [Fact]
    public void PingDelayDisconnect_Serialization_Layout()
    {
        var bytes = Serialize(new PingDelayDisconnect { ping_id = 5, disconnect_delay = 300 });

        Assert.Equal(16, bytes.Length);
        Assert.Equal(new byte[] { 0x8C, 0x7B, 0x42, 0xF3 }, bytes[..4]);
        Assert.Equal(5L, BitConverter.ToInt64(bytes, 4));
        Assert.Equal(300, BitConverter.ToInt32(bytes, 12));
    }

    [Fact]
    public void Ping_Types_AreReadable_FromStream()
    {
        // ping и ping_delay_disconnect приходят от сервера, поэтому обязаны попадать в Layer.Table:
        // методы (: IMethod<T>) из таблицы исключены и из потока не читаются.
        Assert.True(Layer.Table.ContainsKey(0x7ABE77EC));
        Assert.True(Layer.Table.ContainsKey(0xF3427B8C));
        Assert.True(Layer.Table.ContainsKey(0x347773C5));

        var ping = Assert.IsType<Ping>(Deserialize(Serialize(new Ping { ping_id = 42 })));
        Assert.Equal(42L, ping.ping_id);

        var delay = Assert.IsType<PingDelayDisconnect>(
            Deserialize(Serialize(new PingDelayDisconnect { ping_id = 7, disconnect_delay = 60 })));
        Assert.Equal(7L, delay.ping_id);
        Assert.Equal(60, delay.disconnect_delay);
    }
}
