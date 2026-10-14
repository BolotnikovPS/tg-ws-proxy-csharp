#nullable enable

namespace TgWsProxy.Domain.Exceptions;

/// <summary>
/// Транспортная ошибка MTProto, код которой нужно вернуть клиенту как есть
/// (порт upstream <c>_MTProtoTransportError</c> из <c>proxy/cf_h2.py</c>).
/// Возникает, когда Cloudflare отвечает 403/404/429/444 либо присылает 4-байтный
/// ответ с отрицательным кодом.
/// </summary>
public sealed class MTProtoTransportException(int code, string detail)
    : Exception($"MTProto transport error {code} ({detail})")
{
    /// <summary>Код ошибки, который прокси обязан отдать клиенту (отрицательный).</summary>
    public int Code { get; } = code;

    /// <summary>Дополнительное описание источника ошибки (HTTP-статус, cf_ray и т.п.).</summary>
    public string Detail { get; } = detail;
}
