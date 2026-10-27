namespace TgWsProxy.Domain;

/// <summary>
/// Транспортное представление вложенного блока <c>mtproto</c>. <c>null</c> на уровне блока —
/// не менять его; внутри блока подчинённые ключи также nullable (отсутствующий ключ остаётся
/// как есть, пустой массив — очистить коллекцию). Токены доступа вынесены на верхний уровень
/// (<see cref="ConfigDto.Tokens"/>) — блок <c>mtproto</c> их больше не содержит.
/// Категории ключей см. <see cref="MtProtoConfig"/>.
/// </summary>
public sealed class MtProtoConfigDto
{
    public int? Port { get; set; }

    public string? PublicHost { get; set; }

    public List<string>? DcIp { get; set; }

    public bool? AllowInvalidCertificates { get; set; }

    public int? WsMaxFrameBytes { get; set; }

    public int? WsConnectTimeoutSeconds { get; set; }

    public int? SocketBufferKb { get; set; }

    public int? WsPoolSize { get; set; }
}