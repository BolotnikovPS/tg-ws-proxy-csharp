using System.Buffers.Binary;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Тип кадра relay-протокола WEB proxy (browser-relay). Значения выровнены с tdesktop
/// <c>mtproto/web_proxy/web_proxy_frame.h</c> (<c>FrameType</c>).
/// </summary>
public enum RelayFrameType : byte
{
    /// <summary>Открыть новый мультиплексированный поток.</summary>
    Open = 0x01,

    /// <summary>Данные потока (payload — обфусцированный MTProto).</summary>
    Data = 0x02,

    /// <summary>Закрыть поток.</summary>
    Close = 0x03,

    /// <summary>Flow-control: вернуть кредит приёмного окна потоку.</summary>
    Window = 0x04,

    /// <summary>Keepalive-запрос.</summary>
    Ping = 0x05,

    /// <summary>Keepalive-ответ.</summary>
    Pong = 0x06,

    /// <summary>Служебное приветствие (0x10).</summary>
    Hello = 0x10,

    /// <summary>Подтверждение приветствия (0x11).</summary>
    Welcome = 0x11,

    /// <summary>Служебный запрос авторизации (0x12).</summary>
    AuthChallenge = 0x12,

    /// <summary>Ответ авторизации (0x13).</summary>
    AuthResponse = 0x13,

    /// <summary>Отказ / завершение соединения (0x1F).</summary>
    Bye = 0x1F,
}

/// <summary>Причина отклонения кадра на разборе.</summary>
internal enum RelayFrameError
{
    None = 0,

    /// <summary>Буфер короче 8-байтного заголовка или объявленного payload.</summary>
    HeaderTooShort,

    /// <summary>Объявленная длина payload превышает <see cref="RelayFrameCodec.MaxFramePayload"/>.</summary>
    PayloadTooLarge,

    /// <summary>Неизвестный тип кадра.</summary>
    UnknownType,
}

/// <summary>Разобранный relay-кадр: заголовок уже отделён, payload не скопирован.</summary>
public readonly record struct RelayFrame(RelayFrameType Type, int StreamId, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Кодек relay-frame протокола WEB proxy. Заголовок — 8 байт, все многобайтовые поля big-endian:
/// <c>[type:1][streamId:3][length:4]</c> + payload. Горячий путь без копирования payload:
/// <see cref="TryParse"/> возвращает срез входного буфера.
/// </summary>
internal static class RelayFrameCodec
{
    /// <summary>Размер заголовка кадра в байтах (<c>kFrameHeaderSize</c>).</summary>
    public const int FrameHeaderSize = 8;

    /// <summary>Максимальный размер payload кадра (<c>kMaxFramePayload</c> = 1 МиБ).</summary>
    public const int MaxFramePayload = 1024 * 1024;

    /// <summary>Максимум кадров в одном пакете (<c>kMaxBatchFrames</c>).</summary>
    public const int MaxBatchFrames = 4096;

    /// <summary>Начальное приёмное окно потока (<c>kInitialStreamWindow</c> = 4 МиБ).</summary>
    public const int InitialStreamWindow = 4 * 1024 * 1024;

    /// <summary>Максимальное значение <c>streamId</c> (24-битное поле).</summary>
    public const int MaxStreamId = 0x00FF_FFFF;

    /// <summary>Маркер секрета ссылки WEB proxy (<c>kWebProxyLinkSecretMarker</c>).</summary>
    public const byte WebProxyLinkSecretMarker = 0x70;

    /// <summary>Единственный допустимый payload кадра <see cref="RelayFrameType.Hello"/> — версия протокола 1.</summary>
    public const byte HelloProtocolVersion = 0x01;

    /// <summary>Полный размер кадра с payload указанной длины.</summary>
    public static int FrameSize(int payloadLength) => FrameHeaderSize + payloadLength;

    /// <summary>
    /// Разбирает кадр из целого сообщения (одно WS-сообщение = один relay-кадр).
    /// Payload не копируется — возвращается срез входного буфера.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> message, out RelayFrame frame, out RelayFrameError error)
    {
        frame = default;
        var span = message.Span;
        if (span.Length < FrameHeaderSize)
        {
            error = RelayFrameError.HeaderTooShort;
            return false;
        }

        var typeValue = span[0];
        if (!IsKnownType(typeValue))
        {
            error = RelayFrameError.UnknownType;
            return false;
        }

        var streamId = (span[1] << 16) | (span[2] << 8) | span[3];
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        if (length > MaxFramePayload)
        {
            error = RelayFrameError.PayloadTooLarge;
            return false;
        }

        if (span.Length < FrameHeaderSize + length)
        {
            error = RelayFrameError.HeaderTooShort;
            return false;
        }

        frame = new RelayFrame((RelayFrameType)typeValue, streamId, message.Slice(FrameHeaderSize, length));
        error = RelayFrameError.None;
        return true;
    }

    /// <summary>Записывает 8-байтный заголовок кадра.</summary>
    public static void WriteHeader(Span<byte> destination, RelayFrameType type, int streamId, int payloadLength)
    {
        destination[0] = (byte)type;
        destination[1] = (byte)(streamId >> 16);
        destination[2] = (byte)(streamId >> 8);
        destination[3] = (byte)streamId;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)payloadLength);
    }

    /// <summary>Записывает заголовок и payload целиком в буфер размера не менее <see cref="FrameSize"/>.</summary>
    public static void Write(Span<byte> destination, RelayFrameType type, int streamId, ReadOnlySpan<byte> payload)
    {
        WriteHeader(destination, type, streamId, payload.Length);
        payload.CopyTo(destination[FrameHeaderSize..]);
    }

    /// <summary>Размер payload кадра <see cref="RelayFrameType.Window"/> (BE uint32).</summary>
    public const int WindowCreditSize = 4;

    /// <summary>
    /// Читает кредит приёмного окна из payload кадра <c>Window</c> (4 байта BE). Пустой или
    /// короткий payload трактуется как 0 (no-op).
    /// </summary>
    public static int ParseWindowCredit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < WindowCreditSize)
        {
            return 0;
        }

        return (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
    }

    /// <summary>Записывает кредит приёмного окна (4 байта BE).</summary>
    public static void WriteWindowCredit(Span<byte> destination, int credit)
        => BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)credit);

    private static bool IsKnownType(byte value) => value is
        (byte)RelayFrameType.Open or
        (byte)RelayFrameType.Data or
        (byte)RelayFrameType.Close or
        (byte)RelayFrameType.Window or
        (byte)RelayFrameType.Ping or
        (byte)RelayFrameType.Pong or
        (byte)RelayFrameType.Hello or
        (byte)RelayFrameType.Welcome or
        (byte)RelayFrameType.AuthChallenge or
        (byte)RelayFrameType.AuthResponse or
        (byte)RelayFrameType.Bye;
}
