#nullable enable

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Итог чтения клиентского init-пакета.
/// </summary>
internal enum ClientInitStatus
{
    /// <summary>Init прочитан, можно продолжать обработку.</summary>
    Ok,

    /// <summary>Клиент не прошёл Fake TLS — трафик нужно перенаправить на домен маскировки.</summary>
    Masking,

    /// <summary>Клиент пришёл не по TLS — отвечаем HTTP 301 на домен маскировки.</summary>
    HttpRedirect,

    /// <summary>Клиент отключился или прислал неполные данные.</summary>
    Closed,
}
