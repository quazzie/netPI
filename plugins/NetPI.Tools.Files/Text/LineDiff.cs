using System.Text;

namespace NetPI.Tools.Files;

public enum DiffOp { Equal, Delete, Insert }

/// <summary>One line of an edit script. Line numbers are 1-based; 0 when not applicable (inserted lines have no old line).</summary>
public readonly record struct DiffLine(DiffOp Op, string Text, int OldLine, int NewLine, bool NoNewlineAtEnd = false);

public sealed class UnifiedDiff
{
    /// <summary>Unified diff text (empty when the texts are equal).</summary>
    public string Text { get; init; } = "";
    public int Added { get; init; }
    public int Removed { get; init; }
    public int Hunks { get; init; }
    /// <summary>The diff text was cut at the requested maximum number of lines.</summary>
    public bool Truncated { get; init; }
    /// <summary>First changed line in the new text (1-based), 0 when unchanged.</summary>
    public int FirstChangedLine { get; init; }
}

/// <summary>Myers line diff with unified-diff formatting.</summary>
public static class LineDiff
{
    /// <summary>Beyond this edit distance the middle section is reported as a full replacement.</summary>
    public const int DefaultMaxEditDistance = 3000;

    /// <summary>Split into lines; the last line of a text without a trailing newline is flagged.</summary>
    private static (string[] Lines, bool NoEol) Split(string lfText)
    {
        var lines = TextCodec.SplitLines(lfText);
        return (lines, lfText.Length > 0 && lfText[^1] != '\n');
    }

    public static List<DiffLine> Compute(string oldText, string newText, int maxEditDistance = DefaultMaxEditDistance)
    {
        var (a, aNoEol) = Split(TextCodec.NormalizeToLf(oldText));
        var (b, bNoEol) = Split(TextCodec.NormalizeToLf(newText));

        // Intern lines to ints; the missing final newline is part of the last line's identity (like git).
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s) { if (!ids.TryGetValue(s, out var id)) { id = ids.Count; ids[s] = id; } return id; }
        var ai = new int[a.Length];
        for (var i = 0; i < a.Length; i++) ai[i] = Id(i == a.Length - 1 && aNoEol ? a[i] + "\0" : a[i]);
        var bi = new int[b.Length];
        for (var i = 0; i < b.Length; i++) bi[i] = Id(i == b.Length - 1 && bNoEol ? b[i] + "\0" : b[i]);

        var result = new List<DiffLine>(Math.Max(a.Length, b.Length) + 8);
        int prefix = 0;
        while (prefix < ai.Length && prefix < bi.Length && ai[prefix] == bi[prefix]) prefix++;
        int suffix = 0;
        while (suffix < ai.Length - prefix && suffix < bi.Length - prefix && ai[ai.Length - 1 - suffix] == bi[bi.Length - 1 - suffix]) suffix++;

        for (var i = 0; i < prefix; i++) result.Add(new DiffLine(DiffOp.Equal, a[i], i + 1, i + 1));

        var aMid = ai.AsSpan(prefix, ai.Length - prefix - suffix).ToArray();
        var bMid = bi.AsSpan(prefix, bi.Length - prefix - suffix).ToArray();
        var script = Myers(aMid, bMid, maxEditDistance);
        foreach (var (op, x, y) in script)
        {
            result.Add(op switch
            {
                DiffOp.Equal => new DiffLine(DiffOp.Equal, a[prefix + x], prefix + x + 1, prefix + y + 1),
                DiffOp.Delete => new DiffLine(DiffOp.Delete, a[prefix + x], prefix + x + 1, 0),
                _ => new DiffLine(DiffOp.Insert, b[prefix + y], 0, prefix + y + 1),
            });
        }

        for (var i = 0; i < suffix; i++)
        {
            var ax = a.Length - suffix + i;
            var bx = b.Length - suffix + i;
            result.Add(new DiffLine(DiffOp.Equal, a[ax], ax + 1, bx + 1));
        }

        // Flag the last line of each side that has no trailing newline.
        for (var i = 0; i < result.Count; i++)
        {
            var d = result[i];
            var flag = (d.Op != DiffOp.Insert && aNoEol && d.OldLine == a.Length) || (d.Op != DiffOp.Delete && bNoEol && d.NewLine == b.Length);
            if (flag) result[i] = d with { NoNewlineAtEnd = true };
        }
        return result;
    }

    /// <summary>Myers O((N+M)D) diff returning (op, index in a, index in b) triples in order.</summary>
    private static List<(DiffOp Op, int X, int Y)> Myers(int[] a, int[] b, int maxD)
    {
        int n = a.Length, m = b.Length;
        var script = new List<(DiffOp, int, int)>(n + m);
        if (n == 0 && m == 0) return script;
        if (n == 0) { for (var j = 0; j < m; j++) script.Add((DiffOp.Insert, 0, j)); return script; }
        if (m == 0) { for (var i = 0; i < n; i++) script.Add((DiffOp.Delete, i, 0)); return script; }

        var max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        // trace[d] holds V[k] for k in [-(d+1), d+1] as it was at the start of round d.
        var trace = new List<int[]>();
        var found = false;
        var limit = Math.Min(max, maxD);
        for (var d = 0; d <= limit; d++)
        {
            var snap = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snap, 0, snap.Length);
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
            if (found) break;
        }

        if (!found)
        {
            // Too different: report as a block replacement.
            for (var i = 0; i < n; i++) script.Add((DiffOp.Delete, i, 0));
            for (var j = 0; j < m; j++) script.Add((DiffOp.Insert, 0, j));
            return script;
        }

        // Backtrack.
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var snap = trace[d];
            int V(int k) => snap[k + d + 1];
            var k0 = cx - cy;
            int prevK = (k0 == -d || (k0 != d && V(k0 - 1) < V(k0 + 1))) ? k0 + 1 : k0 - 1;
            var prevX = V(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                script.Add((DiffOp.Equal, cx - 1, cy - 1));
                cx--; cy--;
            }
            if (d > 0)
            {
                if (cx == prevX) script.Add((DiffOp.Insert, cx, cy - 1));
                else script.Add((DiffOp.Delete, cx - 1, cy));
            }
            cx = prevX; cy = prevY;
        }
        script.Reverse();
        return script;
    }

    /// <summary>Unified diff with <paramref name="context"/> lines of context. <paramref name="maxLines"/> caps the output (0 = unlimited).</summary>
    public static UnifiedDiff Unified(string oldText, string newText, string? path = null, int context = 3, int maxLines = 0, bool oldIsNull = false)
    {
        var lines = Compute(oldText, newText);
        int added = 0, removed = 0;
        foreach (var l in lines)
        {
            if (l.Op == DiffOp.Insert) added++;
            else if (l.Op == DiffOp.Delete) removed++;
        }
        if (added == 0 && removed == 0) return new UnifiedDiff();

        // Group changes into hunks.
        var hunks = new List<(int Start, int End)>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Op == DiffOp.Equal) { i++; continue; }
            var start = Math.Max(0, i - context);
            var end = i;
            while (true)
            {
                while (end < lines.Count && lines[end].Op != DiffOp.Equal) end++;
                // end = first equal after a change run; look ahead for another change within 2*context.
                var next = end;
                while (next < lines.Count && lines[next].Op == DiffOp.Equal && next - end < 2 * context) next++;
                if (next < lines.Count && lines[next].Op != DiffOp.Equal) { end = next; continue; }
                break;
            }
            var stop = Math.Min(lines.Count, end + context);
            if (hunks.Count > 0 && start <= hunks[^1].End) hunks[^1] = (hunks[^1].Start, stop);
            else hunks.Add((start, stop));
            i = stop;
        }

        var sb = new StringBuilder();
        var count = 0;
        var truncated = false;
        bool Emit(string line)
        {
            if (maxLines > 0 && count >= maxLines) { truncated = true; return false; }
            sb.Append(line).Append('\n');
            count++;
            return true;
        }

        if (path is not null)
        {
            Emit(oldIsNull ? "--- /dev/null" : $"--- a/{path}");
            Emit($"+++ b/{path}");
        }
        var firstChanged = 0;
        foreach (var (start, end) in hunks)
        {
            int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
            int lastOld = 0, lastNew = 0;
            // Line numbers preceding the hunk (for empty ranges).
            for (var j = start - 1; j >= 0; j--)
            {
                if (lastOld == 0 && lines[j].OldLine > 0) lastOld = lines[j].OldLine;
                if (lastNew == 0 && lines[j].NewLine > 0) lastNew = lines[j].NewLine;
                if (lastOld > 0 && lastNew > 0) break;
            }
            for (var j = start; j < end; j++)
            {
                var l = lines[j];
                if (l.Op != DiffOp.Insert) { if (oldStart == 0) oldStart = l.OldLine; oldCount++; }
                if (l.Op != DiffOp.Delete) { if (newStart == 0) newStart = l.NewLine; newCount++; }
                if (firstChanged == 0 && l.Op != DiffOp.Equal)
                    firstChanged = l.Op == DiffOp.Insert ? l.NewLine : Math.Max(1, NewLineNear(lines, j));
            }
            if (oldCount == 0) oldStart = lastOld;
            if (newCount == 0) newStart = lastNew;
            if (!Emit($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@")) break;
            var stopped = false;
            for (var j = start; j < end && !stopped; j++)
            {
                var l = lines[j];
                var prefix = l.Op switch { DiffOp.Insert => '+', DiffOp.Delete => '-', _ => ' ' };
                if (!Emit(prefix + l.Text)) { stopped = true; break; }
                if (l.NoNewlineAtEnd && !Emit("\\ No newline at end of file")) stopped = true;
            }
            if (stopped) break;
        }
        if (truncated) sb.Append("… (diff truncated)\n");
        return new UnifiedDiff { Text = sb.ToString(), Added = added, Removed = removed, Hunks = hunks.Count, Truncated = truncated, FirstChangedLine = firstChanged };
    }

    private static int NewLineNear(List<DiffLine> lines, int index)
    {
        for (var j = index; j >= 0; j--)
            if (lines[j].NewLine > 0) return lines[j].Op == DiffOp.Equal ? lines[j].NewLine + 1 : lines[j].NewLine;
        return 1;
    }

    /// <summary>Cut a diff text to at most <paramref name="maxLines"/> lines / <paramref name="maxChars"/> characters.</summary>
    public static string Cap(string diff, int maxLines, int maxChars, out bool truncated)
    {
        truncated = false;
        if (diff.Length <= maxChars && diff.AsSpan().Count('\n') <= maxLines) return diff;
        var sb = new StringBuilder();
        var n = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (n >= maxLines || sb.Length + line.Length + 1 > maxChars) { truncated = true; break; }
            sb.Append(line).Append('\n');
            n++;
        }
        if (truncated) sb.Append("… (diff truncated)\n");
        return sb.ToString();
    }
}
