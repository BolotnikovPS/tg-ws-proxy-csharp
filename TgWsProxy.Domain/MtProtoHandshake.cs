#nullable enable

namespace TgWsProxy.Domain;

/// <summary>
/// Результат успешного разбора MTProto init-пакета клиента: DC, media-признак и proto tag.
/// </summary>
/// <param name="Dc">Номер дата-центра как он пришёл от клиента (<c>abs(dc_idx)</c>); тестовый DC
/// (значение &gt;= 10000) нормализуется вызывающей стороной.</param>
/// <param name="IsMedia">Признак media-трафика (отрицательный dc_idx в handshake).</param>
/// <param name="ProtoTag">4 байта proto tag (abridged/intermediate/padded).</param>
/// <param name="ProtoInt">Proto tag как little-endian 32-битное число.</param>
public readonly record struct MtProtoHandshake(int Dc, bool IsMedia, byte[] ProtoTag, int ProtoInt);
