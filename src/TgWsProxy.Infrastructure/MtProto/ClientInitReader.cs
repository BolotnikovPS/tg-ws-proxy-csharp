using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.FakeTls;
using TgWsProxy.Infrastructure.IO;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Читает начало клиентского соединения так же, как upstream <c>_read_client_init</c>:
/// опциональный заголовок PROXY protocol v1, затем Fake TLS ClientHello либо обычный init.
/// </summary>
internal sealed class ClientInitReader(
    Config config,
    IProxyTokenRegistry tokens,
    ILogger<ClientInitReader> logger)
{
    /// <summary>Таймаут ожидания каждого шага чтения (секунды).</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Максимальная длина строки PROXY protocol v1 (спецификация: 107 байт).</summary>
    private const int MaxProxyHeaderLength = 108;

    /// <summary>
    /// Читает init-пакет клиента, при необходимости поднимая Fake TLS.
    /// </summary>
    /// <param name="stream">Клиентский поток (сырой TCP).</param>
    /// <param name="scope">Исходная метка сессии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат разбора; при <see cref="ClientInitStatus.Closed"/> обработку нужно прекратить.</returns>
    public async Task<ClientInitResult> ReadAsync(
        Stream stream,
        string scope,
        CancellationToken cancellationToken)
    {
        var label = scope;

        if (config.ProxyProtocol)
        {
            label = await ReadProxyHeaderAsync(stream, label, cancellationToken).ConfigureAwait(false);
        }

        var masking = config.FakeTlsDomain;

        int firstByte;
        try
        {
            firstByte = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] client disconnected before handshake", label);
            return Closed(label);
        }

        if (firstByte < 0)
        {
            logger.LogDebug("[{Scope}] client disconnected before handshake", label);
            return Closed(label);
        }

        if (firstByte == FakeTlsCodec.RecordHandshake && !string.IsNullOrEmpty(masking))
        {
            return await ReadFakeTlsAsync(stream, (byte)firstByte, masking, label, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(masking))
        {
            logger.LogDebug("[{Scope}] non-TLS byte 0x{Byte:X2} -> HTTP redirect", label, firstByte);
            return new ClientInitResult(ClientInitStatus.HttpRedirect, null, null, label, [(byte)firstByte]);
        }

        try
        {
            var rest = await stream
                .ReadExact(Protocol.HandshakeLen - 1, StepTimeout, cancellationToken)
                .ConfigureAwait(false);
            var init = new byte[Protocol.HandshakeLen];
            init[0] = (byte)firstByte;
            rest.CopyTo(init, 1);
            return new ClientInitResult(ClientInitStatus.Ok, init, stream, label, null);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] client disconnected before handshake", label);
            return Closed(label);
        }
    }

    /// <summary>
    /// Разбирает Fake TLS ClientHello: проверяет подпись, отвечает ServerHello и читает init внутри TLS.
    /// Подпись ClientHello считается секретом токена (F-8.4), поэтому токен выбирается здесь же: без
    /// подходящего секрета невозможно ни проверить ClientHello, ни собрать корректный ServerHello.
    /// </summary>
    private async Task<ClientInitResult> ReadFakeTlsAsync(
        Stream stream,
        byte firstByte,
        string masking,
        string label,
        CancellationToken cancellationToken)
    {
        byte[] headerRest;
        try
        {
            headerRest = await stream.ReadExact(4, StepTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] incomplete TLS record header", label);
            return Closed(label);
        }

        var recordLength = (headerRest[2] << 8) | headerRest[3];
        byte[] recordBody;
        try
        {
            recordBody = await stream.ReadExact(recordLength, StepTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] incomplete TLS record body", label);
            return Closed(label);
        }

        var clientHello = new byte[5 + recordBody.Length];
        clientHello[0] = firstByte;
        headerRest.CopyTo(clientHello, 1);
        recordBody.CopyTo(clientHello, 5);

        // Перебор секретов: HMAC над ClientHello — единственный способ понять, чей это токен.
        var verified = SelectToken(clientHello);
        if (verified is null)
        {
            logger.LogDebug(
                "[{Scope}] Fake TLS verify failed (size={Size} rec={RecordLen}) -> masking",
                label, clientHello.Length, recordLength);
            return new ClientInitResult(ClientInitStatus.Masking, null, null, label, clientHello);
        }

        var (info, token) = verified.Value;
        logger.LogDebug(
            "[{Scope}] Fake TLS handshake ok (ts={Timestamp} token={TokenName})",
            label, info.Timestamp, token.Name);

        var serverHello = FakeTlsCodec.BuildServerHello(token.Secret, info.ClientRandom, info.SessionId);
        await stream.WriteAsync(serverHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var tlsStream = new FakeTlsStream(stream);
        try
        {
            var init = await tlsStream.ReadExact(Protocol.HandshakeLen, StepTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new ClientInitResult(ClientInitStatus.Ok, init, tlsStream, label, null, token.Id);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] incomplete obfs2 init inside TLS", label);
            return Closed(label);
        }
    }

    /// <summary>
    /// Ищет токен, чей секрет подписал Fake TLS ClientHello (F-8.4). Порядок тот же, что и при
    /// обычном handshake: от недавно использованных к давним.
    /// </summary>
    /// <param name="clientHello">Полная TLS-запись с ClientHello.</param>
    /// <returns>Пара «данные ClientHello, токен» либо <see langword="null"/>.</returns>
    private (FakeTlsCodec.ClientHelloInfo Info, ProxyTokenEntry Token)? SelectToken(byte[] clientHello)
    {
        var candidates = tokens.HandshakeOrder;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Secret.Length == 0)
            {
                continue;
            }

            if (FakeTlsCodec.VerifyClientHello(clientHello, candidate.Secret) is { } info)
            {
                return (info, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Читает строку заголовка PROXY protocol v1 и извлекает из неё реальный адрес клиента.
    /// </summary>
    private async Task<string> ReadProxyHeaderAsync(Stream stream, string label, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxProxyHeaderLength];
        var length = 0;

        try
        {
            while (length < buffer.Length)
            {
                var b = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                if (b < 0)
                {
                    break;
                }

                buffer[length++] = (byte)b;
                if (b == '\n')
                {
                    break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] disconnected during PROXY header", label);
            return label;
        }

        var text = System.Text.Encoding.ASCII.GetString(buffer, 0, length).Trim();
        if (!text.StartsWith("PROXY ", StringComparison.Ordinal))
        {
            logger.LogDebug("[{Scope}] expected PROXY header, got: {Header}", label, Truncate(text));
            return label;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 6)
        {
            label = $"{parts[2]}:{parts[4]}";
        }

        logger.LogDebug("[{Scope}] PROXY protocol: {Header}", label, text);
        return label;
    }

    /// <summary>
    /// Читает один байт с таймаутом; возвращает -1 при закрытии потока.
    /// </summary>
    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StepTimeout);

        try
        {
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            return read == 0 ? -1 : buffer[0];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while reading client init");
        }
    }

    private static ClientInitResult Closed(string label) => new(ClientInitStatus.Closed, null, null, label, null);

    private static string Truncate(string value) => value.Length <= 60 ? value : value[..60];
}
