#nullable enable

using System.Buffers.Binary;

namespace TgWsProxy.Application.Logic.Helpers;

/// <summary>
/// Разбор и сборка MTProto-пакетов для HTTP/2-транспорта через Cloudflare
/// (порт upstream <c>_read_packet</c> / <c>_encode_reply</c> из <c>proxy/cf_h2.py</c>).
/// </summary>
public static class H2PacketCodec
{
    /// <summary>Минимальный допустимый размер MTProto-пакета.</summary>
    public const int MinPacketLength = 24;

    private static readonly byte[] Zero8 = new byte[8];

    /// <summary>
    /// Читает один MTProto-пакет из клиентского потока, расшифровывая его потоком
    /// <paramref name="decryptor"/>. Поддерживает abridged, intermediate и padded intermediate.
    /// </summary>
    /// <param name="reader">Клиентский поток (расшифрованный трафик).</param>
    /// <param name="decryptor">AES-CTR поток расшифровки клиента.</param>
    /// <param name="protoTag">4 байта proto tag текущей сессии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Тело пакета (без padding) и признак quick-ack.</returns>
    /// <exception cref="EndOfStreamException">Клиент закрыл соединение.</exception>
    /// <exception cref="InvalidDataException">Длина пакета или padding некорректны.</exception>
    public static async Task<(byte[] Body, bool Quick)> ReadPacketAsync(
        Stream reader,
        IncrementalCipher decryptor,
        byte[] protoTag,
        CancellationToken cancellationToken)
    {
        var abridged = protoTag.AsSpan().SequenceEqual(Protocol.ProtoTagAbridged);
        var secure = protoTag.AsSpan().SequenceEqual(Protocol.ProtoTagSecure);

        bool quick;
        int length;

        if (abridged)
        {
            var first = (await ReadPlainAsync(reader, decryptor, 1, cancellationToken).ConfigureAwait(false))[0];
            quick = (first & 0x80) != 0;
            var words = first & 0x7F;
            if (words == 0x7F)
            {
                var extra = await ReadPlainAsync(reader, decryptor, 3, cancellationToken).ConfigureAwait(false);
                words = extra[0] | (extra[1] << 8) | (extra[2] << 16);
            }

            length = words * 4;
        }
        else
        {
            var header = await ReadPlainAsync(reader, decryptor, 4, cancellationToken).ConfigureAwait(false);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(header);
            quick = (value & 0x80000000) != 0;
            length = (int)(value & 0x7FFFFFFF);
        }

        var limit = Protocol.H2MaxPacket + (secure ? Protocol.H2MaxPlainPadding : 0);
        if (length < MinPacketLength || length > limit)
        {
            throw new InvalidDataException($"native packet length outside supported range: {length}");
        }

        var body = await ReadPlainAsync(reader, decryptor, length, cancellationToken).ConfigureAwait(false);

        if (secure)
        {
            int packetLength;
            int maxPadding;
            if (body.AsSpan(0, 8).SequenceEqual(Zero8))
            {
                packetLength = 20 + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16, 4));
                maxPadding = Protocol.H2MaxPlainPadding;
            }
            else
            {
                packetLength = 24 + ((length - 24) / 16 * 16);
                maxPadding = 15;
            }

            var padding = length - packetLength;
            if (packetLength < MinPacketLength || packetLength > length || padding > maxPadding)
            {
                throw new InvalidDataException(
                    $"invalid padded native packet: frame={length} packet={packetLength} padding={padding} limit={maxPadding}");
            }

            body = body.AsSpan(0, packetLength).ToArray();
        }

        if (body.Length % 4 != 0 || body.Length > Protocol.H2MaxPacket)
        {
            throw new InvalidDataException("invalid native packet alignment or size");
        }

        return (body, quick);
    }

    /// <summary>
    /// Кодирует ответ Telegram в кадр соответствующего MTProto-протокола
    /// (порт upstream <c>_encode_reply</c>).
    /// </summary>
    /// <param name="body">Тело ответа (кратно 4 байтам, не больше <see cref="Protocol.H2MaxPacket"/>).</param>
    /// <param name="protoTag">4 байта proto tag текущей сессии.</param>
    /// <returns>Готовый к отправке клиенту кадр.</returns>
    /// <exception cref="InvalidDataException">Тело пустое, не выровнено или слишком велико.</exception>
    public static byte[] EncodeReply(byte[] body, byte[] protoTag)
    {
        if (body.Length == 0 || body.Length % 4 != 0 || body.Length > Protocol.H2MaxPacket)
        {
            throw new InvalidDataException($"invalid HTTP MTProto response length: {body.Length}");
        }

        if (protoTag.AsSpan().SequenceEqual(Protocol.ProtoTagAbridged))
        {
            var words = body.Length / 4;
            if (words < 0x7F)
            {
                var framed = new byte[1 + body.Length];
                framed[0] = (byte)words;
                body.CopyTo(framed, 1);
                return framed;
            }

            var extended = new byte[4 + body.Length];
            extended[0] = 0x7F;
            extended[1] = (byte)(words & 0xFF);
            extended[2] = (byte)((words >> 8) & 0xFF);
            extended[3] = (byte)((words >> 16) & 0xFF);
            body.CopyTo(extended, 4);
            return extended;
        }

        var paddingLength = protoTag.AsSpan().SequenceEqual(Protocol.ProtoTagSecure)
            ? Random.Shared.Next(4)
            : 0;

        var result = new byte[4 + body.Length + paddingLength];
        BinaryPrimitives.WriteInt32LittleEndian(result, body.Length + paddingLength);
        body.CopyTo(result, 4);
        if (paddingLength > 0)
        {
            Random.Shared.NextBytes(result.AsSpan(4 + body.Length, paddingLength));
        }

        return result;
    }

    /// <summary>
    /// Читает ровно <paramref name="count"/> байт и расшифровывает их на месте.
    /// </summary>
    private static async Task<byte[]> ReadPlainAsync(
        Stream reader,
        IncrementalCipher decryptor,
        int count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        decryptor.UpdateInPlace(buffer);
        return buffer;
    }
}
