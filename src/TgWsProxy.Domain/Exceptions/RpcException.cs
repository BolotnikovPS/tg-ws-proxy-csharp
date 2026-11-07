namespace TgWsProxy.Domain.Exceptions;

/// <summary>
/// Ошибка MTProto RPC: Telegram ответил на запрос конструкцией rpc_error
/// (error_code + error_message). Адаптер MTProto пробрасывает её код наверх
/// в Bot API-ответ, поэтому она объявлена в домене, как и остальные
/// исключения MTProto (<see cref="MTProtoTransportException"/> и др.).
/// </summary>
public sealed class RpcException(int code, string message, int x = -1)
    : Exception(message)
{
    /// <summary>Код ошибки RPC (например 401 AUTH_KEY_UNREGISTERED, 420 FLOOD_WAIT).</summary>
    public int Code { get; } = code;

    /// <summary>Значение X из текста ошибки; -1, если переменной X нет.</summary>
    public int X { get; } = x;

    public override string ToString()
    {
        var str = base.ToString();
        return str.Insert(str.IndexOf(':') + 1, " " + Code);
    }
}
