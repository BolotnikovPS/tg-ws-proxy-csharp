using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.Logic.FakeTls;
using TgWsProxy.Application.Logic.Models;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты Fake TLS (порт upstream <c>fake_tls.py</c>) и чтения клиентского init
/// (<c>_read_client_init</c>): проверка ClientHello, сборка ServerHello, TLS-обёртка
/// и разбор PROXY protocol v1.
/// </summary>
public class FakeTlsTests
{
    private static readonly byte[] Secret = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static byte[] BuildSessionId(byte seed)
    {
        var sessionId = new byte[FakeTlsCodec.SessionIdLength];
        for (var i = 0; i < sessionId.Length; i++)
        {
            sessionId[i] = (byte)(seed + i);
        }

        return sessionId;
    }

    /// <summary>
    /// Собирает корректный Fake TLS ClientHello так, как его формирует клиент Telegram.
    /// </summary>
    private static byte[] BuildClientHello(byte[] secret, uint timestamp, byte[] sessionId, bool includeSessionId = true)
    {
        var total = 5 + 4 + 2 + 32 + 1 + (includeSessionId ? 32 : 0);
        var data = new byte[total];
        data[0] = FakeTlsCodec.RecordHandshake;
        data[1] = 0x03;
        data[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3, 2), (ushort)(total - 5));
        data[5] = 0x01;
        data[8] = (byte)(total - 9);
        data[9] = 0x03;
        data[10] = 0x03;
        data[43] = includeSessionId ? (byte)0x20 : (byte)0x00;
        if (includeSessionId)
        {
            sessionId.CopyTo(data, 44);
        }

        // client_random пока нулевой — именно по такой записи считается HMAC.
        var mac = HMACSHA256.HashData(secret, data);
        mac.AsSpan(0, 28).CopyTo(data.AsSpan(FakeTlsCodec.ClientRandomOffset, 28));
        for (var i = 0; i < 4; i++)
        {
            data[FakeTlsCodec.ClientRandomOffset + 28 + i] =
                (byte)(mac[28 + i] ^ (byte)((timestamp >> (8 * i)) & 0xFF));
        }

        return data;
    }

    private static uint NowSeconds() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForShortData()
    {
        var data = new byte[20];
        data[0] = FakeTlsCodec.RecordHandshake;
        data[5] = 0x01;

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, Secret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForWrongRecordType()
    {
        var data = BuildClientHello(Secret, NowSeconds(), BuildSessionId(1));
        data[0] = FakeTlsCodec.RecordApplicationData;

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, Secret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForWrongHandshakeType()
    {
        var data = BuildClientHello(Secret, NowSeconds(), BuildSessionId(1));
        data[5] = 0x02;

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, Secret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForWrongSecret()
    {
        var data = BuildClientHello(Secret, NowSeconds(), BuildSessionId(1));
        var otherSecret = Convert.FromHexString("ffeeddccbbaa99887766554433221100");

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, otherSecret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForStaleTimestamp()
    {
        var data = BuildClientHello(Secret, NowSeconds() - 1000, BuildSessionId(1));

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, Secret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsSessionIdAndTimestamp_ForValidHello()
    {
        var sessionId = BuildSessionId(7);
        var timestamp = NowSeconds();
        var data = BuildClientHello(Secret, timestamp, sessionId);

        var info = FakeTlsCodec.VerifyClientHello(data, Secret);

        Assert.NotNull(info);
        Assert.Equal(timestamp, info!.Value.Timestamp);
        Assert.Equal(sessionId, info.Value.SessionId);
        Assert.Equal(32, info.Value.ClientRandom.Length);
    }

    [Fact]
    public void VerifyClientHello_ReturnsZeroSessionId_WhenClientOmitsIt()
    {
        var data = BuildClientHello(Secret, NowSeconds(), BuildSessionId(1), includeSessionId: false);

        var info = FakeTlsCodec.VerifyClientHello(data, Secret);

        Assert.NotNull(info);
        Assert.Equal(new byte[FakeTlsCodec.SessionIdLength], info!.Value.SessionId);
    }

    [Fact]
    public void WrapTlsRecord_ReturnsEmpty_ForEmptyInput()
    {
        Assert.Empty(FakeTlsCodec.WrapTlsRecord([]));
    }

    [Fact]
    public void WrapTlsRecord_AddsHeader_ForSmallPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var wrapped = FakeTlsCodec.WrapTlsRecord(payload);

        Assert.Equal(payload.Length + 5, wrapped.Length);
        Assert.Equal(FakeTlsCodec.RecordApplicationData, wrapped[0]);
        Assert.Equal(0x03, wrapped[1]);
        Assert.Equal(0x03, wrapped[2]);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(3, 2)));
        Assert.Equal(payload, wrapped[5..]);
    }

    [Fact]
    public void WrapTlsRecord_SplitsPayloadIntoMaxSizeRecords()
    {
        var payload = new byte[FakeTlsCodec.ApplicationDataMax + 100];
        Random.Shared.NextBytes(payload);

        var wrapped = FakeTlsCodec.WrapTlsRecord(payload);

        Assert.Equal(payload.Length + 10, wrapped.Length);
        Assert.Equal(FakeTlsCodec.ApplicationDataMax, BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(3, 2)));
        Assert.Equal(
            100,
            BinaryPrimitives.ReadUInt16BigEndian(
                wrapped.AsSpan(5 + FakeTlsCodec.ApplicationDataMax + 3, 2)));
        Assert.Equal(payload.AsSpan(0, FakeTlsCodec.ApplicationDataMax).ToArray(), wrapped[5..(5 + FakeTlsCodec.ApplicationDataMax)]);
    }

    [Fact]
    public void BuildServerHello_UsesHmacOfClientRandomAndResponse()
    {
        var clientRandom = new byte[FakeTlsCodec.RandomLength];
        Random.Shared.NextBytes(clientRandom);
        var sessionId = BuildSessionId(3);

        var response = FakeTlsCodec.BuildServerHello(Secret, clientRandom, sessionId);

        // Первая запись — ServerHello с обнулённым random, затем CCS и application data.
        Assert.Equal(FakeTlsCodec.RecordHandshake, response[0]);

        // server_random считается по ответу, в котором поле random ещё нулевое (как в upstream).
        var hmacSource = (byte[])response.Clone();
        hmacSource.AsSpan(11, FakeTlsCodec.RandomLength).Clear();
        var expected = new byte[clientRandom.Length + hmacSource.Length];
        clientRandom.CopyTo(expected, 0);
        hmacSource.CopyTo(expected, clientRandom.Length);
        var mac = HMACSHA256.HashData(Secret, expected);

        Assert.Equal(
            mac,
            response.AsSpan(11, FakeTlsCodec.RandomLength).ToArray());

        // session id перенесён в ответ.
        Assert.Equal(
            sessionId,
            response.AsSpan(44, FakeTlsCodec.SessionIdLength).ToArray());

        // После ServerHello идут CCS-кадр и запись application data со «зашифрованным» блоком.
        var ccsIndex = IndexOf(response, [FakeTlsCodec.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01]);
        Assert.True(ccsIndex > 0, "CCS-кадр не найден в ответе");

        var appRecordOffset = ccsIndex + 6;
        Assert.Equal(FakeTlsCodec.RecordApplicationData, response[appRecordOffset]);
        var appRecordLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(appRecordOffset + 3, 2));
        Assert.InRange(appRecordLength, 1900, 2100);
        Assert.Equal(response.Length, appRecordOffset + 5 + appRecordLength);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task FakeTlsStream_RoundTripsPayload()
    {
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);

        var inner = new MemoryStream();
        // Поток не освобождаем: Dispose у FakeTlsStream закрывает и вложенный MemoryStream,
        // из которого ниже нужно прочитать записанные записи.
        var writer = new FakeTlsStream(inner);
        await writer.WriteAsync(payload);

        inner.Position = 0;
        await using var reader = new FakeTlsStream(inner);
        var buffer = new byte[payload.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task FakeTlsStream_SkipsChangeCipherSpecRecords()
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        var ccs = new byte[] { FakeTlsCodec.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01 };

        // CCS-запись перед данными не должна ломать чтение.
        var combined = new MemoryStream();
        await combined.WriteAsync(ccs);
        await combined.WriteAsync(FakeTlsCodec.WrapTlsRecord(payload));
        combined.Position = 0;

        await using var reader = new FakeTlsStream(combined);
        var buffer = new byte[payload.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, buffer);
    }

    private static ClientInitReader CreateReader(Config config) => new(config, NullLogger<ClientInitReader>.Instance);

    [Fact]
    public async Task ReadAsync_ReturnsPlainInit_WhenNoMaskingConfigured()
    {
        var config = new Config();
        var init = new byte[Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);
        var stream = new MemoryStream(init);

        var result = await CreateReader(config).ReadAsync(stream, "1.2.3.4:5", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal(init, result.Init);
        Assert.Equal("1.2.3.4:5", result.Label);
    }

    [Fact]
    public async Task ReadAsync_ReturnsClosed_WhenClientSendsNothing()
    {
        var config = new Config();
        var stream = new MemoryStream([]);

        var result = await CreateReader(config).ReadAsync(stream, "scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Closed, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ReturnsHttpRedirect_WhenNonTlsByteAndMaskingConfigured()
    {
        var config = new Config { FakeTlsDomain = "example.com" };
        var stream = new MemoryStream([0x05, 0x01, 0x02, 0x03]);

        var result = await CreateReader(config).ReadAsync(stream, "scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.HttpRedirect, result.Status);
        Assert.Equal(new byte[] { 0x05 }, result.FirstBytes);
    }

    [Fact]
    public async Task ReadAsync_ReturnsMasking_WhenFakeTlsVerifyFails()
    {
        var config = new Config { FakeTlsDomain = "example.com" };
        var clientHello = BuildClientHello(Secret, NowSeconds(), BuildSessionId(2));
        // Ломаем подпись — проверка должна отправить трафик на домен маскировки.
        clientHello[20] ^= 0xFF;
        var stream = new MemoryStream(clientHello);

        var result = await CreateReader(config).ReadAsync(stream, "scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Masking, result.Status);
        Assert.Equal(clientHello, result.FirstBytes);
    }

    [Fact]
    public async Task ReadAsync_ReadsObfsInit_InsideFakeTls()
    {
        var config = new Config { FakeTlsDomain = "example.com" };
        var init = new byte[Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var incoming = new MemoryStream();
        await incoming.WriteAsync(BuildClientHello(Secret, NowSeconds(), BuildSessionId(4)));
        await incoming.WriteAsync(FakeTlsCodec.WrapTlsRecord(init));

        // Дуплексный поток: ответный ServerHello не должен перезаписывать уже отправленные клиентом данные.
        var stream = new DuplexStream(incoming.ToArray());

        var result = await CreateReader(config).ReadAsync(stream, "scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal(init, result.Init);
        Assert.IsType<FakeTlsStream>(result.ClientStream);
        // Сервер ответил ServerHello (первая запись — handshake).
        Assert.Equal(FakeTlsCodec.RecordHandshake, stream.Written[0]);
    }

    [Fact]
    public async Task ReadAsync_UsesProxyProtocolPeerAsLabel()
    {
        var config = new Config { ProxyProtocol = true };
        var init = new byte[Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var stream = new MemoryStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.7 198.51.100.9 51234 1443\r\n"));
        await stream.WriteAsync(init);
        stream.Position = 0;

        var result = await CreateReader(config).ReadAsync(stream, "raw-scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal("203.0.113.7:51234", result.Label);
        Assert.Equal(init, result.Init);
    }

    [Fact]
    public async Task ReadAsync_KeepsScope_WhenProxyHeaderIsNotProxy()
    {
        var config = new Config { ProxyProtocol = true };
        var init = new byte[Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var stream = new MemoryStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GARBAGE HEADER LINE\r\n"));
        await stream.WriteAsync(init);
        stream.Position = 0;

        var result = await CreateReader(config).ReadAsync(stream, "raw-scope", Secret, CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal("raw-scope", result.Label);
    }

    /// <summary>
    /// Поток для тестов: чтение идёт из заранее заданных байт, запись — в отдельный буфер,
    /// чтобы ответы сервера не перезаписывали входящие данные.
    /// </summary>
    private sealed class DuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream readSide = new(input);
        private readonly MemoryStream writeSide = new();

        public byte[] Written => writeSide.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => readSide.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => readSide.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => writeSide.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => writeSide.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
