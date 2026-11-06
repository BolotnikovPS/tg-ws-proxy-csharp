#nullable disable
using System;

namespace TgWsProxy.Infrastructure.MtProto;

[AttributeUsage(AttributeTargets.Class)]
public sealed class TLDefAttribute(uint ctorNb) : Attribute
{
    public readonly uint CtorNb = ctorNb;
    public int inheritAt = -1;
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class IfFlagAttribute(int bit) : Attribute
{
    public readonly int Bit = bit;
}