using System.Text;

namespace NetPI.Tools.Ssh;

internal sealed record TextEdit(string OldText, string NewText, bool ReplaceAll);

internal sealed record EditOutcome(string Text, string Diff, int Added, int Removed, int Replacements, int FirstChangedLine);

/// <summary>
/// Exact replacements like the local <c>edit</c> tool: the edits are applied **in order to the evolving text**, so a
/// second edit can match what a first one wrote (rename the declaration, then its first use); matching ignores CRLF vs
/// LF, a uniform file keeps its own line endings, and in a mixed file the untouched lines keep their exact bytes while
/// only the lines an edit produces get the file's majority ending. Each oldText must match exactly once unless
/// replace_all, and a failure applies nothing. Produces a unified diff for the UI.
/// </summary>
internal static class TextEdits
{
    public static EditOutcome Apply(string original, IReadOnlyList<TextEdit> edits, string path)
    {
        var (crlfCount, lfCount) = CountBreaks(original);
        var originalNorm = original.Replace("\r\n", "\n");
        // A uniform file is converted whole, the way it always was: a CRLF file comes back CRLF, an LF file stays LF.
        if (crlfCount == 0 || lfCount == 0)
        {
            var toCrlf = crlfCount > 0;
            var text = originalNorm;
            var replacements = 0;
            foreach (var e in edits)
            {
                var old = e.OldText.Replace("\r\n", "\n");
                var replacement = e.NewText.Replace("\r\n", "\n");
                if (old.Length == 0) throw new EditException("oldText is empty.");
                // Like the local edit tool, every edit locates its oldText in the text the earlier edits left behind:
                // the spans shift with each replacement, so all-at-once bookkeeping against the original document would
                // refuse lists the local tool accepts.
                var found = FindAll(text, old);
                if (found.Count == 0) throw new EditException($"oldText not found in {path}: {Preview(old)}. Read the file again and copy the text exactly.");
                if (found.Count > 1 && !e.ReplaceAll)
                    throw new EditException($"oldText matches {found.Count} times in {path}: {Preview(old)}. Add surrounding lines to make it unique, or set replace_all.");
                text = Splice(text, found, old, replacement);
                replacements += found.Count;
            }
            var result = toCrlf ? text.Replace("\n", "\r\n") : text;
            var (diff, added, removed, first) = Diff(originalNorm, text, path);
            return new EditOutcome(result, diff, added, removed, replacements, first);
        }
        // Mixed line endings: the untouched text keeps its exact bytes; only the text an edit produces gets the file's
        // majority ending, so a CRLF-majority file does not become fully CRLF (nor an LF-majority one fully LF).
        var majorityCrlf = crlfCount > lfCount;
        var mixed = original;
        var nrepl = 0;
        foreach (var e in edits)
        {
            var (norm, map) = NormalizeWithMap(mixed);
            var old = e.OldText.Replace("\r\n", "\n");
            var replacement = e.NewText.Replace("\r\n", "\n");
            if (old.Length == 0) throw new EditException("oldText is empty.");
            var found = FindAll(norm, old);
            if (found.Count == 0) throw new EditException($"oldText not found in {path}: {Preview(old)}. Read the file again and copy the text exactly.");
            if (found.Count > 1 && !e.ReplaceAll)
                throw new EditException($"oldText matches {found.Count} times in {path}: {Preview(old)}. Add surrounding lines to make it unique, or set replace_all.");
            mixed = Splice(mixed, map, found, old, majorityCrlf ? replacement.Replace("\n", "\r\n") : replacement);
            nrepl += found.Count;
        }
        var (mdiff, madded, mremoved, mfirst) = Diff(originalNorm, mixed.Replace("\r\n", "\n"), path);
        return new EditOutcome(mixed, mdiff, madded, mremoved, nrepl, mfirst);
    }

    /// <summary>The CRLF and lone-LF breaks of a text (a lone \r is content, not a break, like the local codec).</summary>
    private static (int Crlf, int Lf) CountBreaks(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
            else if (text[i] == '\n') lf++;
        }
        return (crlf, lf);
    }

    private static List<int> FindAll(string text, string old)
    {
        var found = new List<int>();
        for (var i = text.IndexOf(old, StringComparison.Ordinal); i >= 0; i = text.IndexOf(old, i + old.Length, StringComparison.Ordinal))
            found.Add(i);
        return found;
    }

    /// <summary>Replace every found span (positions in the text) with the replacement.</summary>
    private static string Splice(string text, List<int> found, string old, string replacement)
    {
        var sb = new StringBuilder(text.Length + Math.Max(0, (replacement.Length - old.Length) * found.Count));
        var last = 0;
        foreach (var p in found)
        {
            sb.Append(text, last, p - last).Append(replacement);
            last = p + old.Length;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// Replace every found span (positions in the LF-normalized view) with the replacement, spliced back onto the
    /// text with its line endings: <c>map[p]</c> is where the view's character <c>p</c> sits in the text, so a CRLF the
    /// view folded to one \n maps onto its two characters, and the untouched stretches keep their exact bytes.
    /// </summary>
    private static string Splice(string text, int[] map, List<int> found, string old, string replacement)
    {
        var sb = new StringBuilder(text.Length + Math.Max(0, (replacement.Length - old.Length) * found.Count));
        var last = 0;
        foreach (var p in found)
        {
            var from = map[p];
            var to = map[p + old.Length];
            sb.Append(text, last, from - last).Append(replacement);
            last = to;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// The LF-normalized view of <paramref name="text"/> with a map back to it: <c>map[i]</c> is the index in the text
    /// of the character the view's character <c>i</c> stands for, and <c>map[n]</c> is the text's length.
    /// </summary>
    private static (string Norm, int[] Map) NormalizeWithMap(string text)
    {
        var chars = new char[text.Length];
        var map = new int[text.Length + 1];
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                chars[n] = '\n';
                map[n] = i;      // the view's \n stands for the CRLF starting here
                n++;
                map[n] = i + 2;  // the next view character starts after it
                i++;
            }
            else
            {
                chars[n] = text[i];
                map[n] = i;
                n++;
                map[n] = i + 1;
            }
        }
        map[n] = text.Length;
        return (new string(chars, 0, n), map);
    }

    private static string Preview(string s)
    {
        var line = s.Split('\n')[0];
        return "\"" + (line.Length > 80 ? line[..80] + "…" : line) + (s.Contains('\n') ? "…" : "") + "\"";
    }

    /// <summary>
    /// A unified diff of the original and the result, line by line: unchanged lines are context, changed lines are
    /// hunks with 3 lines of context, and nearby hunks merge. Only the middle that actually differs is diffed
    /// (common prefix and suffix are dropped first), which keeps an edit of a big file cheap.
    /// </summary>
    private static (string Diff, int Added, int Removed, int FirstLine) Diff(string original, string result, string path)
    {
        string[] Lines(string s)
        {
            if (s.Length == 0) return [];
            var ls = s.Split('\n');
            return ls[^1].Length == 0 ? ls[..^1] : ls; // no phantom line after the final newline
        }
        var lines = Lines(original);
        var newer = Lines(result);
        var lineCount = lines.Length;

        int a0 = 0, b0 = 0;
        while (a0 < lines.Length && b0 < newer.Length && lines[a0] == newer[b0]) { a0++; b0++; }
        int a1 = lines.Length, b1 = newer.Length;
        while (a1 > a0 && b1 > b0 && lines[a1 - 1] == newer[b1 - 1]) { a1--; b1--; }

        var changes = new List<(int A, int B, string[] NewLines)>();
        if (a1 > a0 || b1 > b0)
            MidDiff(lines, newer, a0, b0, a1, b1, changes);

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
            foreach (var (a, b, ins) in group)
            {
                for (; cursor < a; cursor++) { body.Append(' ').Append(lines[cursor]).Append('\n'); oldCount++; newCount++; }
                for (var i = a; i < b; i++) { body.Append('-').Append(lines[i]).Append('\n'); oldCount++; removed++; }
                foreach (var l in ins) { body.Append('+').Append(l).Append('\n'); newCount++; added++; }
                cursor = b;
            }
            for (; cursor < to; cursor++) { body.Append(' ').Append(lines[cursor]).Append('\n'); oldCount++; newCount++; }
            sb.Append($"@@ -{from + 1},{oldCount} +{from + 1 + shift},{newCount} @@\n").Append(body);
            shift += newCount - oldCount;
        }
        return (sb.ToString(), added, removed, changes.Count > 0 ? changes[0].A + 1 : 0);
    }

    /// <summary>The largest LCS table the diff builds; beyond it the changed middle is one hunk (old out, new in).</summary>
    private const int LcsCellsCap = 2_000_000;

    /// <summary>
    /// The change blocks of the changed middle: the original lines a stretch of replacements deletes (a contiguous
    /// range) and the result lines it inserts there (a contiguous range). An LCS traces them; a middle too large to
    /// table is one block that replaces the whole middle.
    /// </summary>
    private static void MidDiff(string[] oldLines, string[] newLines, int a0, int b0, int a1, int b1,
        List<(int A, int B, string[] NewLines)> changes)
    {
        int n = a1 - a0, m = b1 - b0;
        if ((long)n * m > LcsCellsCap)
        {
            changes.Add((a0, a1, newLines[b0..b1]));
            return;
        }
        // dp[i, j] = the longest common subsequence of oldLines[a0+i..] and newLines[b0+j..]
        var dp = new int[(n + 1) * (m + 1)];
        for (var oi = n - 1; oi >= 0; oi--)
        {
            int row = oi * (m + 1), below = (oi + 1) * (m + 1);
            for (var oj = m - 1; oj >= 0; oj--)
                dp[row + oj] = oldLines[a0 + oi] == newLines[b0 + oj]
                    ? dp[below + oj + 1] + 1
                    : Math.Max(dp[below + oj], dp[row + oj + 1]);
        }
        // Walk it: a diagonal step is a common line, a run of non-diagonal steps is one block. A tie resolves to the
        // insertion, so a block runs out its old lines before its new ones in (the way a replacement reads).
        int i = 0, j = 0, runI = -1, runJ = -1;
        void EndRun()
        {
            if (runI >= 0) changes.Add((a0 + runI, a0 + i, newLines[(b0 + runJ)..(b0 + j)]));
            runI = -1;
        }
        while (i < n || j < m)
        {
            if (i < n && j < m && oldLines[a0 + i] == newLines[b0 + j])
            {
                EndRun(); // the run ends at the common line: i and j still point at it
                i++; j++;
                continue;
            }
            if (runI < 0) { runI = i; runJ = j; }
            var down = i < n ? dp[(i + 1) * (m + 1) + j] : -1;
            var right = j < m ? dp[i * (m + 1) + j + 1] : -1;
            if (i >= n || (j < m && down <= right)) j++;
            else i++;
        }
        EndRun();
    }
}

internal sealed class EditException(string message) : Exception(message);
