namespace TgWsProxy.Infrastructure.Wire;

/// <summary>
/// Набор из четырёх AES-CTR потоков, используемых для ре-шифрования трафика
/// между клиентом и Telegram (аналог upstream <c>CryptoCtx</c>).
/// </summary>
public sealed class CryptoCtx(
    IncrementalCipher cltDecryptor,
    IncrementalCipher cltEncryptor,
    IncrementalCipher tgEncryptor,
    IncrementalCipher tgDecryptor) : IDisposable
{
    /// <summary>Поток расшифровки данных, приходящих от клиента.</summary>
    public IncrementalCipher CltDecryptor { get; } = cltDecryptor;

    /// <summary>Поток шифрования данных, отправляемых клиенту.</summary>
    public IncrementalCipher CltEncryptor { get; } = cltEncryptor;

    /// <summary>Поток шифрования данных, отправляемых в Telegram.</summary>
    public IncrementalCipher TgEncryptor { get; } = tgEncryptor;

    /// <summary>Поток расшифровки данных, приходящих из Telegram.</summary>
    public IncrementalCipher TgDecryptor { get; } = tgDecryptor;

    /// <inheritdoc />
    public void Dispose()
    {
        CltDecryptor.Dispose();
        CltEncryptor.Dispose();
        TgEncryptor.Dispose();
        TgDecryptor.Dispose();
    }
}
