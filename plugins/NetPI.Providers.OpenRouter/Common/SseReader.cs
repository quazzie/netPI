using System.Runtime.CompilerServices;
using System.Text;

// NOTE: mirrored in NetPI.Providers.{AiProxy,Anthropic,OpenRouter}/Common (plugins cannot share code). Keep the copies in sync.
namespace NetPI.Providers.OpenRouter;

/// <summary>One server-sent event (<c>event:</c> name, joined <c>data:</c> lines, <c>id:</c>).</summary>
public readonly record struct SseEvent(string? Event, string Data, string? Id = null)
{
    /// <summary>OpenAI-style end-of-stream sentinel.</summary>
    public bool IsDone => Data == "[DONE]";
}

/// <summary>Minimal, allocation-light SSE reader (spec-compliant plus a few tolerances for sloppy servers).</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
        string? evt = null, id = null;
        var data = new StringBuilder();
        var hasData = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
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
    }

    private static bool LooksComplete(StringBuilder sb)
    {
        if (sb.Length == 0) return false;
        var last = sb[^1];
        return (last == '}' && sb[0] == '{') || sb.Equals("[DONE]");
    }
}
