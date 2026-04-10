namespace TgWsProxy.Domain.Abstractions;

public interface IMtProtoInspector
{
    /// <summary>
    /// Определяет дата-центр и признак media-трафика по init-пакету MTProto.
    /// </summary>
    /// <param name="data">Сырые байты инициализационного пакета.</param>
    /// <param name="secret">MTProto proxy secret для дешифровки.</param>
    /// <returns>Пара значений DC и media-флага; отдельные поля могут быть неопределены.</returns>
    (int? Dc, bool? IsMedia) DcFromInit(byte[] data, byte[] secret);

    /// <summary>
    /// Генерирует байтовый поток AES-CTR для заданного ключа и IV.
    /// </summary>
    /// <param name="key">Ключ шифрования.</param>
    /// <param name="iv">Начальный вектор (счетчик).</param>
    /// <param name="len">Требуемая длина выходного потока.</param>
    /// <returns>Псевдослучайный поток байтов указанной длины.</returns>
    byte[] AesCtr(byte[] key, byte[] iv, int len);

    /// <summary>
    /// Проверяет, соответствует ли пакет HTTP-транспорту MTProto.
    /// </summary>
    /// <param name="data">Первые байты клиентского трафика.</param>
    /// <returns><see langword="true"/>, если обнаружен HTTP-транспорт.</returns>
    bool IsHttpTransport(ReadOnlySpan<byte> data);

    /// <summary>
    /// Подменяет значение DC в init-пакете MTProto.
    /// </summary>
    /// <param name="data">Исходный init-пакет.</param>
    /// <param name="dcRaw">Новое сырое значение DC.</param>
    /// <param name="secret">MTProto proxy secret для дешифровки/шифровки.</param>
    /// <returns>Модифицированный init-пакет.</returns>
    byte[] PatchInitDc(byte[] data, short dcRaw, byte[] secret);

    /// <summary>
    /// Генерирует relay_init пакет для отправки на сервер Telegram.
    /// </summary>
    /// <param name="protoTag">Тег протокола (abridged/intermediate/secure).</param>
    /// <param name="dcIdx">Индекс DC (положительный или отрицательный для media).</param>
    /// <returns>Сгенерированный relay_init пакет длиной 64 байта.</returns>
    byte[] GenerateRelayInit(byte[] protoTag, short dcIdx);
}
