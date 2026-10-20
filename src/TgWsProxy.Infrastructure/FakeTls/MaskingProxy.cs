using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain.Abstractions;

namespace TgWsProxy.Infrastructure.FakeTls;

/// <summary>
/// Перенаправляет соединение, не прошедшее Fake TLS, на домен маскировки
/// (порт upstream <c>proxy_to_masking_domain</c>): прокси ведёт себя как обычный TLS-сайт.
/// </summary>
internal sealed class MaskingProxy(IProxyStats stats, ILogger<MaskingProxy> logger)
{
    /// <summary>Таймаут установки соединения с доменом маскировки.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Размер буфера пересылки.</summary>
    private const int BufferSize = 16384;

    /// <summary>Порт домена маскировки.</summary>
    private const int MaskingPort = 443;

    /// <summary>
    /// Устанавливает соединение с <paramref name="domain"/>:443, отправляет уже прочитанные
    /// байты клиента и ретранслирует трафик в обе стороны до закрытия.
    /// </summary>
    /// <param name="client">Клиентский поток.</param>
    /// <param name="initialData">Уже прочитанные от клиента байты (ClientHello).</param>
    /// <param name="domain">Домен маскировки.</param>
    /// <param name="scope">Метка сессии для логов.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task RelayAsync(
        Stream client,
        byte[] initialData,
        string domain,
        string scope,
        CancellationToken cancellationToken)
    {
        Socket? socket = null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                logger.LogWarning("[{Scope}] masking: {Domain} did not resolve", scope, domain);
                return;
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);

            socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(addresses[0], MaskingPort), connectCts.Token)
                .ConfigureAwait(false);
            socket.NoDelay = true;
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            logger.LogWarning("[{Scope}] masking: cannot connect to {Domain}:443: {Error}", scope, domain, ex.Message);
            return;
        }

        logger.LogDebug("[{Scope}] masking -> {Domain}:443", scope, domain);
        stats.IncConnectionsMasked();

        await using var upstream = new NetworkStream(socket, ownsSocket: true);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        var up = Task.Run(async () =>
        {
            var buffer = new byte[BufferSize];
            try
            {
                if (initialData.Length > 0)
                {
                    await upstream.WriteAsync(initialData, ct).ConfigureAwait(false);
                }

                while (!ct.IsCancellationRequested)
                {
                    var read = await client.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await upstream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "[{Scope}] masking client->domain ended", scope);
            }
            finally
            {
                Cancel(linkedCts);
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            var buffer = new byte[BufferSize];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var read = await upstream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await client.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "[{Scope}] masking domain->client ended", scope);
            }
            finally
            {
                Cancel(linkedCts);
            }
        }, ct);

        await Task.WhenAny(up, down).ConfigureAwait(false);
        Cancel(linkedCts);

        try
        {
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch
        {
            // Ожидаемое завершение ретрансляции.
        }
    }

    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Источник уже освобождён.
        }
    }
}
