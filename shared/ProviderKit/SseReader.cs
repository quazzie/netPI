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
        char? pending = null;
        var buf = new char[1];

        while (true)
        {
            var line = await NextLine();
            if (line is null) break;

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

        // One char at a time (ReadLineAsync would grow a line without limit): \n and \r both end a line, a \n after
        // a \r is part of the ending, and a line past the cap is the provider's problem, not the process's memory.
        async Task<string?> NextLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                char ch;
                if (pending is { } p) { ch = p; pending = null; }
                else
                {
                    var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (n == 0) return sb.Length == 0 ? null : sb.ToString();
                    ch = buf[0];
                }
                if (ch == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                if (ch == '\r')
                {
                    var n2 = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (n2 > 0 && buf[0] != '\n') pending = buf[0];
                    return sb.ToString();
                }
                sb.Append(ch);
                if (sb.Length > MaxLineLength) throw ProviderErrors.SseLineTooLong(provider, MaxLineLength);
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
