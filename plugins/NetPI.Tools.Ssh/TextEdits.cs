using System.Text;

namespace NetPI.Tools.Ssh;

internal sealed record TextEdit(string OldText, string NewText, bool ReplaceAll);

internal sealed record EditOutcome(string Text, string Diff, int Added, int Removed, int Replacements, int FirstChangedLine);

/// <summary>
/// Exact replacements like the local <c>edit</c> tool: matching ignores CRLF vs LF, the file keeps its own line endings,
/// each oldText must match exactly once unless replace_all. Produces a unified diff for the UI.
/// </summary>
internal static class TextEdits
{
    public static EditOutcome Apply(string original, IReadOnlyList<TextEdit> edits, string path)
    {
        var crlf = original.Contains("\r\n", StringComparison.Ordinal);
        var text = original.Replace("\r\n", "\n");
        var spans = new List<(int Start, int Length, string Replacement)>();
        foreach (var e in edits)
        {
            var old = e.OldText.Replace("\r\n", "\n");
            var replacement = e.NewText.Replace("\r\n", "\n");
            if (old.Length == 0) throw new EditException("oldText is empty.");
            var found = new List<int>();
            for (var i = text.IndexOf(old, StringComparison.Ordinal); i >= 0; i = text.IndexOf(old, i + old.Length, StringComparison.Ordinal))
                found.Add(i);
            if (found.Count == 0) throw new EditException($"oldText not found in {path}: {Preview(old)}. Read the file again and copy the text exactly.");
            if (found.Count > 1 && !e.ReplaceAll)
                throw new EditException($"oldText matches {found.Count} times in {path}: {Preview(old)}. Add surrounding lines to make it unique, or set replace_all.");
            spans.AddRange(found.Select(i => (i, old.Length, replacement)));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var k = 1; k < spans.Count; k++)
            if (spans[k].Start < spans[k - 1].Start + spans[k - 1].Length)
                throw new EditException("Two edits overlap; combine them into one.");

        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var (start, length, replacement) in spans)
        {
            sb.Append(text, at, start - at).Append(replacement);
            at = start + length;
        }
        sb.Append(text, at, text.Length - at);
        var result = sb.ToString();
        var (diff, added, removed, first) = Diff(text, result, spans, path);
        return new EditOutcome(crlf ? result.Replace("\n", "\r\n") : result, diff, added, removed, spans.Count, first);
    }

    private static string Preview(string s)
    {
        var line = s.Split('\n')[0];
        return "\"" + (line.Length > 80 ? line[..80] + "…" : line) + (s.Contains('\n') ? "…" : "") + "\"";
    }

    /// <summary>
    /// The original lines each replacement touches become one block (overlapping blocks merge); a block's new lines are
    /// the same stretch of the result. Nearby blocks share a hunk with 3 lines of context.
    /// </summary>
    private static (string Diff, int Added, int Removed, int FirstLine) Diff(string text, string result,
        List<(int Start, int Length, string Replacement)> spans, string path)
    {
        var lines = text.Split('\n');
        var lineCount = text.EndsWith('\n') ? lines.Length - 1 : lines.Length; // no phantom line after the final newline
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        int LineOf(int index)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (starts[mid] <= index) lo = mid; else hi = mid - 1;
            }
            return lo;
        }
        // original offset → result offset (replacements that end at or before it are applied)
        int Map(int offset)
        {
            var shift = 0;
            foreach (var (start, length, replacement) in spans)
                if (start + length <= offset) shift += replacement.Length - length;
            return offset + shift;
        }
        int CharStart(int line) => line < starts.Count ? starts[line] : text.Length;

        var blocks = new List<(int A, int B)>();
        foreach (var (start, length, _) in spans)
        {
            var a = LineOf(start);
            var b = LineOf(start + length - 1) + 1;
            if (blocks.Count > 0 && a < blocks[^1].B) blocks[^1] = (blocks[^1].A, Math.Max(blocks[^1].B, b));
            else blocks.Add((a, b));
        }
        var changes = blocks.Select(bl =>
        {
            var from = Map(CharStart(bl.A));
            var to = Map(CharStart(bl.B));
            var chunk = result[from..to];
            if (chunk.EndsWith('\n')) chunk = chunk[..^1];
            var newLines = chunk.Length == 0 && to == from ? [] : chunk.Split('\n');
            return (bl.A, bl.B, NewLines: newLines);
        }).ToList();

        const int Context = 3;
        var sb = new StringBuilder($"--- a/{path}\n+++ b/{path}\n");
        int added = 0, removed = 0, shift = 0, k = 0;
        while (k < changes.Count)
        {
            var group = new List<(int A, int B, string[] NewLines)> { changes[k] };
            while (k + 1 < changes.Count && changes[k + 1].A - group[^1].B <= Context * 2) group.Add(changes[++k]);
            k++;
            var from = Math.Max(0, group[0].A - Context);
            var to = Math.Min(lineCount, group[^1].B + Context);
            var body = new StringBuilder();
            int oldCount = 0, newCount = 0, cursor = from;
            foreach (var (a, b, newLines) in group)
            {
                for (; cursor < a; cursor++) { body.Append(' ').Append(lines[cursor]).Append('\n'); oldCount++; newCount++; }
                for (var i = a; i < b; i++) { body.Append('-').Append(lines[i]).Append('\n'); oldCount++; removed++; }
                foreach (var l in newLines) { body.Append('+').Append(l).Append('\n'); newCount++; added++; }
                cursor = b;
            }
            for (; cursor < to; cursor++) { body.Append(' ').Append(lines[cursor]).Append('\n'); oldCount++; newCount++; }
            sb.Append($"@@ -{from + 1},{oldCount} +{from + 1 + shift},{newCount} @@\n").Append(body);
            shift += newCount - oldCount;
        }
        return (sb.ToString(), added, removed, changes.Count > 0 ? changes[0].A + 1 : 0);
    }
}

internal sealed class EditException(string message) : Exception(message);
