using System.Net;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Параметры запроса на fallback-маршрут (порт upstream <c>do_fallback(...)</c>).
/// </summary>
/// <param name="Client">Клиентский поток (уже прочитан MTProto init; может быть TLS-обёрткой).</param>
/// <param name="RelayInit">Сгенерированный relay init, который отправляется Telegram.</param>
/// <param name="Scope">Идентификатор сессии для логов.</param>
/// <param name="Dc">Номер дата-центра (без media-признака, тестовый DC уже нормализован).</param>
/// <param name="IsTestDc">Признак тестового DC (влияет на таблицу IP и отключает CF).</param>
/// <param name="IsMedia">Признак media-трафика.</param>
/// <param name="ProtoTag">4 байта proto tag (нужен для H2 media).</param>
/// <param name="Crypto">Четыре AES-CTR потока для ре-шифрования.</param>
/// <param name="Splitter">Разборщик MTProto-пакетов (может быть <see langword="null"/>).</param>
/// <param name="Peer">Адрес клиента без порта — ключ статистики по IP (может быть <see langword="null"/>).</param>
/// <param name="TokenId">
/// Идентификатор токена доступа сессии — вторая часть ключа учёта трафика (F-9.2). Может быть
/// <see langword="null"/>, если сессия не привязана к токену.
/// </param>
public sealed record FallbackRequest(
    Stream Client,
    byte[] RelayInit,
    string Scope,
    int Dc,
    bool IsTestDc,
    bool IsMedia,
    byte[] ProtoTag,
    CryptoCtx Crypto,
    MsgSplitter? Splitter,
    IPAddress? Peer = null,
    string? TokenId = null);
