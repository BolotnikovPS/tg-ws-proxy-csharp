using System.Buffers;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Собирает одно бинарное WS-сообщение из фрагментов: start frame (opcode 0x1/0x2) + 0..N
/// continuation frames (opcode 0x0) до FIN=true.
/// </summary>
/// <remarks>
/// Горячий путь — нефрагментированный кадр (FIN=true на start frame): в этом случае
/// payload возвращается как есть, без копирования и лишних аллокаций. Буфер для
/// склейки фрагментов берётся из <see cref="ArrayPool{T}"/> и возвращается в пул
/// при завершении сообщения или сбросе.
/// </remarks>
internal sealed class WsBinaryMessageAssembler
{
    private bool _inFragment;
    private byte[]? _rented;
    private int _length;

    /// <summary>
    /// Обрабатывает один WS-фрейм и возвращает готовое payload, когда накопление завершено.
    /// </summary>
    /// <param name="fin">Признак последнего кадра сообщения.</param>
    /// <param name="opcode">Код операции кадра.</param>
    /// <param name="payload">Payload кадра (владение переходит сборщику).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Собранное сообщение либо <c>null</c>, если сообщение ещё не завершено.</returns>
    public Task<byte[]?> OnFrame(bool fin, byte opcode, byte[] payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (opcode is 0x1 or 0x2)
        {
            if (_inFragment)
            {
                throw new IOException("Unexpected WS data frame while a fragmented message is in progress");
            }

            if (fin)
            {
                // Нефрагментированное сообщение: отдаём кадр напрямую.
                return Task.FromResult<byte[]?>(payload);
            }

            _inFragment = true;
            Append(payload);
            return Task.FromResult<byte[]?>(null);
        }

        if (opcode == 0x0)
        {
            if (!_inFragment)
            {
                throw new IOException("Unexpected WS continuation frame without a started fragment");
            }

            Append(payload);
            if (!fin)
            {
                return Task.FromResult<byte[]?>(null);
            }

            _inFragment = false;
            return Task.FromResult<byte[]?>(Complete());
        }

        // Not handled here (ping/pong/close/text) - keep RawWebSocket handling separate.
        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// Сбрасывает незавершённое сообщение и возвращает арендованный буфер в пул.
    /// </summary>
    public void Reset()
    {
        _inFragment = false;
        _length = 0;

        var rented = _rented;
        _rented = null;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Append(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var rented = _rented;
        if (rented is null)
        {
            rented = ArrayPool<byte>.Shared.Rent(payload.Length);
            _rented = rented;
        }
        else if (_length + payload.Length > rented.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(_length + payload.Length);
            Buffer.BlockCopy(rented, 0, grown, 0, _length);
            ArrayPool<byte>.Shared.Return(rented);
            rented = grown;
            _rented = grown;
        }

        Buffer.BlockCopy(payload, 0, rented, _length, payload.Length);
        _length += payload.Length;
    }

    private byte[] Complete()
    {
        var rented = _rented;
        _rented = null;
        var length = _length;
        _length = 0;

        if (rented is null)
        {
            return [];
        }

        if (length == rented.Length)
        {
            // Точное попадание в размер аренды: буфер можно отдать без копии.
            return rented;
        }

        var result = new byte[length];
        Buffer.BlockCopy(rented, 0, result, 0, length);
        ArrayPool<byte>.Shared.Return(rented);
        return result;
    }
}
