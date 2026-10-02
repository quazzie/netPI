using System.Text;

namespace NetPI;

/// <summary>
/// How a tool trims long output to a limit: the tail of command output, head cuts and head+tail cuts, and the notes
/// that go with them. One implementation for all of them, so a cut never splits a surrogate pair and the notes
/// read the same in every tool that tails or cuts output (shell, ssh, agent results, compaction).
/// </summary>
public static class ToolOutput
{
    /// <summary>The model tail of command output: the most lines and the most UTF-8 bytes a result shows.</summary>
    public const int ModelMaxLines = 2000;
    public const int ModelMaxBytes = 30 * 1024;

    /// <summary>Normalize line breaks and collapse carriage-return overwrites (progress bars): keep the text after the last \r of each line.</summary>
    public static string ResolveCarriageReturns(string s)
    {
        if (s.IndexOf('\r') < 0) return s;
        s = s.Replace("\r\n", "\n");
        if (s.IndexOf('\r') < 0) return s;
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            var cr = l.TrimEnd('\r').LastIndexOf('\r');
            lines[i] = cr >= 0 ? l[(cr + 1)..].TrimEnd('\r') : l.TrimEnd('\r');
        }
        return string.Join('\n', lines);
    }

    /// <summary>Last <paramref name="maxLines"/> lines, at most <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    public static (string Text, bool Truncated, int TotalLines, int ShownLines) TailLines(string s, int maxLines, int maxBytes)
    {
        s = s.TrimEnd('\n');
        if (s.Length == 0) return ("", false, 0, 0);
        var totalLines = s.AsSpan().Count('\n') + 1;
        if (totalLines <= maxLines && (s.Length <= maxBytes / 4 || Encoding.UTF8.GetByteCount(s) <= maxBytes))
            return (s, false, totalLines, totalLines);

        var bytes = 0;
        var shown = 0;
        var end = s.Length;
        var start = s.Length;
        while (shown < maxLines && start > 0)
        {
            var nl = s.LastIndexOf('\n', start - 1);
            var lineStart = nl + 1;
            var lineBytes = Encoding.UTF8.GetByteCount(s.AsSpan(lineStart, start - lineStart)) + 1;
            if (bytes + lineBytes > maxBytes)
            {
                if (shown == 0)
                {
                    // One giant line: keep its end.
                    var keep = Math.Min(start - lineStart, maxBytes);
                    var from = start - keep;
                    if (from > 0 && char.IsLowSurrogate(s[from])) from++;
                    return ("…" + s[from..end], true, totalLines, 1);
                }
                break;
            }
            bytes += lineBytes;
            shown++;
            start = nl < 0 ? 0 : nl;
            if (nl < 0) { start = 0; break; }
        }
        var text = s[(start == 0 ? 0 : start + 1)..end];
        return (text, shown < totalLines, totalLines, shown);
    }

    /// <summary>
    /// The note a tool prepends when it shows only the tail of a longer output:
    /// "[Output truncated: showing the last N lines of M. Full output saved to P (use read or grep on it).]"
    /// <paramref name="total"/> is the phrase after "lines" (" of 3000", "; only the last 8 MB were kept", …); with
    /// <paramref name="savedPath"/> null no output was saved and the note ends after the count.
    /// </summary>
    public static string Note(int shownLines, string total, string? savedPath = null, string savedAs = "Full output")
    {
        var s = new StringBuilder("[Output truncated: showing the last ").Append(shownLines).Append(" lines").Append(total).Append('.');
        if (savedPath is not null) s.Append(' ').Append(savedAs).Append(" saved to ").Append(savedPath).Append(" (use read or grep on it).");
        s.Append(']');
        return s.ToString();
    }
}

/// <summary>Cutting text to a limit without splitting a surrogate pair.</summary>
public static class TextLimit
{
    /// <summary>
    /// The first <paramref name="max"/> characters; when it was longer, <paramref name="note"/> is appended in a
    /// "[… truncated]" marker with the number of characters left out.
    /// </summary>
    public static string Head(string? text, int max, string note)
    {
        text ??= "";
        if (text.Length <= max) return text;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + $"\n[... truncated: {text.Length - cut:N0} more characters. {note}]";
    }

    /// <summary>
    /// The beginning and the end of text that outruns <paramref name="max"/>: the head gets
    /// <paramref name="headShare"/> shares of the budget (head = max · share / (share + 1)), the tail the rest, and
    /// <paramref name="marker"/> — the characters dropped and the total — stands in between.
    /// </summary>
    public static string HeadTail(string text, int max, int headShare, Func<int, int, string> marker)
    {
        if (text.Length <= max) return text;
        var head = max * headShare / (headShare + 1);
        if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        var tail = max - head;
        if (tail > 0 && char.IsLowSurrogate(text[^tail])) tail--;
        return text[..head] + marker(text.Length - head - tail, text.Length) + text[^tail..];
    }
}
