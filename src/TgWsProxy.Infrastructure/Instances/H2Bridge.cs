using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Domain.Exceptions;
using TgWsProxy.Infrastructure.Wire;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Связывает native-соединение клиента с HTTP/2-каналом Cloudflare для media-трафика
/// (порт upstream <c>bridge_h2</c> из <c>proxy/cf_h2.py</c>): читает MTProto-пакеты
/// из клиентского потока, отправляет их в канал и возвращает ответы обратно.
/// </summary>
internal sealed class H2Bridge(IProxyStats stats, IPeerStatsRegistry peerStats, ILogger<H2Bridge> logger)
{
    /// <summary>
    /// Гоняет трафик между клиентом и каналом, пока не завершится любая из сторон.
    /// </summary>
    /// <param name="client">Поток клиента (возможно, обёрнутый в Fake TLS).</param>
    /// <param name="channel">HTTP/2-канал к Cloudflare.</param>
    /// <param name="crypto">Криптоконтекст сессии.</param>
    /// <param name="protoTag">4 байта proto tag текущей сессии.</param>
    /// <param name="scope">Метка сессии для логов.</param>
    /// <param name="peer">Адрес клиента без порта для статистики по IP (может быть <see langword="null"/>).</param>
    /// <param name="tokenId">Идентификатор токена доступа сессии (может быть <see langword="null"/>, F-9.2).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task BridgeAsync(
        Stream client,
        ICfH2Channel channel,
        CryptoCtx crypto,
        byte[] protoTag,
        string scope,
        IPAddress? peer,
        string? tokenId,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        var upload = UploadAsync(client, channel, crypto, protoTag, peer, tokenId, token);
        var download = DownloadAsync(client, channel, crypto, protoTag, peer, tokenId, token);
        var recover = channel.RecoverAsync(token);

        var completed = await Task.WhenAny(upload, download, recover).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);

        var transportError = channel.TransportError;
        if (transportError is not null)
        {
            // Upstream возвращает клиенту 4-байтный код ошибки, чтобы Telegram
            // сам переключился на другой транспорт.
            var code = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(code, transportError.Value);
            logger.LogWarning(
                "[{Scope}] H2 channel={Channel} transport error={Code}; reporting to client",
                scope,
                channel.GetType().Name,
                transportError.Value);

            try
            {
                await WriteNativeAsync(client, crypto, H2PacketCodec.EncodeReply(code, protoTag), token)
                    .ConfigureAwait(false);
                stats.AddBytesDown(code.Length);
                peerStats.AddBytesDown(peer, tokenId, code.Length);
            }
            catch (NativeDeliveryException ex)
            {
                logger.LogDebug("[{Scope}] H2 native closed while reporting error: {Reason}", scope, ex.Message);
            }
        }

        await DescribeAsync(upload, "upload", scope).ConfigureAwait(false);
        await DescribeAsync(download, "download", scope).ConfigureAwait(false);
        await DescribeAsync(recover, "recover", scope).ConfigureAwait(false);
    }

    private async Task UploadAsync(
        Stream client,
        ICfH2Channel channel,
        CryptoCtx crypto,
        byte[] protoTag,
        IPAddress? peer,
        string? tokenId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] body;
            bool quick;
            try
            {
                (body, quick) = await H2PacketCodec
                    .ReadPacketAsync(client, crypto.CltDecryptor, protoTag, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new NativeDeliveryException("native EOF");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                           or System.Net.Sockets.SocketException)
            {
                throw new NativeDeliveryException(ex.Message);
            }

            await channel.SendAsync(body, quick, cancellationToken).ConfigureAwait(false);
            // Глобальный счётчик учитывается внутри канала; per-IP — здесь, где известен адрес клиента.
            peerStats.AddBytesUp(peer, tokenId, body.Length);
        }
    }

    private async Task DownloadAsync(
        Stream client,
        ICfH2Channel channel,
        CryptoCtx crypto,
        byte[] protoTag,
        IPAddress? peer,
        string? tokenId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (channel.Closed)
            {
                throw new NativeDeliveryException("H2 channel closed before native response");
            }

            try
            {
                await WriteNativeAsync(client, crypto, H2PacketCodec.EncodeReply(body, protoTag), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Снимаем native backpressure даже при ошибке записи: канал закрывается выше.
                channel.MarkDelivered();
            }

            stats.AddBytesDown(body.Length);
            peerStats.AddBytesDown(peer, tokenId, body.Length);
            channel.MarkProgress();
        }
    }

    private static async Task WriteNativeAsync(
        Stream client,
        CryptoCtx crypto,
        byte[] data,
        CancellationToken cancellationToken)
    {
        try
        {
            // data — свежий буфер, отданный вызывающим кодом, поэтому шифруем на месте
            // (AES-CTR — XOR с keystream) без дополнительной копии.
            crypto.CltEncryptor.UpdateInPlace(data);
            await client.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                       or System.Net.Sockets.SocketException)
        {
            throw new NativeDeliveryException(ex.Message);
        }
    }

    private async Task DescribeAsync(Task task, string stage, string scope)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (NativeDeliveryException ex)
        {
            logger.LogDebug("[{Scope}] H2 bridge {Stage} finished: {Reason}", scope, stage, ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Штатное завершение после отмены связанных задач.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            logger.LogDebug("[{Scope}] H2 bridge {Stage} ended: {Error}", scope, stage, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[{Scope}] H2 bridge {Stage} failed: {Error}", scope, stage, ex.Message);
        }
    }
}
