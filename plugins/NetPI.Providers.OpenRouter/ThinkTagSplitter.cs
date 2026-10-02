using System.Text;

// NOTE: mirrored in NetPI.Providers.{AiProxy,OpenRouter} (plugins cannot share code). Keep the copies in sync.

namespace NetPI.Providers.OpenRouter;

/// <summary>
/// Splits a <em>leading</em> inline <c>&lt;think&gt;…&lt;/think&gt;</c> reasoning block out of streamed content.
/// Tags may be split across chunks: a trailing partial tag is held back until the next chunk (or
/// <see cref="Flush"/>). Leading whitespace at the start of the stream and right after the block is dropped.
/// <para>
/// Only a leading block is reasoning. The format is a prefix of the completion, so a <c>&lt;think&gt;</c> that
/// arrives after visible text is content the model wrote — a tag it is quoting, a file it is editing, a sentence
/// about this very format. Splitting it there cuts an answer in two and stores a thinking part after its text:
/// the UI shows a phantom block, the model reads its own words back as its reasoning on the next turn, and a
/// transport that fixes the order of an assistant turn's items (Responses, Anthropic) refuses the whole request
/// with <c>400 invalid_assistant_history</c>. After the first visible text everything is text, verbatim.
/// </para>
/// </summary>
internal sealed class ThinkTagSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _inThink;
    private bool _trimLeading = true;
    /// <summary>Visible text has been emitted: from here on a tag is content, not a boundary.</summary>
    private bool _sawText;

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
            var idx = _sawText ? -1 : s.IndexOf(tag, pos, StringComparison.Ordinal);
            if (idx >= 0)
            {
                Emit(s[pos..idx], emit);
                if (_sawText) { Verbatim(s[idx..], emit); return; } // a tag after visible text is content
                pos = idx + tag.Length;
                _inThink = !_inThink;
                _trimLeading = true;
                continue;
            }

            // No full tag: hold back a trailing prefix of the tag ("<thi") for the next chunk — unless visible
            // text has already ruled the boundary out, and no later chunk can complete one.
            var hold = _sawText ? 0 : PartialSuffix(s, pos, tag);
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
        if (_sawText) { Verbatim(s, emit); return; }
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
        if (!_inThink) _sawText = true;
        emit(_inThink, text);
    }

    /// <summary>Text as written: mid-sentence, so no leading trim — and never a boundary.</summary>
    private void Verbatim(string text, Action<bool, string> emit)
    {
        if (text.Length == 0) return;
        _trimLeading = false;
        _sawText = true;
        emit(false, text);
    }

    private static int PartialSuffix(string s, int start, string tag)
    {
        var max = Math.Min(tag.Length - 1, s.Length - start);
        for (var len = max; len > 0; len--)
            if (string.CompareOrdinal(s, s.Length - len, tag, 0, len) == 0) return len;
        return 0;
    }
}
