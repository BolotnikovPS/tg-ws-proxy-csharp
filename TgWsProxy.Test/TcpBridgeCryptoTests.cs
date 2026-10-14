using TgWsProxy.Application.Logic;
using TgWsProxy.Application.Logic.Helpers;
using TgWsProxy.Infrastructure.Instances;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты ре-шифрования в горячем пути моста (порт upstream
/// <c>plain = ctx.clt_dec.update(chunk); chunk = ctx.tg_enc.update(plain)</c>
/// из <c>proxy/bridge.py</c>).
/// </summary>
/// <remarks>
/// Проверяют именно порядок преобразований: в Telegram должен уходить шифротекст,
/// полученный из <b>расшифрованных</b> данных клиента, а не повторно зашифрованный
/// клиентский шифротекст.
/// </remarks>
public class TcpBridgeCryptoTests
{
    private static readonly byte[] CltDecKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] CltDecIv = Enumerable.Range(41, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] CltEncKey = Enumerable.Range(61, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] CltEncIv = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] TgEncKey = Enumerable.Range(121, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] TgEncIv = Enumerable.Range(161, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] TgDecKey = Enumerable.Range(181, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] TgDecIv = Enumerable.Range(221, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void TransformUpstream_ReEncryptsPlaintext_NotClientCiphertext()
    {
        var plaintext = Pattern(64);
        var clientCipher = Encrypt(CltDecKey, CltDecIv, plaintext);

        using var ctx = CreateCtx();
        var buffer = (byte[])clientCipher.Clone();
        var plainBuffer = new byte[clientCipher.Length];

        var parts = TcpBridgeService.TransformUpstream(ctx, splitter: null, buffer, plainBuffer, clientCipher.Length);

        var sent = Assert.Single(parts);
        Assert.Equal(plaintext.Length, sent.Length);

        // Разборщик определяет границы пакетов по plaintext — он должен получить расшифровку.
        Assert.Equal(plaintext, plainBuffer);

        // В Telegram уходит tg_enc(plaintext): расшифровка тем же потоком возвращает исходные данные.
        Assert.Equal(plaintext, Encrypt(TgEncKey, TgEncIv, sent));

        // Регрессия: повторное шифрование клиентского шифротекста даёт другой (неверный) результат.
        var doubleEncrypted = Encrypt(TgEncKey, TgEncIv, clientCipher);
        Assert.NotEqual(doubleEncrypted, sent);
    }

    [Fact]
    public void TransformUpstream_WithSplitter_SplitsPacketsAndKeepsPlainBoundaries()
    {
        var first = BuildAbridgedPacket(Pattern(32));
        var second = BuildAbridgedPacket(Pattern(16));
        var stream = first.Concat(second).ToArray();
        var clientCipher = Encrypt(CltDecKey, CltDecIv, stream);

        using var ctx = CreateCtx();
        var buffer = (byte[])clientCipher.Clone();
        var plainBuffer = new byte[clientCipher.Length];
        var splitter = new MsgSplitter(Protocol.ProtoAbridgedInt);

        var parts = TcpBridgeService.TransformUpstream(ctx, splitter, buffer, plainBuffer, clientCipher.Length);

        Assert.Equal(2, parts.Count);

        // Каждый WS-пакет — tg_enc соответствующего MTProto-пакета. Поток tg_enc
        // непрерывен, поэтому второй пакет расшифровывается со сдвигом на длину первого.
        Assert.Equal(first, Encrypt(TgEncKey, TgEncIv, parts[0], skip: 0));
        Assert.Equal(second, Encrypt(TgEncKey, TgEncIv, parts[1], skip: first.Length));
    }

    [Fact]
    public void TransformUpstream_AdvancesTgEncryptor_ByProcessedLength()
    {
        var plaintext = Pattern(64);
        var clientCipher = Encrypt(CltDecKey, CltDecIv, plaintext);

        using var ctx = CreateCtx();
        var buffer = (byte[])clientCipher.Clone();
        var plainBuffer = new byte[clientCipher.Length];

        TcpBridgeService.TransformUpstream(ctx, splitter: null, buffer, plainBuffer, clientCipher.Length);

        // Поток tg_enc должен сдвинуться ровно на длину обработанных данных:
        // следующий фрагмент шифруется продолжением того же keystream.
        var next = Pattern(16);
        var nextEncrypted = ctx.TgEncryptor.Update(next);

        using var verifier = new IncrementalCipher(TgEncKey, TgEncIv);
        verifier.Skip(plaintext.Length);
        Assert.Equal(next, verifier.Update(nextEncrypted));
    }

    [Fact]
    public void TransformUpstream_WithoutSplitter_ReturnsWholeFragment()
    {
        var plaintext = Pattern(48);
        var clientCipher = Encrypt(CltDecKey, CltDecIv, plaintext);

        using var ctx = CreateCtx();
        var buffer = (byte[])clientCipher.Clone();
        var plainBuffer = new byte[clientCipher.Length];

        var parts = TcpBridgeService.TransformUpstream(ctx, splitter: null, buffer, plainBuffer, clientCipher.Length);

        var sent = Assert.Single(parts);
        Assert.Equal(plaintext, Encrypt(TgEncKey, TgEncIv, sent));
    }

    private static CryptoCtx CreateCtx() => new(
        new IncrementalCipher(CltDecKey, CltDecIv),
        new IncrementalCipher(CltEncKey, CltEncIv),
        new IncrementalCipher(TgEncKey, TgEncIv),
        new IncrementalCipher(TgDecKey, TgDecIv));

    /// <summary>Шифрует данные отдельным AES-CTR потоком (AES-CTR симметричен).</summary>
    /// <param name="key">Ключ потока.</param>
    /// <param name="iv">Начальный counter потока.</param>
    /// <param name="data">Данные для обработки.</param>
    /// <param name="skip">Сколько байт потока пропустить перед обработкой.</param>
    private static byte[] Encrypt(byte[] key, byte[] iv, byte[] data, int skip = 0)
    {
        using var cipher = new IncrementalCipher(key, iv);
        if (skip > 0)
        {
            cipher.Skip(skip);
        }

        return cipher.Update(data);
    }

    /// <summary>Собирает MTProto-пакет abridged: 1-байтовый заголовок (words) + payload.</summary>
    private static byte[] BuildAbridgedPacket(byte[] payload)
    {
        Assert.True(payload.Length % 4 == 0 && payload.Length / 4 < 0x7f);
        return new byte[] { (byte)(payload.Length / 4) }.Concat(payload).ToArray();
    }

    private static byte[] Pattern(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 7) + 3)).ToArray();
}
