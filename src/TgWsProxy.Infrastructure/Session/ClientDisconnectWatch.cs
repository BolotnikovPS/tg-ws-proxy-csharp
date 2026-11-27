using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using static TgWsProxy.Domain.AppConstants.Server;

namespace TgWsProxy.Infrastructure.Session;

/// <summary>
/// Отменяет сессию, когда клиент ушёл, пока шёл дозвон в fallback
/// (порт upstream <c>client_setup</c> из <c>event_loop.py</c>).
/// </summary>
/// <remarks>
/// До v1.12.0 клиент, отключившийся во время дозвона (TLS-рукопожатие к CF-домену,
/// открытие воркера, TCP-fallback), оставлял за собой висящую попытку до истечения её
/// таймаута: результат уже некому отдать. Upstream отслеживает EOF через протокол
/// соединения; в C# сокет опрашивается напрямую — <see cref="Socket.Poll(int, SelectMode)"/>
/// ничего не вычитывает, поэтому не мешает последующему чтению сессии.
/// </remarks>
internal sealed class ClientDisconnectWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    private ClientDisconnectWatch(
        Socket client,
        CancellationTokenSource session,
        string scope,
        ILogger logger)
    {
        loop = WatchAsync(client, session, scope, logger, stop.Token);
    }

    /// <summary>
    /// Начинает наблюдение за клиентским сокетом; отмена <paramref name="session"/>
    /// происходит только при обнаружении EOF, то есть когда данных от клиента уже не будет.
    /// </summary>
    /// <param name="client">Клиентский сокет сессии.</param>
    /// <param name="session">Токен сессии, который нужно отменить при уходе клиента.</param>
    /// <param name="scope">Метка сессии для логов.</param>
    /// <param name="logger">Логгер.</param>
    /// <returns>Наблюдатель, останавливаемый освобождением.</returns>
    public static ClientDisconnectWatch Start(
        Socket client,
        CancellationTokenSource session,
        string scope,
        ILogger logger)
        => new(client, session, scope, logger);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка наблюдения.
        }

        stop.Dispose();
    }

    private static async Task WatchAsync(
        Socket client,
        CancellationTokenSource session,
        string scope,
        ILogger logger,
        CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ClientDisconnectPollMs), stop).ConfigureAwait(false);

                // Сокет мог быть уже освобождён завершающейся сессией — тогда наблюдать нечего.
                if (IsSocketGone(client))
                {
                    return;
                }

                // Читаемость без доступных байтов = FIN от клиента; доступные байты — обычные данные.
                if (!client.Poll(0, SelectMode.SelectRead) || client.Available > 0)
                {
                    continue;
                }

                logger.LogDebug("[{Scope}] client left during dial; canceling session", scope);

                // Сессия уже завершилась — отменять нечего.
                if (!session.IsCancellationRequested)
                {
                    await session.CancelAsync().ConfigureAwait(false);
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Освобождение наблюдателя.
        }
        catch (ObjectDisposedException)
        {
            // Сокет закрыт — сессия завершается.
        }
        catch (SocketException)
        {
            // Сокет в нерабочем состоянии; штатное завершение сессии обработает это само.
        }
    }

    private static bool IsSocketGone(Socket client)
    {
        try
        {
            var handle = client.SafeHandle;
            return handle.IsClosed || handle.IsInvalid;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
