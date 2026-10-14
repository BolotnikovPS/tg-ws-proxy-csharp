#nullable enable

using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Logic.FakeTls;
using TgWsProxy.Application.Logic.Models;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Читает начало клиентского соединения так же, как upstream <c>_read_client_init</c>:
/// опциональный заголовок PROXY protocol v1, затем Fake TLS ClientHello либо обычный init.
/// </summary>
internal sealed class ClientInitReader(Config config, ILogger<ClientInitReader> logger)
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
    /// <param name="secret">Секрет прокси в байтах.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат разбора; при <see cref="ClientInitStatus.Closed"/> обработку нужно прекратить.</returns>
    public async Task<ClientInitResult> ReadAsync(
        Stream stream,
        string scope,
        byte[] secret,
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
            return await ReadFakeTlsAsync(stream, (byte)firstByte, masking, label, secret, cancellationToken)
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
    /// </summary>
    private async Task<ClientInitResult> ReadFakeTlsAsync(
        Stream stream,
        byte firstByte,
        string masking,
        string label,
        byte[] secret,
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

        var info = FakeTlsCodec.VerifyClientHello(clientHello, secret);
        if (info is null)
        {
            logger.LogDebug(
                "[{Scope}] Fake TLS verify failed (size={Size} rec={RecordLen}) -> masking",
                label, clientHello.Length, recordLength);
            return new ClientInitResult(ClientInitStatus.Masking, null, null, label, clientHello);
        }

        var verified = info.Value;
        logger.LogDebug("[{Scope}] Fake TLS handshake ok (ts={Timestamp})", label, verified.Timestamp);

        var serverHello = FakeTlsCodec.BuildServerHello(secret, verified.ClientRandom, verified.SessionId);
        await stream.WriteAsync(serverHello, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var tlsStream = new FakeTlsStream(stream);
        try
        {
            var init = await tlsStream.ReadExact(Protocol.HandshakeLen, StepTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new ClientInitResult(ClientInitStatus.Ok, init, tlsStream, label, null);
        }
        catch (EndOfStreamException)
        {
            logger.LogDebug("[{Scope}] incomplete obfs2 init inside TLS", label);
            return Closed(label);
        }
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
