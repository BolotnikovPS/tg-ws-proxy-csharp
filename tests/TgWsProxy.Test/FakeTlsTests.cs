using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.FakeTls;
using TgWsProxy.Infrastructure.MtProto;
using TgWsProxy.Infrastructure.Transport;

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
        var sessionId = new byte[AppConstants.FakeTls.SessionIdLength];
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
        data[0] = AppConstants.FakeTls.RecordHandshake;
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
        mac.AsSpan(0, 28).CopyTo(data.AsSpan(AppConstants.FakeTls.ClientRandomOffset, 28));
        for (var i = 0; i < 4; i++)
        {
            data[AppConstants.FakeTls.ClientRandomOffset + 28 + i] =
                (byte)(mac[28 + i] ^ (byte)((timestamp >> (8 * i)) & 0xFF));
        }

        return data;
    }

    private static uint NowSeconds() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForShortData()
    {
        var data = new byte[20];
        data[0] = AppConstants.FakeTls.RecordHandshake;
        data[5] = 0x01;

        Assert.Null(FakeTlsCodec.VerifyClientHello(data, Secret));
    }

    [Fact]
    public void VerifyClientHello_ReturnsNull_ForWrongRecordType()
    {
        var data = BuildClientHello(Secret, NowSeconds(), BuildSessionId(1));
        data[0] = AppConstants.FakeTls.RecordApplicationData;

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
        Assert.Equal(new byte[AppConstants.FakeTls.SessionIdLength], info!.Value.SessionId);
    }

    [Fact]
    public void WrapTlsRecord_ReturnsEmpty_ForEmptyInput() => Assert.Empty(FakeTlsCodec.WrapTlsRecord([]));

    [Fact]
    public void WrapTlsRecord_AddsHeader_ForSmallPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var wrapped = FakeTlsCodec.WrapTlsRecord(payload);

        Assert.Equal(payload.Length + 5, wrapped.Length);
        Assert.Equal(AppConstants.FakeTls.RecordApplicationData, wrapped[0]);
        Assert.Equal(0x03, wrapped[1]);
        Assert.Equal(0x03, wrapped[2]);
        Assert.Equal(payload.Length, BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(3, 2)));
        Assert.Equal(payload, wrapped[5..]);
    }

    [Fact]
    public void WrapTlsRecord_SplitsPayloadIntoMaxSizeRecords()
    {
        var payload = new byte[AppConstants.FakeTls.ApplicationDataMax + 100];
        Random.Shared.NextBytes(payload);

        var wrapped = FakeTlsCodec.WrapTlsRecord(payload);

        Assert.Equal(payload.Length + 10, wrapped.Length);
        Assert.Equal(AppConstants.FakeTls.ApplicationDataMax, BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(3, 2)));
        Assert.Equal(
            100,
            BinaryPrimitives.ReadUInt16BigEndian(
                wrapped.AsSpan(5 + AppConstants.FakeTls.ApplicationDataMax + 3, 2)));
        Assert.Equal(payload.AsSpan(0, AppConstants.FakeTls.ApplicationDataMax).ToArray(), wrapped[5..(5 + AppConstants.FakeTls.ApplicationDataMax)]);
    }

    [Fact]
    public void BuildServerHello_UsesHmacOfClientRandomAndResponse()
    {
        var clientRandom = new byte[AppConstants.FakeTls.RandomLength];
        Random.Shared.NextBytes(clientRandom);
        var sessionId = BuildSessionId(3);

        var response = FakeTlsCodec.BuildServerHello(Secret, clientRandom, sessionId);

        // Первая запись — ServerHello с обнулённым random, затем CCS и application data.
        Assert.Equal(AppConstants.FakeTls.RecordHandshake, response[0]);

        // server_random считается по ответу, в котором поле random ещё нулевое (как в upstream).
        var hmacSource = (byte[])response.Clone();
        hmacSource.AsSpan(11, AppConstants.FakeTls.RandomLength).Clear();
        var expected = new byte[clientRandom.Length + hmacSource.Length];
        clientRandom.CopyTo(expected, 0);
        hmacSource.CopyTo(expected, clientRandom.Length);
        var mac = HMACSHA256.HashData(Secret, expected);

        Assert.Equal(
            mac,
            response.AsSpan(11, AppConstants.FakeTls.RandomLength).ToArray());

        // session id перенесён в ответ.
        Assert.Equal(
            sessionId,
            response.AsSpan(44, AppConstants.FakeTls.SessionIdLength).ToArray());

        // После ServerHello идут CCS-кадр и запись application data со «зашифрованным» блоком.
        var ccsIndex = IndexOf(response, [AppConstants.FakeTls.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01]);
        Assert.True(ccsIndex > 0, "CCS-кадр не найден в ответе");

        var appRecordOffset = ccsIndex + 6;
        Assert.Equal(AppConstants.FakeTls.RecordApplicationData, response[appRecordOffset]);
        var appRecordLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(appRecordOffset + 3, 2));
        // Без профиля сайта маскировки размер берётся из узкого диапазона, а не 1900-2100:
        // прежний «толстый» ответ и был признаком MTProxy для DPI (issue #1473).
        Assert.InRange(
            appRecordLength,
            AppConstants.FakeTls.FallbackAppDataMinLength,
            AppConstants.FakeTls.FallbackAppDataMaxLength);
        Assert.Equal(response.Length, appRecordOffset + 5 + appRecordLength);
    }

    /// <summary>
    /// Собирает ClientHello с cipher suites и расширением key_share — то, что нужно
    /// разбору параметров клиента (upstream <c>parse_client_hello</c>).
    /// </summary>
    private static byte[] BuildParsableClientHello(
        ushort[] cipherSuites,
        (ushort Group, int KeyLength)[] keyShares)
    {
        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(new byte[AppConstants.FakeTls.RandomLength]);
        body.Add(0x00);

        AddUInt16BigEndian(body, cipherSuites.Length * 2);
        foreach (var suite in cipherSuites)
        {
            AddUInt16BigEndian(body, suite);
        }

        body.Add(0x01);
        body.Add(0x00);

        var entries = new List<byte>();
        foreach (var (group, keyLength) in keyShares)
        {
            AddUInt16BigEndian(entries, group);
            AddUInt16BigEndian(entries, keyLength);
            entries.AddRange(new byte[keyLength]);
        }

        var keyShareExtension = new List<byte>();
        AddUInt16BigEndian(keyShareExtension, entries.Count);
        keyShareExtension.AddRange(entries);

        var extensions = new List<byte>();
        AddUInt16BigEndian(extensions, AppConstants.FakeTls.ExtensionKeyShare);
        AddUInt16BigEndian(extensions, keyShareExtension.Count);
        extensions.AddRange(keyShareExtension);

        AddUInt16BigEndian(body, extensions.Count);
        body.AddRange(extensions);

        var record = new List<byte> { AppConstants.FakeTls.RecordHandshake, 0x03, 0x03 };
        AddUInt16BigEndian(record, 4 + body.Count);
        record.Add(0x01);
        record.Add((byte)(body.Count >> 16));
        record.Add((byte)(body.Count >> 8));
        record.Add((byte)body.Count);
        record.AddRange(body);
        return [.. record];
    }

    private static void AddUInt16BigEndian(List<byte> target, int value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }

    /// <summary>Достаёт из ответа сервера выбранный шифр, группу и длину key share.</summary>
    private static (ushort Cipher, ushort Group, int KeyShareLength) ParseServerHello(byte[] response)
    {
        var pos = 5 + 4 + 2 + AppConstants.FakeTls.RandomLength;
        pos += 1 + response[pos];
        var cipher = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos, 2));
        pos += 3;
        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos, 2));
        pos += 2;

        var end = Math.Min(response.Length, pos + extensionsLength);
        while (pos + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos + 2, 2));
            pos += 4;
            if (type == AppConstants.FakeTls.ExtensionKeyShare)
            {
                var group = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos, 2));
                var keyLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(pos + 2, 2));
                return (cipher, group, keyLength);
            }

            pos += length;
        }

        return (cipher, 0, 0);
    }

    [Fact]
    public void ParseClientHello_ReturnsEmpty_ForHelloWithoutExtensions()
    {
        // Синтетический ClientHello без cipher suites и расширений: разбор не должен падать.
        var offers = FakeTlsCodec.ParseClientHello(BuildClientHello(Secret, NowSeconds(), BuildSessionId(1)));

        Assert.Empty(offers.CipherSuites);
        Assert.Empty(offers.KeyShares);
    }

    [Fact]
    public void ParseClientHello_ExtractsCipherSuitesAndKeyShares()
    {
        var hello = BuildParsableClientHello(
            [0x1303, 0x1301],
            [
                (AppConstants.FakeTls.GroupX25519MlKem768, AppConstants.FakeTls.X25519MlKem768ClientShareLength),
                (AppConstants.FakeTls.GroupX25519, AppConstants.FakeTls.X25519KeyLength),
            ]);

        var offers = FakeTlsCodec.ParseClientHello(hello);

        Assert.Equal(2, offers.CipherSuites.Count);
        Assert.Contains((ushort)0x1303, offers.CipherSuites);
        Assert.Contains((ushort)0x1301, offers.CipherSuites);
        Assert.Equal(
            AppConstants.FakeTls.X25519MlKem768ClientShareLength,
            offers.KeyShares[AppConstants.FakeTls.GroupX25519MlKem768]);
        Assert.Equal(
            AppConstants.FakeTls.X25519KeyLength,
            offers.KeyShares[AppConstants.FakeTls.GroupX25519]);
    }

    [Fact]
    public void BuildServerHello_MirrorsHybridKeyShare_WhenClientOffersIt()
    {
        var offers = FakeTlsCodec.ParseClientHello(BuildParsableClientHello(
            [0x1301],
            [(AppConstants.FakeTls.GroupX25519MlKem768, AppConstants.FakeTls.X25519MlKem768ClientShareLength)]));

        var response = FakeTlsCodec.BuildServerHello(Secret, new byte[AppConstants.FakeTls.RandomLength], BuildSessionId(5), offers);
        var (_, group, keyShareLength) = ParseServerHello(response);

        Assert.Equal(AppConstants.FakeTls.GroupX25519MlKem768, group);
        Assert.Equal(
            AppConstants.FakeTls.MlKem768CiphertextLength + AppConstants.FakeTls.X25519KeyLength,
            keyShareLength);
    }

    [Fact]
    public void BuildServerHello_UsesX25519_WhenClientDoesNotOfferHybrid()
    {
        var offers = FakeTlsCodec.ParseClientHello(BuildParsableClientHello(
            [0x1301],
            [(AppConstants.FakeTls.GroupX25519, AppConstants.FakeTls.X25519KeyLength)]));

        var response = FakeTlsCodec.BuildServerHello(Secret, new byte[AppConstants.FakeTls.RandomLength], BuildSessionId(6), offers);
        var (_, group, keyShareLength) = ParseServerHello(response);

        Assert.Equal(AppConstants.FakeTls.GroupX25519, group);
        Assert.Equal(AppConstants.FakeTls.X25519KeyLength, keyShareLength);
    }

    [Fact]
    public void BuildServerHello_SelectsCipherFromClientList()
    {
        // Клиент не поддерживает предпочтительный 0x1301 — берём следующий из его списка.
        var offers = FakeTlsCodec.ParseClientHello(BuildParsableClientHello([0x1302], []));

        var response = FakeTlsCodec.BuildServerHello(Secret, new byte[AppConstants.FakeTls.RandomLength], BuildSessionId(7), offers);

        Assert.Equal((ushort)0x1302, ParseServerHello(response).Cipher);
    }

    [Fact]
    public void BuildServerHello_UsesProfiledRecordSize()
    {
        const int profiled = 512;
        try
        {
            FakeTlsCodec.SetProfiledAppDataLength(profiled);
            var response = FakeTlsCodec.BuildServerHello(Secret, new byte[AppConstants.FakeTls.RandomLength], BuildSessionId(8));

            var ccsIndex = IndexOf(response, [AppConstants.FakeTls.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01]);
            var appRecordOffset = ccsIndex + 6;

            Assert.Equal(profiled, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(appRecordOffset + 3, 2)));
        }
        finally
        {
            FakeTlsCodec.SetProfiledAppDataLength(null);
        }
    }

    [Fact]
    public void SetProfiledAppDataLength_ClampsToTlsRecordBounds()
    {
        try
        {
            FakeTlsCodec.SetProfiledAppDataLength(10);
            var response = FakeTlsCodec.BuildServerHello(Secret, new byte[AppConstants.FakeTls.RandomLength], BuildSessionId(9));
            var ccsIndex = IndexOf(response, [AppConstants.FakeTls.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01]);

            Assert.Equal(
                AppConstants.FakeTls.AppDataMinLength,
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(ccsIndex + 9, 2)));
        }
        finally
        {
            FakeTlsCodec.SetProfiledAppDataLength(null);
        }
    }

    [Fact]
    public void FirstAppDataRecordLength_ReturnsNull_ForIncompleteHeader()
    {
        Assert.Null(FakeTlsCodec.FirstAppDataRecordLength([0x16, 0x03, 0x03]));
    }

    [Fact]
    public void FirstAppDataRecordLength_ReturnsNull_ForIncompleteBody()
    {
        // Заголовок обещает 8 байт, а пришло меньше: данных пока не хватает.
        byte[] partial = [0x16, 0x03, 0x03, 0x00, 0x08, 0x01, 0x02];

        Assert.Null(FakeTlsCodec.FirstAppDataRecordLength(partial));
    }

    [Fact]
    public void FirstAppDataRecordLength_SkipsHandshakeAndCcsRecords()
    {
        var data = new List<byte>();
        data.AddRange([AppConstants.FakeTls.RecordHandshake, 0x03, 0x03, 0x00, 0x02, 0xAA, 0xBB]);
        data.AddRange([AppConstants.FakeTls.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01]);
        data.AddRange([AppConstants.FakeTls.RecordApplicationData, 0x03, 0x03, 0x00, 0x03, 0xCC, 0xDD, 0xEE]);

        Assert.Equal(3, FakeTlsCodec.FirstAppDataRecordLength([.. data]));
    }

    [Fact]
    public void FirstAppDataRecordLength_ReturnsZero_ForUnexpectedRecordType()
    {
        // Alert вместо ожидаемых записей: профиль снять невозможно.
        byte[] alert = [0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28];

        Assert.Equal(0, FakeTlsCodec.FirstAppDataRecordLength(alert));
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
        await writer.WriteAsync(payload, CancellationToken.None);

        inner.Position = 0;
        await using var reader = new FakeTlsStream(inner);
        var buffer = new byte[payload.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total), CancellationToken.None);
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
        var ccs = new byte[] { AppConstants.FakeTls.RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01 };

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
            var read = await reader.ReadAsync(buffer.AsMemory(total), CancellationToken.None);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, buffer);
    }

    /// <summary>
    /// Создаёт читатель init с одним тестовым токеном: с редакции 3 Fake TLS проверяет
    /// ClientHello по секретам списка токенов, а не по единственному секрету (F-8.4).
    /// </summary>
    private static ClientInitReader CreateReader(Config config)
    {
        config.Tokens.Add(new ProxyToken
        {
            Id = "test",
            Name = "test",
            Secret = Convert.ToHexString(Secret).ToLowerInvariant(),
            Enabled = true,
        });

        return new(config, new ProxyTokenRegistry(config), NullLogger<ClientInitReader>.Instance);
    }

    [Fact]
    public async Task ReadAsync_ReturnsPlainInit_WhenNoMaskingConfigured()
    {
        var config = new Config();
        var init = new byte[AppConstants.Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);
        var stream = new MemoryStream(init);

        var result = await CreateReader(config).ReadAsync(stream, "1.2.3.4:5", CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal(init, result.Init);
        Assert.Equal("1.2.3.4:5", result.Label);
    }

    [Fact]
    public async Task ReadAsync_ReturnsClosed_WhenClientSendsNothing()
    {
        var config = new Config();
        var stream = new MemoryStream([]);

        var result = await CreateReader(config).ReadAsync(stream, "scope", CancellationToken.None);

        Assert.Equal(ClientInitStatus.Closed, result.Status);
    }

    [Fact]
    public async Task ReadAsync_ReturnsHttpRedirect_WhenNonTlsByteAndMaskingConfigured()
    {
        var config = new Config { FakeTlsDomain = "example.com" };
        var stream = new MemoryStream([0x05, 0x01, 0x02, 0x03]);

        var result = await CreateReader(config).ReadAsync(stream, "scope", CancellationToken.None);

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

        var result = await CreateReader(config).ReadAsync(stream, "scope", CancellationToken.None);

        Assert.Equal(ClientInitStatus.Masking, result.Status);
        Assert.Equal(clientHello, result.FirstBytes);
    }

    [Fact]
    public async Task ReadAsync_ReadsObfsInit_InsideFakeTls()
    {
        var config = new Config { FakeTlsDomain = "example.com" };
        var init = new byte[AppConstants.Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var incoming = new MemoryStream();
        await incoming.WriteAsync(BuildClientHello(Secret, NowSeconds(), BuildSessionId(4)));
        await incoming.WriteAsync(FakeTlsCodec.WrapTlsRecord(init));

        // Дуплексный поток: ответный ServerHello не должен перезаписывать уже отправленные клиентом данные.
        var stream = new DuplexStream(incoming.ToArray());

        var result = await CreateReader(config).ReadAsync(stream, "scope", CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal(init, result.Init);
        Assert.IsType<FakeTlsStream>(result.ClientStream);
        // Сервер ответил ServerHello (первая запись — handshake).
        Assert.Equal(AppConstants.FakeTls.RecordHandshake, stream.Written[0]);
    }

    [Fact]
    public async Task ReadAsync_UsesProxyProtocolPeerAsLabel()
    {
        var config = new Config { ProxyProtocol = true };
        var init = new byte[AppConstants.Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var stream = new MemoryStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.7 198.51.100.9 51234 1443\r\n"));
        await stream.WriteAsync(init);
        stream.Position = 0;

        var result = await CreateReader(config).ReadAsync(stream, "raw-scope", CancellationToken.None);

        Assert.Equal(ClientInitStatus.Ok, result.Status);
        Assert.Equal("203.0.113.7:51234", result.Label);
        Assert.Equal(init, result.Init);
    }

    [Fact]
    public async Task ReadAsync_KeepsScope_WhenProxyHeaderIsNotProxy()
    {
        var config = new Config { ProxyProtocol = true };
        var init = new byte[AppConstants.Protocol.HandshakeLen];
        Random.Shared.NextBytes(init);

        var stream = new MemoryStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GARBAGE HEADER LINE\r\n"));
        await stream.WriteAsync(init);
        stream.Position = 0;

        var result = await CreateReader(config).ReadAsync(stream, "raw-scope", CancellationToken.None);

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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
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
            CancellationToken cancellationToken)
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
