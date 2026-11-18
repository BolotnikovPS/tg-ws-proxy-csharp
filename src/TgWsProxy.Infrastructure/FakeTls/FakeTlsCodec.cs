using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using static TgWsProxy.Domain.AppConstants.FakeTls;

namespace TgWsProxy.Infrastructure.FakeTls;

/// <summary>
/// Кодек Fake TLS (порт upstream <c>fake_tls.py</c>): проверка ClientHello по секрету,
/// сборка «настоящего» ServerHello под параметры клиента и обёртка/разбор TLS-записей.
/// </summary>
internal static class FakeTlsCodec
{
    /// <summary>Кадр ChangeCipherSpec: 14 03 03 00 01 01.</summary>
    private static readonly byte[] ChangeCipherSpecFrame =
    [
        RecordChangeCipherSpec, 0x03, 0x03, 0x00, 0x01, 0x01,
    ];

    /// <summary>
    /// Порядок предпочтения шифров: TLS_AES_128_GCM_SHA256, TLS_CHACHA20_POLY1305_SHA256,
    /// TLS_AES_256_GCM_SHA384 (upstream <c>CIPHER_PREFERENCE</c>).
    /// </summary>
    private static readonly ushort[] CipherPreference = [0x1301, 0x1303, 0x1302];

    /// <summary>Версия TLS 1.3 в поле supported_versions (0x0304).</summary>
    private const ushort Tls13Version = 0x0304;

    /// <summary>
    /// Размер единственной записи ApplicationData, снятый с сайта маскировки при
    /// старте (0 — профиля нет). Читается из потока соединений, поэтому через <see cref="Volatile"/>.
    /// </summary>
    private static int profiledAppDataLength;

    /// <summary>
    /// Задаёт размер записи ApplicationData, измеренный у сайта маскировки; <see langword="null"/>
    /// сбрасывает профиль и возвращает случайный размер из диапазона по умолчанию.
    /// </summary>
    public static void SetProfiledAppDataLength(int? length)
        => Volatile.Write(
            ref profiledAppDataLength,
            length is null ? 0 : Math.Clamp(length.Value, AppDataMinLength, ApplicationDataMax));

    /// <summary>
    /// Параметры ClientHello, которые сервер обязан зеркалировать: набор шифров и
    /// размеры клиентских key share по именованным группам.
    /// </summary>
    /// <remarks>
    /// DPI отбраковывал прокси по несовпадению с клиентом: ответ всегда был X25519 и
    /// одинаковой формы (issue #1473).
    /// </remarks>
    public sealed record ClientHelloOffers(
        IReadOnlySet<ushort> CipherSuites,
        IReadOnlyDictionary<ushort, int> KeyShares)
    {
        /// <summary>Пустой набор — клиент не прислал разбираемых расширений.</summary>
        public static readonly ClientHelloOffers Empty = new(
            new HashSet<ushort>(),
            new Dictionary<ushort, int>());
    }

    /// <summary>Данные ClientHello, извлечённые из подписанной записи.</summary>
    public readonly record struct ClientHelloInfo(byte[] ClientRandom, byte[] SessionId, uint Timestamp);

    /// <summary>
    /// Проверяет ClientHello по секрету токена и извлекает client_random, session id и метку времени.
    /// </summary>
    /// <param name="data">Полная TLS-запись с handshake-сообщением ClientHello.</param>
    /// <param name="secret">Секрет токена (16 байт).</param>
    /// <returns>Данные ClientHello либо <see langword="null"/>, если подпись не сходится.</returns>
    public static ClientHelloInfo? VerifyClientHello(ReadOnlySpan<byte> data, byte[] secret)
    {
        if (data.Length < MinClientHelloLength
            || data[0] != RecordHandshake
            || data[5] != 0x01)
        {
            return null;
        }

        // HMAC считается по записи с обнулённым client_random: в это поле клиент кладёт
        // первые 28 байт подписи и метку времени в последних четырёх.
        var record = data.ToArray();
        record.AsSpan(ClientRandomOffset, RandomLength).Clear();
        var mac = HMACSHA256.HashData(secret, record);
        for (var i = 0; i < 28; i++)
        {
            if (data[ClientRandomOffset + i] != mac[i])
            {
                return null;
            }
        }

        var clientRandom = data.Slice(ClientRandomOffset, RandomLength).ToArray();
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(mac.AsSpan(28, 4))
            ^ BinaryPrimitives.ReadUInt32LittleEndian(clientRandom.AsSpan(28, 4));
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = now > timestamp ? now - timestamp : timestamp - now;
        if (skew > TimestampToleranceSeconds)
        {
            return null;
        }

        var sessionId = data[SessionIdOffset - 1] == SessionIdLength
            ? data.Slice(SessionIdOffset, SessionIdLength).ToArray()
            : new byte[SessionIdLength];

        return new ClientHelloInfo(clientRandom, sessionId, timestamp);
    }

    /// <summary>
    /// Разбирает cipher suites и key share из ClientHello (upstream <c>parse_client_hello</c>).
    /// Любая ошибка разбора означает «ничего не известно» — наружу отдаётся пустой набор.
    /// </summary>
    public static ClientHelloOffers ParseClientHello(ReadOnlySpan<byte> data)
    {
        try
        {
            // 5 — заголовок записи, 4 — заголовок handshake, 2 — legacy_version, 32 — random.
            var pos = 5 + 4 + 2 + RandomLength;
            if (pos >= data.Length)
            {
                return ClientHelloOffers.Empty;
            }

            // session id: длина + тело.
            pos += 1 + data[pos];
            if (pos + 2 > data.Length)
            {
                return ClientHelloOffers.Empty;
            }

            var suitesLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            pos += 2;
            if (pos + suitesLength > data.Length)
            {
                return ClientHelloOffers.Empty;
            }

            var suites = new HashSet<ushort>(suitesLength / 2 + 1);
            for (var i = 0; i + 1 < suitesLength; i += 2)
            {
                suites.Add(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + i, 2)));
            }

            pos += suitesLength;
            if (pos >= data.Length)
            {
                return new ClientHelloOffers(suites, ClientHelloOffers.Empty.KeyShares);
            }

            // compression methods: длина + тело.
            pos += 1 + data[pos];
            if (pos + 2 > data.Length)
            {
                return new ClientHelloOffers(suites, ClientHelloOffers.Empty.KeyShares);
            }

            var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            pos += 2;
            var end = Math.Min(data.Length, pos + extensionsLength);
            var shares = new Dictionary<ushort, int>();

            while (pos + 4 <= end)
            {
                var extensionType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
                pos += 4;
                if (pos + extensionLength > end)
                {
                    break;
                }

                if (extensionType == ExtensionKeyShare)
                {
                    ParseKeyShares(data.Slice(pos, extensionLength), shares);
                }

                pos += extensionLength;
            }

            return new ClientHelloOffers(suites, shares);
        }
        catch (Exception)
        {
            return ClientHelloOffers.Empty;
        }
    }

    /// <summary>Разбирает тело расширения key_share: группу и длину клиентского share.</summary>
    private static void ParseKeyShares(ReadOnlySpan<byte> data, Dictionary<ushort, int> shares)
    {
        if (data.Length < 2)
        {
            return;
        }

        var listLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
        var end = Math.Min(data.Length, 2 + listLength);
        var pos = 2;

        while (pos + 4 <= end)
        {
            var group = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            var keyLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
            pos += 4;
            if (pos + keyLength > end)
            {
                break;
            }

            shares[group] = keyLength;
            pos += keyLength;
        }
    }

    /// <summary>Выбирает шифр из предпочтений, которые поддерживает клиент.</summary>
    private static ushort SelectCipherSuite(ClientHelloOffers offers)
    {
        foreach (var candidate in CipherPreference)
        {
            if (offers.CipherSuites.Contains(candidate))
            {
                return candidate;
            }
        }

        return CipherPreference[0];
    }

    /// <summary>
    /// Генерирует публичный ключ X25519 для key_share сервера.
    /// </summary>
    /// <remarks>
    /// BCL не предоставляет X25519 (в отличие от <c>cryptography</c> в upstream), но любая
    /// 32-байтовая строка — корректная кодировка u-координаты, а рукопожатие с нами никто
    /// не завершает. Для DPI важно, что это 32 байта, а не 1216 и не фиксированный шаблон.
    /// </remarks>
    private static byte[] CreateX25519PublicKey()
    {
        var key = new byte[X25519KeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Собирает серверный key share: при гибридной группе — «шифртекст» MLKEM768 плюс
    /// публичный ключ X25519, иначе только X25519 (upstream <c>_server_key_share</c>).
    /// </summary>
    private static byte[] BuildServerKeyShare(bool mirrorsMlKem768)
    {
        var publicKey = CreateX25519PublicKey();
        if (!mirrorsMlKem768)
        {
            return publicKey;
        }

        var combined = new byte[MlKem768CiphertextLength + X25519KeyLength];
        RandomNumberGenerator.Fill(combined.AsSpan(0, MlKem768CiphertextLength));
        publicKey.CopyTo(combined, MlKem768CiphertextLength);
        return combined;
    }

    /// <summary>Зеркалит ли ответ гибридную группу клиента (длина share ровно 1216 байт).</summary>
    private static bool MirrorsMlKem768(ClientHelloOffers offers)
        => offers.KeyShares.TryGetValue(GroupX25519MlKem768, out var length)
            && length == X25519MlKem768ClientShareLength;

    /// <summary>
    /// Размер единственной записи ApplicationData: измеренный у сайта маскировки либо
    /// случайный из узкого диапазона по умолчанию (upstream <c>_appdata_len</c>).
    /// </summary>
    private static int PickAppDataLength()
    {
        var profiled = Volatile.Read(ref profiledAppDataLength);
        return profiled > 0
            ? profiled
            : Random.Shared.Next(FallbackAppDataMinLength, FallbackAppDataMaxLength + 1);
    }

    /// <summary>
    /// Собирает ответ сервера: ServerHello под параметры клиента, ChangeCipherSpec и
    /// одна запись ApplicationData, размер которой совпадает с сайтом маскировки.
    /// server_random заполняется HMAC-SHA256 от client_random и всего ответа.
    /// </summary>
    /// <param name="secret">Секрет токена (16 байт).</param>
    /// <param name="clientRandom">client_random из ClientHello.</param>
    /// <param name="sessionId">session id из ClientHello.</param>
    /// <param name="offers">Параметры ClientHello для зеркалирования; <see langword="null"/> — без них.</param>
    public static byte[] BuildServerHello(
        byte[] secret,
        byte[] clientRandom,
        byte[] sessionId,
        ClientHelloOffers? offers = null)
    {
        offers ??= ClientHelloOffers.Empty;

        var cipher = SelectCipherSuite(offers);
        var mirrorsMlKem768 = MirrorsMlKem768(offers);
        var group = mirrorsMlKem768 ? GroupX25519MlKem768 : GroupX25519;
        var keyShare = BuildServerKeyShare(mirrorsMlKem768);

        // supported_versions (тип 2 + длина 2 + данные 2) и key_share (тип 2 + длина 2 + данные).
        var extensionsLength = 6 + 4 + 4 + keyShare.Length;

        // Тело ServerHello: version, random, session id, шифр, сжатие, расширения.
        var bodyLength = 2 + RandomLength + 1 + sessionId.Length + 2 + 1 + 2 + extensionsLength;

        using var buffer = new MemoryStream(5 + 4 + bodyLength);
        buffer.WriteByte(RecordHandshake);
        buffer.WriteByte(0x03);
        buffer.WriteByte(0x03);
        WriteUInt16BigEndian(buffer, 4 + bodyLength);

        buffer.WriteByte(0x02);
        buffer.WriteByte((byte)(bodyLength >> 16));
        buffer.WriteByte((byte)(bodyLength >> 8));
        buffer.WriteByte((byte)bodyLength);

        buffer.WriteByte(0x03);
        buffer.WriteByte(0x03);
        buffer.Write(new byte[RandomLength]);
        buffer.WriteByte((byte)sessionId.Length);
        buffer.Write(sessionId);
        WriteUInt16BigEndian(buffer, cipher);
        buffer.WriteByte(0x00);
        WriteUInt16BigEndian(buffer, extensionsLength);

        WriteUInt16BigEndian(buffer, ExtensionSupportedVersions);
        WriteUInt16BigEndian(buffer, 2);
        WriteUInt16BigEndian(buffer, Tls13Version);

        WriteUInt16BigEndian(buffer, ExtensionKeyShare);
        WriteUInt16BigEndian(buffer, 4 + keyShare.Length);
        WriteUInt16BigEndian(buffer, group);
        WriteUInt16BigEndian(buffer, keyShare.Length);
        buffer.Write(keyShare);

        buffer.Write(ChangeCipherSpecFrame);

        var appDataLength = PickAppDataLength();
        var appData = new byte[appDataLength];
        RandomNumberGenerator.Fill(appData);
        buffer.WriteByte(RecordApplicationData);
        buffer.WriteByte(0x03);
        buffer.WriteByte(0x03);
        WriteUInt16BigEndian(buffer, appDataLength);
        buffer.Write(appData);

        var response = buffer.ToArray();

        // server_random = HMAC(secret, client_random + ответ), причём в ответе поле
        // random ещё нулевое — ровно так же считает upstream.
        var macInput = new byte[clientRandom.Length + response.Length];
        clientRandom.CopyTo(macInput, 0);
        response.CopyTo(macInput, clientRandom.Length);
        var mac = HMACSHA256.HashData(secret, macInput);
        mac.CopyTo(response, ServerHelloRandomOffset);

        return response;
    }

    /// <summary>
    /// Возвращает длину первой записи ApplicationData во фрагменте серверного полёта.
    /// </summary>
    /// <returns>
    /// Длина записи; <see langword="null"/> — данных пока не хватает; 0 — записей
    /// ApplicationData в этом полёте быть не может.
    /// </returns>
    public static int? FirstAppDataRecordLength(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (true)
        {
            if (data.Length - offset < 5)
            {
                return null;
            }

            var type = data[offset];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
            if (data.Length - offset - 5 < length)
            {
                return null;
            }

            if (type == RecordApplicationData)
            {
                return length;
            }

            if (type != RecordHandshake && type != RecordChangeCipherSpec)
            {
                // Посторонний тип записи: профиль снять нельзя.
                return 0;
            }

            offset += 5 + length;
        }
    }

    /// <summary>
    /// Снимает профиль с сайта маскировки: отправляет ClientHello и читает размер первой
    /// зашифрованной записи, не завершая рукопожатие (upstream <c>probe_appdata_len</c>).
    /// Весь замер ограничен одним общим дедлайном <paramref name="timeout"/>.
    /// </summary>
    /// <param name="domain">Домен сайта маскировки.</param>
    /// <param name="port">Порт (обычно 443).</param>
    /// <param name="timeout">Общий таймаут замера.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Размер записи либо <see langword="null"/>, если замер не удался.</returns>
    public static async Task<int?> ProbeAppDataLengthAsync(
        string domain,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(domain, port, deadline.Token).ConfigureAwait(false);

            await using var network = client.GetStream();
            using var capture = new FirstRecordCapture(network);
            using var ssl = new SslStream(
                capture,
                leaveInnerStreamOpen: true,
                userCertificateValidationCallback: static (_, _, _, _) => true);

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = domain,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            };

            // Рукопожатие не доводим до конца: нужен только первый полёт сервера.
            var handshake = ssl.AuthenticateAsClientAsync(options, deadline.Token);
            _ = handshake.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var length = await capture.WaitAsync(deadline.Token).ConfigureAwait(false);
            return length is > 0 ? length : null;
        }
        catch (Exception)
        {
            // Сайт недоступен, не TLS или не прислал данных — остаёмся без профиля.
            return null;
        }
    }

    /// <summary>
    /// Делит TLS-записи на записи не больше <see cref="ApplicationDataMax"/> байт.
    /// </summary>
    /// <param name="data">Полезная нагрузка (уже «зашифрованные» данные MTProto).</param>
    /// <returns>Склеенные записи ApplicationData.</returns>
    public static byte[] WrapTlsRecord(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        var chunks = (data.Length + ApplicationDataMax - 1) / ApplicationDataMax;
        var result = new byte[data.Length + 5 * chunks];
        var srcOffset = 0;
        var dstOffset = 0;

        while (srcOffset < data.Length)
        {
            var size = Math.Min(ApplicationDataMax, data.Length - srcOffset);
            result[dstOffset] = RecordApplicationData;
            result[dstOffset + 1] = 0x03;
            result[dstOffset + 2] = 0x03;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(dstOffset + 3, 2), (ushort)size);
            data.Slice(srcOffset, size).CopyTo(result.AsSpan(dstOffset + 5, size));
            srcOffset += size;
            dstOffset += 5 + size;
        }

        return result;
    }

    private static void WriteUInt16BigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    /// <summary>
    /// Поток-обёртка: пропускает чтение насквозь и копит прочитанные байты, пока в них не
    /// появится первая запись ApplicationData (серверный полёт TLS 1.3).
    /// </summary>
    private sealed class FirstRecordCapture(Stream inner) : Stream
    {
        /// <summary>Предел накапливаемого полёта: больше профилю не нужно.</summary>
        private const int MaxCapturedBytes = 64 * 1024;

        private readonly MemoryStream captured = new();
        private readonly TaskCompletionSource<int?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ждёт длину первой записи ApplicationData (<see langword="null"/> — полёта не будет).</summary>
        public Task<int?> WaitAsync(CancellationToken cancellationToken) => result.Task.WaitAsync(cancellationToken);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Observe(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
            => Observe(inner.Read(buffer), buffer);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Observe(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false), buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Observe(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Span);

        private int Observe(int read, Span<byte> buffer)
        {
            if (read <= 0)
            {
                result.TrySetResult(null);
                return read;
            }

            if (captured.Length < MaxCapturedBytes)
            {
                captured.Write(buffer[..read]);
                var pending = captured.GetBuffer().AsSpan(0, (int)captured.Length);
                if (FirstAppDataRecordLength(pending) is { } length)
                {
                    result.TrySetResult(length);
                }
            }

            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
