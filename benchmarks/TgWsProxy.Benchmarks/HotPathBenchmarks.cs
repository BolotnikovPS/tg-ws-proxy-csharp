using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Transport;
using TgWsProxy.Infrastructure.Instances;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Benchmarks;

/// <summary>
/// Базовые линии горячих путей. TransformUpstream (ре-шифрование MTProto-моста)
/// композиционно раскладывается на две операции <see cref="IncrementalCipher.UpdateInto"/>
/// (decrypt клиента + encrypt в Telegram) плюс <see cref="MsgSplitter.Split"/>, поэтому
/// измеряются составляющие по отдельности — это чище и повторяемее, чем гонять
/// stateful-поток, в котором счётчик AES-CTR смещается на каждом вызове.
/// </summary>

[MemoryDiagnoser]
public class CryptoBenchmarks
{
    private IncrementalCipher _cipher = null!;
    private byte[] _input = null!;
    private byte[] _output = null!;

    [Params(1024, 8192)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _cipher = new IncrementalCipher(
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
            Enumerable.Range(41, 16).Select(i => (byte)i).ToArray());
        _input = new byte[Size];
        _output = new byte[Size];
        RandomNumberGenerator.Fill(_input);
    }

    [GlobalCleanup]
    public void Cleanup() => _cipher.Dispose();

    // Size кратен 16, поэтому после каждого вызова поток возвращается на границу блока
    // и быстрый векторный путь (Vector128) остаётся активным — как в реальном трафике.
    [Benchmark]
    public void UpdateInto() => _cipher.UpdateInto(_input, _output);
}

[MemoryDiagnoser]
public class MsgSplitterBenchmarks
{
    private MsgSplitter _splitter = null!;
    private byte[] _cipher = null!;
    private byte[] _plain = null!;

    [Params(1290, 12900)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);

        // Каждый байт 0x20 = 32 слова → payload 128 байт, заголовок 1 байт → пакет 129 байт.
        // 1290 = 10 пакетов, 12900 = 100 пакетов.
        _plain = new byte[Size];
        Array.Fill(_plain, (byte)0x20);

        _cipher = new byte[Size];
        RandomNumberGenerator.Fill(_cipher);
    }

    // Split полностью разбирает фрагмент и сбрасывает состояние (Consume → Reset),
    // поэтому каждый вызов обрабатывает весь Size заново.
    [Benchmark]
    public int Split() => _splitter.Split(_cipher, _plain).Count;
}

[MemoryDiagnoser]
public class RawWebSocketBenchmarks
{
    private RawWebSocket _ws = null!;
    private byte[] _payload = null!;

    [Params(256, 4096)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _ws = new RawWebSocket(socket, Stream.Null, "bench", NullLogger.Instance, 16 * 1024 * 1024);
        _payload = new byte[Size];
        RandomNumberGenerator.Fill(_payload);
    }

    // Измеряет построение кадра + маскирование (векторный XOR) + ArrayPool + WriteAsync в Stream.Null.
    [Benchmark]
    public Task Send() => _ws.Send(_payload, CancellationToken.None);
}

[MemoryDiagnoser]
public class PeerStatsBenchmarks
{
    private PeerStatsRegistry _registry = null!;

    [Params(1000, 10000)]
    public int Peers;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new PeerStatsRegistry(enabled: true, ttlSeconds: 0, maxTrackedPeers: 100_000);

        for (var i = 0; i < Peers; i++)
        {
            var ip = new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i });
            _registry.ConnectionOpened(ip);
            _registry.AddBytesUp(ip, null, 1000);
            _registry.AddBytesDown(ip, null, 2000);
        }
    }

    // Snapshot не запускает sweep — измеряется только сбор снимка (P0: O(n) перебор +
    // однократное построение карты имён токенов вместо линейного Find на каждую пару).
    [Benchmark]
    public int Snapshot() => _registry.Snapshot().Count;
}
