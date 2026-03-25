#nullable enable

namespace TgWsProxy.Infrastructure;

/// <summary>
/// Собирает одно бинарное WS-сообщение из фрагментов: start frame (opcode 0x1/0x2) + 0..N
/// continuation frames (opcode 0x0) до FIN=true.
/// </summary>
internal sealed class WsBinaryMessageAssembler
{
    private bool _inFragment;
    private readonly MemoryStream _buffer = new();

    /// <summary>
    /// Обрабатывает один WS-фрейм и возвращает готовое payload, когда накопление завершено.
    /// </summary>
    public byte[]? OnFrame(bool fin, byte opcode, byte[] payload)
    {
        if (opcode is 0x1 or 0x2)
        {
            if (_inFragment)
            {
                throw new IOException("Unexpected WS data frame while a fragmented message is in progress");
            }

            _inFragment = !fin;
            _buffer.SetLength(0);
            _buffer.Write(payload, 0, payload.Length);

            if (fin)
            {
                return _buffer.ToArray();
            }

            return null;
        }

        if (opcode == 0x0)
        {
            if (!_inFragment)
            {
                throw new IOException("Unexpected WS continuation frame without a started fragment");
            }

            _buffer.Write(payload, 0, payload.Length);
            if (fin)
            {
                _inFragment = false;
                return _buffer.ToArray();
            }

            return null;
        }

        // Not handled here (ping/pong/close/text) - keep RawWebSocket handling separate.
        return null;
    }
}
