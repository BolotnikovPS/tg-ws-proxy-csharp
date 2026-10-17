#nullable enable

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;

namespace TgWsProxy.Infrastructure.Wire;

/// <summary>
/// Обертка над AES-CTR для пошагового шифрования/дешифрования. При каждом вызове Update() counter автоматически инкрементируется.
/// </summary>
public class IncrementalCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _transform;
    private readonly byte[] _counter;
    private int _offsetInBlock;
    private readonly byte[] _keystreamBlock;

    public IncrementalCipher(byte[] key, byte[] iv)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        _transform = _aes.CreateEncryptor();
        _counter = [.. iv];
        _offsetInBlock = 0;
        _keystreamBlock = new byte[16];
        // Pre-compute first keystream block
        _transform.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
    }

    /// <summary>
    /// Шифрует/дешифрует данные (AES-CTR симметричен). Автоматически инкрементирует counter после обработки.
    /// </summary>
    public virtual byte[] Update(byte[] data)
    {
        var result = new byte[data.Length];
        UpdateImpl(data.AsSpan(), result.AsSpan());
        return result;
    }

    /// <summary>
    /// Выполняет Update inplace (без аллокации нового буфера) для оптимизации.
    /// </summary>
    public void UpdateInPlace(Span<byte> data) => UpdateImpl(data, data);

    /// <summary>
    /// Шифрует/дешифрует данные в заранее выделенный буфер (без аллокаций).
    /// </summary>
    /// <param name="input">Входные данные.</param>
    /// <param name="output">Буфер результата не короче <paramref name="input"/>.</param>
    public void UpdateInto(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output buffer is smaller than input", nameof(output));
        }

        UpdateImpl(input, output);
    }

    /// <summary>
    /// Продвигает поток на указанное число байт без выдачи результата (аналог
    /// <c>decryptor.update(ZERO_64)</c> в upstream, но без аллокации буфера).
    /// </summary>
    /// <param name="length">Количество байт, которые нужно пропустить.</param>
    public void Skip(long length)
    {
        var pos = 0L;
        while (pos < length)
        {
            var take = (int)Math.Min(16 - _offsetInBlock, length - pos);
            pos += take;
            _offsetInBlock += take;

            if (_offsetInBlock == 16)
            {
                IncrementCounter();
                _transform.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
                _offsetInBlock = 0;
            }
        }
    }

    private void UpdateImpl(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var pos = 0;

        // Быстрый путь: пока поток выровнен по границе блока, XOR-им по 16 байт векторно.
        if (_offsetInBlock == 0 && Vector128.IsHardwareAccelerated)
        {
            while (input.Length - pos >= 16)
            {
                var block = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(input.Slice(pos, 16)));
                var key = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_keystreamBlock));
                Vector128.Xor(block, key)
                    .StoreUnsafe(ref MemoryMarshal.GetReference(output.Slice(pos, 16)));

                pos += 16;
                IncrementCounter();
                _transform.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
            }
        }

        while (pos < input.Length)
        {
            var take = Math.Min(16 - _offsetInBlock, input.Length - pos);
            for (var i = 0; i < take; i++)
            {
                output[pos + i] = (byte)(input[pos + i] ^ _keystreamBlock[_offsetInBlock + i]);
            }

            pos += take;
            _offsetInBlock += take;

            // Если дошли до конца блока, инкрементируем counter и вычисляем следующий keystream
            if (_offsetInBlock == 16)
            {
                IncrementCounter();
                _transform.TransformBlock(_counter, 0, 16, _keystreamBlock, 0);
                _offsetInBlock = 0;
            }
        }
    }

    public void Dispose()
    {
        _transform.Dispose();
        _aes.Dispose();
    }

    private void IncrementCounter()
    {
        // Big-endian increment
        for (var i = 15; i >= 0; i--)
        {
            if (++_counter[i] != 0)
            {
                break;
            }
        }
    }
}
