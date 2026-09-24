using System.Text;

// NOTE: mirrored in NetPI.Providers.{AiProxy,OpenRouter} (plugins cannot share code). Keep the copies in sync.

namespace NetPI.Providers.OpenRouter;

/// <summary>
/// Splits inline <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning out of streamed content. Tags may be split across
/// chunks: a trailing partial tag is held back until the next chunk (or <see cref="Flush"/>).
/// Leading whitespace at the start of the stream and right after each tag is dropped.
/// </summary>
internal sealed class ThinkTagSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _inThink;
    private bool _trimLeading = true;

    public bool InThink => _inThink;

    /// <summary>Process a chunk; <paramref name="emit"/> receives (isThinking, text) segments in order.</summary>
    public void Process(string chunk, Action<bool, string> emit)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _pending.Append(chunk);
        var s = _pending.ToString();
        _pending.Clear();

        var pos = 0;
        while (pos < s.Length)
        {
            var tag = _inThink ? Close : Open;
            var idx = s.IndexOf(tag, pos, StringComparison.Ordinal);
            if (idx >= 0)
            {
                Emit(s[pos..idx], emit);
                pos = idx + tag.Length;
                _inThink = !_inThink;
                _trimLeading = true;
                continue;
            }

            // No full tag: hold back a trailing prefix of the tag ("<thi") for the next chunk.
            var hold = PartialSuffix(s, pos, tag);
            Emit(s[pos..(s.Length - hold)], emit);
            if (hold > 0) _pending.Append(s, s.Length - hold, hold);
            break;
        }
    }

    /// <summary>Emit anything held back (end of stream or before a tool call).</summary>
    public void Flush(Action<bool, string> emit)
    {
        if (_pending.Length == 0) return;
        var s = _pending.ToString();
        _pending.Clear();
        Emit(s, emit);
    }

    private void Emit(string text, Action<bool, string> emit)
    {
        if (text.Length == 0) return;
        if (_trimLeading)
        {
            text = text.TrimStart();
            if (text.Length == 0) return;
            _trimLeading = false;
        }
        emit(_inThink, text);
    }

    private static int PartialSuffix(string s, int start, string tag)
    {
        var max = Math.Min(tag.Length - 1, s.Length - start);
        for (var len = max; len > 0; len--)
            if (string.CompareOrdinal(s, s.Length - len, tag, 0, len) == 0) return len;
        return 0;
    }
}
