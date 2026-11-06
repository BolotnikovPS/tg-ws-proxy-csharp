#nullable disable
using System;

namespace TgWsProxy.Infrastructure.MtProto;

public sealed class RpcException(int code, string message, int x = -1) : Exception(message)
{
    public readonly int Code = code;
    /// <summary>The value of X in the message, -1 if no variable X was found</summary>
    public readonly int X = x;
    public override string ToString() { var str = base.ToString(); return str.Insert(str.IndexOf(':') + 1, " " + Code); }
}