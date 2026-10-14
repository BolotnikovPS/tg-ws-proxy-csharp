#nullable enable

namespace TgWsProxy.Domain.Exceptions;

/// <summary>
/// Ошибка доставки данных клиенту (native-сторона) при работе через HTTP/2-канал
/// (порт upstream <c>_NativeDeliveryError</c> из <c>proxy/cf_h2.py</c>).
/// </summary>
public sealed class NativeDeliveryException(string message) : Exception(message);
