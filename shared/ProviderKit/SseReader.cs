// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Runtime.CompilerServices;
using System.Text;

namespace NetPI.Providers.Kit;

/// <summary>One server-sent event (<c>event:</c> name, joined <c>data:</c> lines, <c>id:</c>).</summary>
internal readonly record struct SseEvent(string? Event, string Data, string? Id = null)
{
    /// <summary>OpenAI-style end-of-stream sentinel.</summary>
    public bool IsDone => Data == "[DONE]";
}

/// <summary>Minimal, allocation-light SSE reader (spec-compliant plus a few tolerances for sloppy servers).</summary>
internal static class SseReader
{
    /// <summary>The longest one SSE line may be (in chars). A server that does not end its line is not streaming,
    /// and reading it unbounded would fill the process's memory: past the cap the stream is a provider error.</summary>
    public const int MaxLineLength = 16 * 1024 * 1024;

    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default,
        string? provider = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
        string? evt = null, id = null;
        var data = new StringBuilder();
        var hasData = false;
        var buf = new char[16 * 1024];
        int pos = 0, len = 0;
        var skipLf = false;   // the last line ended in \r: a \n that follows is part of that ending
        var lineBuf = new StringBuilder();

        while (true)
        {
            var next = await NextLine();
            if (next is null) break;
            var line = next;

            if (line.Length == 0)
            {
                if (hasData) yield return new SseEvent(evt, data.ToString(), id);
                evt = null; data.Clear(); hasData = false;
                continue;
            }
            if (line[0] == ':') continue; // comment / keep-alive

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.Length > 0 && value[0] == ' ') value = value[1..];

            switch (field)
            {
                case "data":
                    if (hasData)
                    {
                        // Tolerance: some servers omit the blank separator line between JSON events.
                        if (LooksComplete(data) && value.Length > 0 && (value[0] == '{' || value == "[DONE]"))
                        {
                            yield return new SseEvent(evt, data.ToString(), id);
                            evt = null; data.Clear();
                        }
                        else data.Append('\n');
                    }
                    data.Append(value);
                    hasData = true;
                    break;
                case "event":
                    evt = value;
                    break;
                case "id":
                    id = value;
                    break;
                // "retry" and unknown fields are ignored.
            }
        }
        if (hasData) yield return new SseEvent(evt, data.ToString(), id);

        // A line reader over our own buffer (ReadLineAsync would grow a line without limit): \n, \r and \r\n end a line,
        // a line ending in \r is returned at once (no read-ahead that would hold an event until the next byte), and a
        // line past the cap is the provider's problem, not the process's memory.
        async Task<string?> NextLine()
        {
            lineBuf.Clear();
            while (true)
            {
                if (pos == len)
                {
                    len = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    pos = 0;
                    if (len == 0) return lineBuf.Length == 0 ? null : lineBuf.ToString();
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (buf[pos] == '\n') { pos++; continue; }
                }
                var span = buf.AsSpan(pos, len - pos);
                var end = span.IndexOfAny('\r', '\n');
                if (end < 0)
                {
                    lineBuf.Append(span);
                    pos = len;
                }
                else
                {
                    lineBuf.Append(span[..end]);
                    skipLf = span[end] == '\r';
                    pos += end + 1;
                }
                if (lineBuf.Length > MaxLineLength) throw ProviderErrors.SseLineTooLong(provider, MaxLineLength);
                if (end >= 0) return lineBuf.ToString();
            }
        }
    }

    private static bool LooksComplete(StringBuilder sb)
    {
        if (sb.Length == 0) return false;
        var last = sb[^1];
        return (last == '}' && sb[0] == '{') || sb.Equals("[DONE]");
    }
}
