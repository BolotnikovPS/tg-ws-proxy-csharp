namespace TgWsProxy.Infrastructure.IO;

public static class IoUtil
{
    public static async Task<byte[]> ReadExact(this Stream stream, int len, CancellationToken ct)
    {
        var buf = new byte[len];
        await stream.ReadExact(buf.AsMemory(), ct);
        return buf;
    }

    /// <summary>
    /// Читает ровно <paramref name="buffer"/> байт в заранее выделенный буфер без
    /// дополнительной аллокации (порт горячего пути <c>RawWebSocket.ReadFrame</c>,
    /// который раньше выделял отдельный массив под каждый заголовок/маску).
    /// </summary>
    public static async Task ReadExact(this Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var pos = 0;
        var len = buffer.Length;
        while (pos < len)
        {
            var n = await stream.ReadAsync(buffer.Slice(pos, len - pos), ct);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            pos += n;
        }
    }

    public static async Task<byte[]> ReadExact(this Stream stream, int len, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await stream.ReadExact(len, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out while reading {len} bytes");
        }
    }
}
