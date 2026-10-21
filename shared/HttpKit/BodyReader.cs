namespace NetPI.HttpKit;

/// <summary>
/// Bounded reads of HTTP bodies. Shared SOURCE compiled into each plugin that downloads (plugins do not share assemblies).
/// </summary>
internal static class BodyReader
{
    /// <summary>
    /// Reads the body in chunks and stops at <paramref name="max"/> bytes: <c>cut</c> is true only when the body is longer
    /// than <paramref name="max"/>. <c>ReadAsByteArrayAsync</c> has no such bound: a chunked response, or a ~1000:1
    /// compressed one, allocates up to HttpClient's own cap in the single host process before it stops.
    /// </summary>
    internal static async Task<(byte[] Bytes, bool Cut)> ReadCappedAsync(HttpContent content, long max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return (ms.ToArray(), false);
            var room = max - ms.Length;
            if (n > room)
            {
                ms.Write(buffer, 0, (int)room);
                return (ms.ToArray(), true);
            }
            ms.Write(buffer, 0, n);
        }
    }
}
