#nullable enable

using System.Collections.Frozen;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Константы и таблицы протокола MTProto/WebSocket, выровненные с upstream <c>proxy/utils.py</c>.
/// </summary>
public static class Protocol
{
    /// <summary>Длина MTProto handshake (HANDSHAKE_LEN).</summary>
    public const int HandshakeLen = 64;

    /// <summary>Смещение prekey+iv внутри handshake (SKIP_LEN).</summary>
    public const int SkipLen = 8;

    /// <summary>Длина prekey (PREKEY_LEN).</summary>
    public const int PreKeyLen = 32;

    /// <summary>Длина ключа AES (KEY_LEN).</summary>
    public const int KeyLen = 32;

    /// <summary>Длина IV/counter (IV_LEN).</summary>
    public const int IvLen = 16;

    /// <summary>Смещение proto tag в handshake (PROTO_TAG_POS).</summary>
    public const int ProtoTagPos = 56;

    /// <summary>Смещение dc_idx в handshake (DC_IDX_POS).</summary>
    public const int DcIdxPos = 60;

    /// <summary>Abridged-протокол (PROTO_ABRIDGED_INT).</summary>
    public const int ProtoAbridgedInt = unchecked((int)0xEFEFEFEF);

    /// <summary>Intermediate-протокол (PROTO_INTERMEDIATE_INT).</summary>
    public const int ProtoIntermediateInt = unchecked((int)0xEEEEEEEE);

    /// <summary>Padded intermediate-протокол (PROTO_PADDED_INTERMEDIATE_INT).</summary>
    public const int ProtoPaddedIntermediateInt = unchecked((int)0xDDDDDDDD);

    /// <summary>Байты proto tag для abridged-протокола (PROTO_TAG_ABRIDGED).</summary>
    public static readonly byte[] ProtoTagAbridged = [0xEF, 0xEF, 0xEF, 0xEF];

    /// <summary>Байты proto tag для intermediate-протокола (PROTO_TAG_INTERMEDIATE).</summary>
    public static readonly byte[] ProtoTagIntermediate = [0xEE, 0xEE, 0xEE, 0xEE];

    /// <summary>Байты proto tag для padded intermediate (PROTO_TAG_SECURE).</summary>
    public static readonly byte[] ProtoTagSecure = [0xDD, 0xDD, 0xDD, 0xDD];

    /// <summary>
    /// 64 нулевых байта (ZERO_64) для «прогрева» AES-CTR на длину handshake.
    /// Не модифицировать: массив общий для всех сессий.
    /// </summary>
    public static readonly byte[] Zero64 = new byte[HandshakeLen];

    /// <summary>Запрещённые первые байты генерируемого handshake (RESERVED_FIRST_BYTES).</summary>
    public static readonly FrozenSet<byte> ReservedFirstBytes = new HashSet<byte> { 0xEF }.ToFrozenSet();

    /// <summary>Запрещённые первые 4 байта генерируемого handshake (RESERVED_STARTS).</summary>
    public static readonly byte[][] ReservedStarts =
    [
        [0x48, 0x45, 0x41, 0x44], // HEAD
        [0x50, 0x4F, 0x53, 0x54], // POST
        [0x47, 0x45, 0x54, 0x20], // "GET "
        [0xEE, 0xEE, 0xEE, 0xEE],
        [0xDD, 0xDD, 0xDD, 0xDD],
        [0x16, 0x03, 0x01, 0x02],
        [0x4F, 0x50, 0x54, 0x49], // OPTI
    ];

    /// <summary>Запрещённые байты 4..8 генерируемого handshake (RESERVED_CONTINUE).</summary>
    public static readonly byte[] ReservedContinue = [0x00, 0x00, 0x00, 0x00];

    /// <summary>Продакшн-адреса дата-центров (DC_DEFAULT_IPS).</summary>
    public static readonly FrozenDictionary<int, string> DcDefaultIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.50",
        [2] = "149.154.167.51",
        [3] = "149.154.175.100",
        [4] = "149.154.167.91",
        [5] = "149.154.171.5",
        [203] = "91.105.192.100",
    }.ToFrozenDictionary();

    /// <summary>Адреса тестовых дата-центров (DC_TEST_IPS).</summary>
    public static readonly FrozenDictionary<int, string> DcTestIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.10",
        [2] = "149.154.167.40",
        [3] = "149.154.175.117",
    }.ToFrozenDictionary();

    /// <summary>Путь WebSocket-эндпоинта (WS_PATH).</summary>
    public const string WsPath = "/apiws";

    /// <summary>Путь тестового WebSocket-эндпоинта (WS_PATH_TEST).</summary>
    public const string WsPathTest = "/apiws_test";

    /// <summary>Порт WebSocket-эндпоинта Telegram.</summary>
    public const int WsPort = 443;

    /// <summary>Порт WebSocket-эндпоинта при <c>--no-secure</c> (обычный ws://).</summary>
    public const int WsPlainPort = 80;

    /// <summary>Максимальная длина WS-сообщения (MAX_MESSAGE_LEN, 16 МиБ).</summary>
    public const int MaxMessageLen = 16 * 1024 * 1024;

    /// <summary>Путь HTTP/2-эндпоинта Cloudflare (CF /api).</summary>
    public const string H2ApiPath = "/api";

    /// <summary>Максимальный размер MTProto-пакета в HTTP/2-транспорте (MAX_PACKET, 4 МиБ).</summary>
    public const int H2MaxPacket = 4 * 1024 * 1024;

    /// <summary>Максимальный размер случайного padding в padded-пакете (MAX_PLAIN_PADDING).</summary>
    public const int H2MaxPlainPadding = 256;

    /// <summary>
    /// Возвращает домены WebSocket для DC в порядке попыток (порт upstream <c>ws_domains</c>).
    /// </summary>
    /// <param name="dc">Номер дата-центра (203 приводится к 2).</param>
    /// <param name="isMedia">Признак media-трафика.</param>
    /// <returns>Список доменов для последовательного перебора.</returns>
    public static List<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203)
        {
            dc = 2;
        }

        return isMedia
            ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"]
            : [$"kws{dc}.web.telegram.org"];
    }
}
