#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Регрессия на отбраковку «мёртвых» соединений в WS-пуле.
/// Раньше <see cref="IRawWebSocket.IsRemoteClosed"/> возвращал <see langword="true"/>
/// только для «чистого» FIN (<c>Poll(SelectRead) &amp;&amp; Available == 0</c>).
/// Telegram же закрывает простаивающие WS, присылая TLS close_notify либо WS close-кадр:
/// это байты в приёмном буфере, поэтому сокет «читаем», но <c>Available != 0</c> —
/// соединение считалось живым, выдавалось клиенту, и тот мгновенно получал обрыв
/// (в логах: pool hit, следом «WS read ended (peer closed TCP without WS close frame)»).
/// Простаивающее соединение в пуле не должно получать от Telegram ничего до отправки
/// obfs-init, поэтому любая готовность к чтению означает разрыв.
/// </summary>
public class RawWebSocketLivenessTests
{
    private const int WsMaxFrameBytes = 16 * 1024 * 1024;

    [Fact]
    public async Task IsRemoteClosed_False_WhenPeerAliveAndIdle()
    {
        var (ws, peer, server) = await ConnectPairAsync();
        try
        {
            // Свежее соединение без трафика: пул вправе выдать его клиенту.
            Assert.False(ws.IsClosed);
            Assert.False(ws.IsRemoteClosed);
        }
        finally
        {
            Close(ws, peer, server);
        }
    }

    [Fact]
    public async Task IsRemoteClosed_True_WhenPeerSendsFin()
    {
        var (ws, peer, server) = await ConnectPairAsync();
        try
        {
            peer.Shutdown(SocketShutdown.Send);

            Assert.True(
                await WaitUntilAsync(() => ws.IsRemoteClosed),
                "FIN от пира должен помечать соединение как закрытое удалённой стороной");
        }
        finally
        {
            Close(ws, peer, server);
        }
    }

    /// <summary>
    /// Ключевой случай регрессии: пир прислал байты (close_notify / WS close-кадр),
    /// но TCP-соединение ещё не разорвано. Такое соединение непригодно, хотя
    /// <c>Available != 0</c>.
    /// </summary>
    [Fact]
    public async Task IsRemoteClosed_True_WhenPeerSentBytesWithoutClosing()
    {
        var (ws, peer, server) = await ConnectPairAsync();
        try
        {
            // 0x15 0x03 0x03 ... — форма TLS alert (close_notify), т.е. именно тот случай,
            // который раньше ошибочно считался живым соединением.
            var closeNotify = new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x01, 0x00 };
            peer.Send(closeNotify);

            Assert.True(
                await WaitUntilAsync(() => server.Available > 0),
                "байты пира должны дойти до нашего сокета");

            // Сокет читаем, и в буфере есть данные — прежняя проверка (Available == 0)
            // вернула бы false, то есть «соединение живо».
            Assert.True(server.Available > 0);
            Assert.True(
                ws.IsRemoteClosed,
                "данные от пира до отправки obfs-init означают, что соединение уже не живо");
        }
        finally
        {
            Close(ws, peer, server);
        }
    }

    [Fact]
    public async Task IsRemoteClosed_True_AfterLocalClose()
    {
        var (ws, peer, server) = await ConnectPairAsync();
        try
        {
            await ws.Close(CancellationToken.None);

            Assert.True(ws.IsClosed);
            Assert.True(ws.IsRemoteClosed);
        }
        finally
        {
            Close(ws, peer, server);
        }
    }

    /// <summary>
    /// Создаёт реальную пару соединённых сокетов через loopback и оборачивает
    /// серверную сторону в <see cref="RawWebSocket"/>.
    /// Транспорт подменён <see cref="MemoryStream"/>: проверка живучести работает
    /// напрямую с сокетом и транспорта не касается.
    /// </summary>
    private static async Task<(RawWebSocket Ws, Socket Peer, Socket Server)> ConnectPairAsync()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await peer.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        var server = await listener.AcceptAsync();

        var ws = new RawWebSocket(server, new MemoryStream(), "test", NullLogger.Instance, WsMaxFrameBytes);
        return (ws, peer, server);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static void Close(RawWebSocket ws, Socket peer, Socket server)
    {
        ws.Close(CancellationToken.None).GetAwaiter().GetResult();
        peer.Dispose();
        server.Dispose();
    }
}
