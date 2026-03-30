namespace TgWsProxy.Application;

public static class IoUtil
{
    public static async Task<byte[]> ReadExact(Stream stream, int len, CancellationToken ct)
    {
        var buf = new byte[len];
        var pos = 0;
        while (pos < len)
        {
            var n = await stream.ReadAsync(buf.AsMemory(pos, len - pos), ct);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            pos += n;
        }
        return buf;
    }

    public static async Task<byte[]> ReadExact(Stream stream, int len, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await ReadExact(stream, len, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out while reading {len} bytes");
        }
    }
}
