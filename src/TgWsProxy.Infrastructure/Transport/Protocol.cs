using System.Collections.Frozen;
using static TgWsProxy.Domain.AppConstants.Protocol;

namespace TgWsProxy.Infrastructure.Transport;

/// <summary>
/// Константы и таблицы протокола MTProto/WebSocket, выровненные с upstream <c>proxy/utils.py</c>.
/// </summary>
public static class Protocol
{










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
        [1] = "149.154.174.100",
        [2] = "149.154.167.99",
        [3] = "149.154.174.100",
        [4] = "149.154.167.99",
        [5] = "149.154.170.100",
        [203] = "91.105.192.100",
    }.ToFrozenDictionary();

    /// <summary>Адреса тестовых дата-центров (DC_TEST_IPS).</summary>
    public static readonly FrozenDictionary<int, string> DcTestIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.10",
        [2] = "149.154.167.40",
        [3] = "149.154.175.117",
    }.ToFrozenDictionary();









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
