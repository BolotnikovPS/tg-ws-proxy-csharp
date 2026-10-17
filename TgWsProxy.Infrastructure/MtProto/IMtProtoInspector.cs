#nullable enable

using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Разбор MTProto handshake и генерация relay-пакетов.
/// </summary>
public interface IMtProtoInspector
{
    /// <summary>
    /// Пытается разобрать init-пакет клиента: проверяет secret и извлекает DC, media-признак,
    /// тестовый DC и proto tag.
    /// </summary>
    /// <param name="init">Сырые байты init-пакета (не менее 64 байт).</param>
    /// <param name="secret">MTProto proxy secret (16 байт).</param>
    /// <returns>Разобранные параметры handshake либо <see langword="null"/>, если secret не подошёл.</returns>
    MtProtoHandshake? TryHandshake(ReadOnlySpan<byte> init, byte[] secret);

    /// <summary>
    /// Проверяет, что клиентский трафик похож на HTTP-транспорт MTProto (прокси его не обслуживает).
    /// </summary>
    /// <param name="data">Первые байты клиентского трафика.</param>
    /// <returns><see langword="true"/>, если обнаружен HTTP-транспорт.</returns>
    bool IsHttpTransport(ReadOnlySpan<byte> data);

    /// <summary>
    /// Генерирует relay_init пакет (64 байта) для отправки на сервер Telegram.
    /// </summary>
    /// <param name="protoTag">Тег протокола (abridged/intermediate/padded).</param>
    /// <param name="dcIdx">Индекс DC (отрицательный для media-трафика).</param>
    /// <returns>Сгенерированный relay_init пакет.</returns>
    byte[] GenerateRelayInit(byte[] protoTag, short dcIdx);
}
