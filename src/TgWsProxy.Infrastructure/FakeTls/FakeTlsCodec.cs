using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TgWsProxy.Infrastructure.FakeTls;

/// <summary>
/// Проверка и генерация Fake TLS рукопожатия (порт upstream <c>fake_tls.py</c>).
/// Маскировка позволяет клиенту выглядеть как обычный TLS-трафик к заданному домену.
/// </summary>
internal static class FakeTlsCodec
{
    /// <summary>Тип записи TLS «handshake» (TLS_RECORD_HANDSHAKE).</summary>
    internal const byte RecordHandshake = 0x16;

    /// <summary>Тип записи TLS «change cipher spec» (TLS_RECORD_CCS).</summary>
    internal const byte RecordChangeCipherSpec = 0x14;

    /// <summary>Тип записи TLS «application data» (TLS_RECORD_APPDATA).</summary>
    internal const byte RecordApplicationData = 0x17;

    /// <summary>Смещение client_random в ClientHello (CLIENT_RANDOM_OFFSET).</summary>
    internal const int ClientRandomOffset = 11;

    /// <summary>Длина client_random / server_random.</summary>
    internal const int RandomLength = 32;

    /// <summary>Смещение session id в ClientHello (SESSION_ID_OFFSET).</summary>
    internal const int SessionIdOffset = 44;

    /// <summary>Длина session id.</summary>
    internal const int SessionIdLength = 32;

    /// <summary>Допустимое отклонение метки времени, секунды (TIMESTAMP_TOLERANCE).</summary>
    internal const int TimestampToleranceSeconds = 120;

    /// <summary>Максимальный размер полезной нагрузки одной TLS-записи (TLS_APPDATA_MAX).</summary>
    internal const int ApplicationDataMax = 16384;

    /// <summary>Минимальная длина ClientHello для проверки: 5 + 6 + 32.</summary>
    private const int MinClientHelloLength = 43;

    /// <summary>Готовая заготовка ServerHello с плейсхолдерами random/session id/pubkey.</summary>
    private static readonly byte[] ServerHelloTemplate =
    [
        0x16, 0x03, 0x03, 0x00, 0x7a,
        0x02, 0x00, 0x00, 0x76,
        0x03, 0x03,
        .. new byte[32],
        0x20,
        .. new byte[32],
        0x13, 0x01, 0x00,
        0x00, 0x2e,
        0x00, 0x33, 0x00, 0x24, 0x00, 0x1d, 0x00, 0x20,
        .. new byte[32],
        0x00, 0x2b, 0x00, 0x02, 0x03, 0x04,
    ];

    /// <summary>Смещение server_random в заготовке ServerHello (_SH_RANDOM_OFF).</summary>
    private const int ServerHelloRandomOffset = 11;

    /// <summary>Смещение session id в заготовке ServerHello (_SH_SESSID_OFF).</summary>
    private const int ServerHelloSessionIdOffset = 44;

    /// <summary>Смещение публичного ключа в заготовке ServerHello (_SH_PUBKEY_OFF).</summary>
    private const int ServerHelloPublicKeyOffset = 89;

    /// <summary>Кадр change_cipher_spec, который upstream отправляет сразу после ServerHello.</summary>
    private static readonly byte[] ChangeCipherSpecFrame = [0x14, 0x03, 0x03, 0x00, 0x01, 0x01];

    /// <summary>
    /// Результат успешной проверки Fake TLS ClientHello.
    /// </summary>
    /// <param name="ClientRandom">32 байта client_random.</param>
    /// <param name="SessionId">32 байта session id (нули, если клиент его не передал).</param>
    /// <param name="Timestamp">Метка времени, извлечённая из client_random.</param>
    internal readonly record struct ClientHelloInfo(byte[] ClientRandom, byte[] SessionId, uint Timestamp);

    /// <summary>
    /// Проверяет Fake TLS ClientHello: HMAC-подпись, метку времени и session id.
    /// </summary>
    /// <param name="data">Полная TLS-запись с ClientHello.</param>
    /// <param name="secret">Секрет прокси в байтах.</param>
    /// <returns>Разобранные параметры либо <see langword="null"/>, если проверка не прошла.</returns>
    internal static ClientHelloInfo? VerifyClientHello(ReadOnlySpan<byte> data, byte[] secret)
    {
        if (data.Length < MinClientHelloLength)
        {
            return null;
        }

        if (data[0] != RecordHandshake || data[5] != 0x01)
        {
            return null;
        }

        var clientRandom = data.Slice(ClientRandomOffset, RandomLength).ToArray();

        // Подпись считается по ClientHello с обнулённым client_random.
        var zeroed = data.ToArray();
        zeroed.AsSpan(ClientRandomOffset, RandomLength).Clear();

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(secret, zeroed, expected);

        if (!CryptographicOperations.FixedTimeEquals(expected[..28], clientRandom.AsSpan(0, 28)))
        {
            return null;
        }

        Span<byte> timestampBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            timestampBytes[i] = (byte)(clientRandom[28 + i] ^ expected[28 + i]);
        }

        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(timestampBytes);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = now > timestamp ? now - timestamp : timestamp - now;
        if (skew > TimestampToleranceSeconds)
        {
            return null;
        }

        var sessionId = new byte[SessionIdLength];
        if (data.Length >= SessionIdOffset + SessionIdLength && data[43] == 0x20)
        {
            data.Slice(SessionIdOffset, SessionIdLength).CopyTo(sessionId);
        }

        return new ClientHelloInfo(clientRandom, sessionId, timestamp);
    }

    /// <summary>
    /// Собирает ответный ServerHello (с CCS и «зашифрованной» записью) для успешной проверки.
    /// </summary>
    /// <param name="secret">Секрет прокси в байтах.</param>
    /// <param name="clientRandom">client_random из ClientHello.</param>
    /// <param name="sessionId">session id из ClientHello.</param>
    /// <returns>Готовый набор TLS-записей для отправки клиенту.</returns>
    internal static byte[] BuildServerHello(byte[] secret, byte[] clientRandom, byte[] sessionId)
    {
        var serverHello = (byte[])ServerHelloTemplate.Clone();
        sessionId.AsSpan(0, SessionIdLength).CopyTo(serverHello.AsSpan(ServerHelloSessionIdOffset, SessionIdLength));
        RandomNumberGenerator.Fill(serverHello.AsSpan(ServerHelloPublicKeyOffset, RandomLength));

        var encryptedSize = Random.Shared.Next(1900, 2101);
        var appRecord = new byte[5 + encryptedSize];
        appRecord[0] = RecordApplicationData;
        appRecord[1] = 0x03;
        appRecord[2] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(appRecord.AsSpan(3, 2), (ushort)encryptedSize);
        RandomNumberGenerator.Fill(appRecord.AsSpan(5));

        var response = new byte[serverHello.Length + ChangeCipherSpecFrame.Length + appRecord.Length];
        var offset = 0;
        serverHello.CopyTo(response, offset);
        offset += serverHello.Length;
        ChangeCipherSpecFrame.CopyTo(response, offset);
        offset += ChangeCipherSpecFrame.Length;
        appRecord.CopyTo(response, offset);

        // server_random = HMAC-SHA256(secret, client_random + response)
        var hmacInput = new byte[clientRandom.Length + response.Length];
        clientRandom.CopyTo(hmacInput, 0);
        response.CopyTo(hmacInput, clientRandom.Length);

        Span<byte> serverRandom = stackalloc byte[32];
        HMACSHA256.HashData(secret, hmacInput, serverRandom);
        serverRandom.CopyTo(response.AsSpan(ServerHelloRandomOffset, RandomLength));

        return response;
    }

    /// <summary>
    /// Оборачивает данные в одну или несколько TLS-записей application data (WRAP_TLS_RECORD).
    /// </summary>
    /// <param name="data">Полезная нагрузка.</param>
    /// <returns>Последовательность TLS-записей.</returns>
    internal static byte[] WrapTlsRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        var records = (data.Length + ApplicationDataMax - 1) / ApplicationDataMax;
        var result = new byte[data.Length + (records * 5)];
        var srcOffset = 0;
        var dstOffset = 0;

        while (srcOffset < data.Length)
        {
            var chunkLen = Math.Min(ApplicationDataMax, data.Length - srcOffset);
            result[dstOffset] = RecordApplicationData;
            result[dstOffset + 1] = 0x03;
            result[dstOffset + 2] = 0x03;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(dstOffset + 3, 2), (ushort)chunkLen);
            data.Slice(srcOffset, chunkLen).CopyTo(result.AsSpan(dstOffset + 5));
            srcOffset += chunkLen;
            dstOffset += 5 + chunkLen;
        }

        return result;
    }
}
