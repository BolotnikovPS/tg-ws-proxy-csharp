#nullable enable

using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Security.Cryptography;
using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure;

internal sealed class TcpBridgeService(ILogger<TcpBridgeService> logger, IProxyStats stats) : ITcpBridgeService
{
    public async Task BridgeWsAsync(NetworkStream client, IRawWebSocket ws, string scope, byte[]? init, CancellationToken cancellationToken)
    {
        var splitter = init is { Length: >= 64 } ? new MtProtoMsgSplitter(init) : null;
        using var cts = new CancellationTokenSource();
        var up = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (!cts.IsCancellationRequested)
            {
                var n = await client.ReadAsync(buf, cts.Token);
                if (n == 0)
                {
                    break;
                }

                var payload = buf.AsSpan(0, n).ToArray();
                stats.AddBytesUp(payload.Length);
                if (splitter is null)
                {
                    await ws.Send(payload, cancellationToken);
                    continue;
                }

                var parts = splitter.Split(payload);
                if (parts.Count <= 1)
                {
                    await ws.Send(parts[0], cancellationToken);
                }
                else
                {
                    await ws.SendBatch(parts, cancellationToken);
                }
            }
        }, cts.Token);

        var down = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var data = await ws.Recv(cancellationToken);
                if (data is null)
                {
                    break;
                }

                stats.AddBytesDown(data.Length);
                await client.WriteAsync(data, cts.Token);
            }
        }, cts.Token);

        await Task.WhenAny(up, down);
        cts.Cancel();
        await Task.WhenAll(IgnoreTaskErrors(up, scope, "client->ws"), IgnoreTaskErrors(down, scope, "ws->client"));
        try
        {
            await ws.Close(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Failed to close WS connection", scope);
        }
    }

    public Task TcpFallbackAsync(NetworkStream client, string dst, int port, byte[] init, string scope, CancellationToken cancellationToken)
        => BridgeTcpAsync(client, dst, port, scope, init, "fallback", cancellationToken);

    public Task TcpPassthroughAsync(NetworkStream client, string dst, int port, string scope, CancellationToken cancellationToken)
        => BridgeTcpAsync(client, dst, port, scope, null, "passthrough", cancellationToken);

    /// <summary>
    /// Создает двунаправленный TCP-мост между клиентом и удаленным хостом.
    /// </summary>
    /// <param name="client">Поток клиентского подключения.</param>
    /// <param name="dst">Целевой адрес удаленного сервера.</param>
    /// <param name="port">Целевой порт удаленного сервера.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="init">Необязательный инициализационный пакет для предварительной отправки.</param>
    /// <param name="mode">Режим моста (fallback/passthrough) для логов.</param>
    private async Task BridgeTcpAsync(NetworkStream client, string dst, int port, string scope, byte[]? init, string mode, CancellationToken cancellationToken)
    {
        using var remote = new TcpClient();
        try
        {
            await remote.ConnectAsync(dst, port, cancellationToken);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            logger.LogWarning("[{Scope}] TCP {Mode} connect unavailable {Dst}:{Port} ({SocketError})", scope, mode, dst, port, ex.SocketErrorCode);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TCP {Mode} connect failed {Dst}:{Port}", scope, mode, dst, port);
            return;
        }

        remote.NoDelay = true;
        await using var rs = remote.GetStream();
        if (init is not null)
        {
            try
            {
                await rs.WriteAsync(init, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Scope}] Failed to send init packet to fallback target", scope);
                return;
            }
        }

        using var cts = new CancellationTokenSource();
        var t1 = PipeAsync(client, rs, true, cts.Token);
        var t2 = PipeAsync(rs, client, false, cts.Token);
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        await Task.WhenAll(
            IgnorePipeTaskErrors(t1, scope, $"{mode}:client->remote"),
            IgnorePipeTaskErrors(t2, scope, $"{mode}:remote->client"));
    }

    /// <summary>
    /// Копирует данные из одного потока в другой и учитывает переданный трафик.
    /// </summary>
    /// <param name="src">Исходный поток чтения.</param>
    /// <param name="dst">Целевой поток записи.</param>
    /// <param name="upstream">Признак направления client->remote.</param>
    /// <param name="ct">Токен отмены операции копирования.</param>
    private async Task PipeAsync(Stream src, Stream dst, bool upstream, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            var n = await src.ReadAsync(buf, ct);
            if (n == 0)
            {
                break;
            }

            if (upstream)
            {
                stats.AddBytesUp(n);
            }
            else
            {
                stats.AddBytesDown(n);
            }

            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    /// <summary>
    /// Ожидает завершение задачи моста и подавляет ожидаемые ошибки отключения.
    /// </summary>
    /// <param name="task">Задача канала моста.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="channel">Логическое имя канала передачи.</param>
    private async Task IgnoreTaskErrors(Task task, string scope, string channel)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} canceled", scope, channel);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se &&
                                     se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} connection closed by peer", scope, channel);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            logger.LogDebug("[{Scope}] Bridge {Channel} connection closed by peer", scope, channel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] Bridge channel failed: {Channel}", scope, channel);
        }
    }

    /// <summary>
    /// Ожидает завершение TCP-задачи и мягко обрабатывает типовые ошибки разрыва.
    /// </summary>
    /// <param name="task">Задача канала TCP fallback/passthrough.</param>
    /// <param name="scope">Идентификатор скоупа для логирования.</param>
    /// <param name="channel">Логическое имя канала передачи.</param>
    private async Task IgnorePipeTaskErrors(Task task, string scope, string channel)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[{Scope}] TCP fallback {Channel} canceled", scope, channel);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Peer closed/reset the TCP connection; this is expected during disconnects.
            logger.LogDebug("[{Scope}] TCP fallback {Channel} connection reset by peer", scope, channel);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            logger.LogDebug("[{Scope}] TCP fallback {Channel} connection reset by peer", scope, channel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Scope}] TCP fallback channel failed: {Channel}", scope, channel);
        }
    }

    private sealed class MtProtoMsgSplitter(byte[] init)
    {
        private readonly byte[] key = init.AsSpan(8, 32).ToArray();
        private readonly byte[] iv = init.AsSpan(40, 16).ToArray();
        private long streamOffset = 64; // skip init packet keystream like Python implementation

        /// <summary>
        /// Расшифровывает chunk и, при наличии нескольких MTProto-сообщений, разбивает его на части.
        /// </summary>
        /// <param name="chunk">Шифрованный chunk из входящего потока.</param>
        /// <returns>Список chunk-частей для последовательной отправки.</returns>
        public IReadOnlyList<byte[]> Split(byte[] chunk)
        {
            var plain = DecryptChunk(chunk);
            var boundaries = new List<int>();
            var pos = 0;
            while (pos < plain.Length)
            {
                int msgLen;
                var first = plain[pos];
                if (first == 0x7f)
                {
                    if (pos + 4 > plain.Length)
                    {
                        break;
                    }

                    msgLen = (plain[pos + 1] | (plain[pos + 2] << 8) | (plain[pos + 3] << 16)) * 4;
                    pos += 4;
                }
                else
                {
                    msgLen = first * 4;
                    pos += 1;
                }

                if (msgLen == 0 || pos + msgLen > plain.Length)
                {
                    break;
                }

                pos += msgLen;
                boundaries.Add(pos);
            }

            if (boundaries.Count <= 1)
            {
                return [chunk];
            }

            var parts = new List<byte[]>(boundaries.Count + 1);
            var prev = 0;
            foreach (var b in boundaries)
            {
                parts.Add(chunk[prev..b]);
                prev = b;
            }
            if (prev < chunk.Length)
            {
                parts.Add(chunk[prev..]);
            }

            return parts;
        }

        /// <summary>
        /// Выполняет дешифрование фрагмента MTProto потоком AES-CTR с учетом смещения.
        /// </summary>
        /// <param name="chunk">Шифрованный фрагмент входящего потока.</param>
        /// <returns>Дешифрованные байты фрагмента.</returns>
        private byte[] DecryptChunk(byte[] chunk)
        {
            var ks = Keystream(streamOffset, chunk.Length);
            var plain = new byte[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                plain[i] = (byte)(chunk[i] ^ ks[i]);
            }
            streamOffset += chunk.Length;
            return plain;
        }

        /// <summary>
        /// Генерирует последовательность ключевого потока AES-CTR для указанного диапазона.
        /// </summary>
        /// <param name="offset">Смещение в шифропотоке.</param>
        /// <param name="len">Требуемая длина ключевого потока.</param>
        /// <returns>Ключевой поток заданной длины.</returns>
        private byte[] Keystream(long offset, int len)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            using var enc = aes.CreateEncryptor();

            var output = new byte[len];
            var blockIndex = offset / 16;
            var blockOffset = (int)(offset % 16);
            var written = 0;
            while (written < len)
            {
                var counter = CounterAt(blockIndex);
                var block = new byte[16];
                enc.TransformBlock(counter, 0, 16, block, 0);
                var take = Math.Min(16 - blockOffset, len - written);
                Buffer.BlockCopy(block, blockOffset, output, written, take);
                written += take;
                blockIndex++;
                blockOffset = 0;
            }

            return output;
        }

        /// <summary>
        /// Вычисляет значение счетчика CTR для заданного индекса блока.
        /// </summary>
        /// <param name="blockIndex">Индекс 16-байтового блока в потоке.</param>
        /// <returns>Буфер счетчика для шифрования блока.</returns>
        private byte[] CounterAt(long blockIndex)
        {
            var ctr = iv.ToArray();
            var carry = (ulong)blockIndex;
            for (var b = 15; b >= 0 && carry != 0; b--)
            {
                var sum = ctr[b] + (carry & 0xFF);
                ctr[b] = (byte)sum;
                carry = (carry >> 8) + (sum >> 8);
            }
            return ctr;
        }
    }
}
