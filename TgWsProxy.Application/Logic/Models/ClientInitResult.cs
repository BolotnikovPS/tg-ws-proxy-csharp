#nullable enable

using TgWsProxy;

namespace TgWsProxy.Application.Logic.Models;

/// <summary>
/// Результат чтения init-пакета: статус, данные, поток для дальнейшей работы и метка сессии.
/// </summary>
/// <param name="Status">Итог разбора первых байт.</param>
/// <param name="Init">64 байта MTProto init (только при <see cref="ClientInitStatus.Ok"/>).</param>
/// <param name="ClientStream">Поток для обмена с клиентом (TLS-обёртка при Fake TLS).</param>
/// <param name="Label">Метка сессии (peer из PROXY protocol либо исходный scope).</param>
/// <param name="FirstBytes">Первые байты клиента для перенаправления на домен маскировки.</param>
internal sealed record ClientInitResult(
    ClientInitStatus Status,
    byte[]? Init,
    Stream? ClientStream,
    string Label,
    byte[]? FirstBytes);
