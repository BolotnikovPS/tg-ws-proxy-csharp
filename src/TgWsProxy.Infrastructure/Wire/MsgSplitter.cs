using System.Buffers.Binary;
using TgWsProxy.Application.Logic;
using TgWsProxy.Infrastructure.Transport;

namespace TgWsProxy.Infrastructure.Wire;

/// <summary>
/// Разбивает поток MTProto на отдельные транспортные пакеты, чтобы каждый отправлялся
/// отдельным WS-кадром (порт upstream <c>MsgSplitter</c>).
/// </summary>
/// <remarks>
/// Формат кадров зависит от proto tag: abridged использует 1-байтовый (или 4-байтовый
/// расширенный) заголовок, intermediate и padded intermediate — 4-байтовую длину LE32.
/// Буферы компактизируются, а не сдвигаются на каждый пакет (в отличие от старой
/// реализации со списком и <c>RemoveRange</c>).
/// </remarks>
public sealed class MsgSplitter
{
    private readonly int _proto;
    private byte[] _cipher = new byte[8192];
    private byte[] _plain = new byte[8192];

    /// <summary>Количество валидных байт в буферах (от <see cref="_start"/> до конца).</summary>
    private int _len;

    /// <summary>Смещение первого неразобранного байта.</summary>
    private int _start;

    /// <summary>Признак отключения разбора (некорректный поток — данные идут как есть).</summary>
    private bool _disabled;

    /// <summary>
    /// Создаёт разборщик для указанного proto tag.
    /// </summary>
    /// <param name="protoInt">Proto tag как 32-битное число (abridged/intermediate/padded).</param>
    public MsgSplitter(int protoInt) => _proto = protoInt;

    /// <summary>Признак того, что разбор отключён и данные передаются без разбиения.</summary>
    public bool Disabled => _disabled;

    /// <summary>
    /// Добавляет очередной фрагмент и возвращает готовые пакеты (срезы шифротекста).
    /// </summary>
    /// <param name="chunk">Шифротекст, прочитанный от клиента.</param>
    /// <param name="plain">Тот же фрагмент после расшифровки (для определения границ пакетов).</param>
    /// <returns>Список пакетов; пустой, если пакет ещё не собран целиком.</returns>
    /// <remarks>
    /// Возвращаемые срезы указывают во внутренний буфер и действительны до следующего
    /// вызова <see cref="Split"/> или <see cref="Flush"/>: вызывающая сторона обязана
    /// отправить их синхронно (WS-отправка читает срез до первой приостановки await).
    /// Компактизация буфера выполняется только в начале следующего вызова, поэтому
    /// срезы не портятся внутри текущего вызова.
    /// </remarks>
    public List<ReadOnlyMemory<byte>> Split(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> plain)
    {
        if (chunk.Length == 0)
        {
            return [];
        }

        if (_disabled)
        {
            // Отключённый разбор: фрагмент приходит из арендованного пула вызывающей
            // стороны и не переживает её кадр, поэтому копируем.
            return [chunk.ToArray()];
        }

        // Предыдущие срезы уже отправлены — можно безопасно компактизировать.
        CompactIfNeeded();
        EnsureCapacity(_len + chunk.Length);
        chunk.CopyTo(_cipher.AsSpan(_len));
        plain.CopyTo(_plain.AsSpan(_len));
        _len += chunk.Length;

        var parts = new List<ReadOnlyMemory<byte>>();
        var offset = _start;

        while (offset < _len)
        {
            var packetLen = NextPacketLen(offset, _len - offset);
            if (packetLen is null)
            {
                break;
            }

            if (packetLen <= 0)
            {
                // Некорректный поток: отдаём остаток целиком и отключаем разбор.
                parts.Add(_cipher.AsMemory(offset, _len - offset));
                offset = _len;
                _disabled = true;
                break;
            }

            parts.Add(_cipher.AsMemory(offset, packetLen.Value));
            offset += packetLen.Value;
        }

        Consume(offset);
        return parts;
    }

    /// <summary>
    /// Возвращает остаток буфера (вызывается при завершении соединения).
    /// </summary>
    /// <returns>Список из одного неполного пакета либо пустой список.</returns>
    public List<ReadOnlyMemory<byte>> Flush()
    {
        if (_len == _start)
        {
            Reset();
            return [];
        }

        var tail = _cipher.AsMemory(_start, _len - _start);
        Reset();
        return [tail];
    }

    private void Reset()
    {
        _len = 0;
        _start = 0;
    }

    /// <summary>
    /// Учитывает разобранный префикс буфера, только сдвигая указатель.
    /// Компактизация выполняется отдельно в <see cref="CompactIfNeeded"/> в начале
    /// следующего вызова <see cref="Split"/>, чтобы не затирать уже возвращённые срезы.
    /// </summary>
    private void Consume(int offset)
    {
        if (offset == 0)
        {
            return;
        }

        if (offset >= _len)
        {
            Reset();
            return;
        }

        _start = offset;
    }

    /// <summary>
    /// Сдвигает неразобранный остаток в начало, если потреблённый префикс занимает
    /// заметную часть буфера. Вызывается только до добавления новых данных.
    /// </summary>
    private void CompactIfNeeded()
    {
        if (_start > 0 && _start >= 4096 && _start * 2 >= _len)
        {
            var remaining = _len - _start;
            Buffer.BlockCopy(_cipher, _start, _cipher, 0, remaining);
            Buffer.BlockCopy(_plain, _start, _plain, 0, remaining);
            _start = 0;
            _len = remaining;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_start > 0 && _start + required > _cipher.Length)
        {
            var remaining = _len - _start;
            Buffer.BlockCopy(_cipher, _start, _cipher, 0, remaining);
            Buffer.BlockCopy(_plain, _start, _plain, 0, remaining);
            _start = 0;
            _len = remaining;
        }

        if (_len + required <= _cipher.Length)
        {
            return;
        }

        var size = Math.Max(_cipher.Length * 2, _len + required);
        Array.Resize(ref _cipher, size);
        Array.Resize(ref _plain, size);
    }

    /// <summary>
    /// Определяет длину следующего пакета.
    /// </summary>
    /// <param name="offset">Смещение начала пакета в буфере.</param>
    /// <param name="avail">Доступное количество байт от <paramref name="offset"/>.</param>
    /// <returns>Длина пакета, 0 при ошибке формата либо <see langword="null"/>, если данных мало.</returns>
    private int? NextPacketLen(int offset, int avail)
    {
        if (avail <= 0)
        {
            return null;
        }

        if (_proto == Protocol.ProtoAbridgedInt)
        {
            return NextAbridgedLen(offset, avail);
        }

        if (_proto is Protocol.ProtoIntermediateInt or Protocol.ProtoPaddedIntermediateInt)
        {
            return NextIntermediateLen(offset, avail);
        }

        return 0;
    }

    private int? NextAbridgedLen(int offset, int avail)
    {
        var first = _plain[offset];
        int payloadLen;
        int headerLen;

        if (first is 0x7F or 0xFF)
        {
            if (avail < 4)
            {
                return null;
            }

            payloadLen = (_plain[offset + 1] | (_plain[offset + 2] << 8) | (_plain[offset + 3] << 16)) * 4;
            headerLen = 4;
        }
        else
        {
            payloadLen = (first & 0x7F) * 4;
            headerLen = 1;
        }

        if (payloadLen <= 0)
        {
            return 0;
        }

        var packetLen = headerLen + payloadLen;
        return avail < packetLen ? null : packetLen;
    }

    private int? NextIntermediateLen(int offset, int avail)
    {
        if (avail < 4)
        {
            return null;
        }

        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(_plain.AsSpan(offset, 4)) & 0x7FFFFFFF;
        if (payloadLen <= 0)
        {
            return 0;
        }

        var packetLen = 4 + payloadLen;
        return avail < packetLen ? null : packetLen;
    }
}
