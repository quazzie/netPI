using System.Text;

namespace NetPI.Tools.Files;

/// <summary>How an edit's oldText was located.</summary>
public enum MatchStrategy
{
    Exact,
    /// <summary>Whole lines compared ignoring trailing whitespace.</summary>
    TrailingWhitespace,
    /// <summary>Additionally smart quotes → ASCII, dashes → '-', exotic spaces → ' ', zero-width chars removed.</summary>
    Unicode,
    /// <summary>Additionally ignoring leading indentation; newText is re-indented by the found delta.</summary>
    Indentation,
}

public sealed class EditOutcome
{
    public bool Success { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public MatchStrategy Strategy { get; init; }
    /// <summary>1-based line numbers (in the text before this edit) where replacements happened.</summary>
    public List<int> Lines { get; init; } = [];
    public int Replacements { get; init; }
}

/// <summary>
/// Locates and applies a single oldText → newText replacement in LF-normalized text.
/// Exact match first, then the fallback ladder (trailing whitespace → unicode → indentation).
/// </summary>
public static class EditMatcher
{
    public static string StrategyName(MatchStrategy s) => s switch
    {
        MatchStrategy.TrailingWhitespace => "trailing-whitespace",
        MatchStrategy.Unicode => "unicode",
        MatchStrategy.Indentation => "indentation",
        _ => "exact",
    };

    public static EditOutcome Apply(string text, string oldText, string newText, bool replaceAll)
    {
        oldText = TextCodec.NormalizeToLf(oldText);
        newText = TextCodec.NormalizeToLf(newText);

        if (oldText.Length == 0)
        {
            if (text.Length == 0)
                return new EditOutcome { Success = true, Text = newText, Strategy = MatchStrategy.Exact, Lines = [1], Replacements = 1 };
            return Fail("oldText is empty. Provide the exact text to replace (include a few surrounding lines), or use the write tool to replace the whole file.");
        }
        if (oldText == newText)
            return Fail("oldText and newText are identical, so there is nothing to change.");

        // 1. Exact.
        var positions = FindAll(text, oldText);
        if (positions.Count > 0)
        {
            if (positions.Count > 1 && !replaceAll)
                return Ambiguous(positions.Count, positions.Select(p => LineOf(text, p)).ToList(), MatchStrategy.Exact);
            var sb = new StringBuilder(text.Length + Math.Max(0, (newText.Length - oldText.Length) * positions.Count));
            var last = 0;
            foreach (var p in positions)
            {
                sb.Append(text, last, p - last).Append(newText);
                last = p + oldText.Length;
            }
            sb.Append(text, last, text.Length - last);
            return new EditOutcome
            {
                Success = true, Text = sb.ToString(), Strategy = MatchStrategy.Exact,
                Lines = positions.Select(p => LineOf(text, p)).ToList(), Replacements = positions.Count,
            };
        }

        // 2. Line-based fallbacks.
        var fileLines = text.Split('\n');
        var oldLines = TrimTrailingEmpty(oldText.Split('\n'), oldText.EndsWith('\n'));
        var newLines = newText.Length == 0 ? [] : TrimTrailingEmpty(newText.Split('\n'), newText.EndsWith('\n'));
        if (oldLines.Length == 0 || oldLines.All(string.IsNullOrWhiteSpace))
            return Fail(NotFoundMessage(text, oldText));

        foreach (var strategy in new[] { MatchStrategy.TrailingWhitespace, MatchStrategy.Unicode, MatchStrategy.Indentation })
        {
            Func<string, string> norm = strategy switch
            {
                MatchStrategy.TrailingWhitespace => l => l.TrimEnd(),
                MatchStrategy.Unicode => l => NormalizeUnicode(l).TrimEnd(),
                _ => l => NormalizeUnicode(l).Trim(),
            };
            var normFile = fileLines.Select(norm).ToArray();
            var normOld = oldLines.Select(norm).ToArray();
            var starts = FindLineWindows(normFile, normOld);
            if (starts.Count == 0) continue;
            if (starts.Count > 1 && !replaceAll)
                return Ambiguous(starts.Count, starts.Select(s => s + 1).ToList(), strategy);

            var result = new List<string>(fileLines.Length + (newLines.Length - oldLines.Length) * starts.Count);
            var cursor = 0;
            foreach (var s in starts)
            {
                for (var i = cursor; i < s; i++) result.Add(fileLines[i]);
                var replacement = strategy == MatchStrategy.Indentation
                    ? Reindent(newLines, oldLines, fileLines.AsSpan(s, oldLines.Length).ToArray())
                    : newLines;
                result.AddRange(replacement);
                cursor = s + oldLines.Length;
            }
            for (var i = cursor; i < fileLines.Length; i++) result.Add(fileLines[i]);
            return new EditOutcome
            {
                Success = true, Text = string.Join('\n', result), Strategy = strategy,
                Lines = starts.Select(s => s + 1).ToList(), Replacements = starts.Count,
            };
        }

        return Fail(NotFoundMessage(text, oldText));
    }

    private static string[] TrimTrailingEmpty(string[] lines, bool endsWithNewline) =>
        endsWithNewline && lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;

    private static EditOutcome Fail(string message) => new() { Success = false, Error = message };

    private static EditOutcome Ambiguous(int count, List<int> lines, MatchStrategy strategy)
    {
        var shown = string.Join(", ", lines.Take(20)) + (lines.Count > 20 ? ", …" : "");
        var how = strategy == MatchStrategy.Exact ? "" : $" (matched ignoring {StrategyDescription(strategy)})";
        return Fail($"oldText matches {count} locations{how} at lines {shown}. Include more surrounding context in oldText to make it unique, or set replaceAll=true to replace every occurrence.");
    }

    private static string StrategyDescription(MatchStrategy s) => s switch
    {
        MatchStrategy.TrailingWhitespace => "trailing whitespace",
        MatchStrategy.Unicode => "trailing whitespace and unicode quote/dash/space differences",
        MatchStrategy.Indentation => "indentation",
        _ => "nothing",
    };

    public static List<int> FindAll(string text, string needle)
    {
        var result = new List<int>();
        var i = text.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            result.Add(i);
            i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Non-overlapping start indices where <paramref name="needle"/> matches consecutive lines.</summary>
    private static List<int> FindLineWindows(string[] hay, string[] needle)
    {
        var result = new List<int>();
        var n = needle.Length;
        for (var i = 0; i + n <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < n; j++)
            {
                if (!string.Equals(hay[i + j], needle[j], StringComparison.Ordinal)) { ok = false; break; }
            }
            if (!ok) continue;
            result.Add(i);
            i += n - 1;
        }
        return result;
    }

    public static int LineOf(string text, int index)
    {
        var n = 1;
        var span = text.AsSpan(0, Math.Min(index, text.Length));
        n += span.Count('\n');
        return n;
    }

    /// <summary>Smart quotes → ASCII, dashes → '-', exotic spaces → ' ', ellipsis → "...", zero-width characters removed.</summary>
    public static string NormalizeUnicode(string s)
    {
        var needs = false;
        foreach (var c in s)
            if (c > 0x7F) { needs = true; break; }
        if (!needs) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '‘' or '’' or '‚' or '‛' or '′' or '´' or 'ʼ': sb.Append('\''); break;
                case '“' or '”' or '„' or '‟' or '″' or '«' or '»': sb.Append('"'); break;
                case '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' or '﹘' or '﹣' or '－': sb.Append('-'); break;
                case ' ' or ' ' or (>= ' ' and <= ' ') or ' ' or ' ' or '　': sb.Append(' '); break;
                case '​' or '‌' or '‍' or '⁠' or '﻿': break;
                case '…': sb.Append("..."); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string LeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    /// <summary>
    /// Re-indent <paramref name="newLines"/> to the file's indentation. The matched line pairs teach a mapping
    /// "indent used in oldText → indent found in the file"; new lines with a known indent use it, deeper lines extend the
    /// closest known level (converting spaces ↔ tabs when the file uses the other style), and anything else is shifted
    /// by the delta of the first non-blank line.
    /// </summary>
    internal static string[] Reindent(string[] newLines, string[] oldLines, string[] foundLines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var j = 0; j < oldLines.Length && j < foundLines.Length; j++)
        {
            if (oldLines[j].Trim().Length == 0) continue;
            map.TryAdd(LeadingWhitespace(oldLines[j]), LeadingWhitespace(foundLines[j]));
        }
        if (map.Count == 0 || map.All(kv => kv.Key == kv.Value)) return newLines;

        var k = Array.FindIndex(oldLines, l => l.Trim().Length > 0);
        var oldIndent = LeadingWhitespace(oldLines[k]);
        var fileIndent = map[oldIndent];
        var fileUsesTabs = map.Values.Any(v => v.Contains('\t'));
        var fileUsesSpaces = map.Values.Any(v => v.Contains(' '));
        // Spaces per tab, learned from a pair like ("        ", "\t\t"); default 4.
        var unit = 4;
        foreach (var (o, f) in map)
        {
            if (o.Length > 0 && f.Length > 0 && !o.Contains('\t') && f.All(c => c == '\t') && o.Length % f.Length == 0) { unit = o.Length / f.Length; break; }
            if (o.Length > 0 && f.Length > 0 && o.All(c => c == '\t') && !f.Contains('\t') && f.Length % o.Length == 0) { unit = f.Length / o.Length; break; }
        }
        string Convert(string extra)
        {
            if (fileUsesTabs && !fileUsesSpaces && !extra.Contains('\t'))
                return new string('\t', extra.Length / unit) + new string(' ', extra.Length % unit);
            if (fileUsesSpaces && !fileUsesTabs && extra.Contains('\t'))
                return extra.Replace("\t", new string(' ', unit));
            return extra;
        }

        var result = new string[newLines.Length];
        for (var i = 0; i < newLines.Length; i++)
        {
            var line = newLines[i];
            if (line.Trim().Length == 0) { result[i] = line; continue; }
            var lead = LeadingWhitespace(line);
            var rest = line[lead.Length..];
            if (map.TryGetValue(lead, out var mapped)) { result[i] = mapped + rest; continue; }
            // Deeper than a known level: extend the longest known prefix.
            var baseKey = map.Keys.Where(key => lead.StartsWith(key, StringComparison.Ordinal)).OrderByDescending(key => key.Length).FirstOrDefault();
            if (baseKey is not null) { result[i] = map[baseKey] + Convert(lead[baseKey.Length..]) + rest; continue; }
            // Shallower than every known level: shift by the reference delta.
            if (fileIndent.StartsWith(oldIndent, StringComparison.Ordinal))
                result[i] = fileIndent[oldIndent.Length..] + line;
            else if (oldIndent.Length > fileIndent.Length)
                result[i] = line[Math.Min(lead.Length, oldIndent.Length - fileIndent.Length)..];
            else
                result[i] = line;
        }
        return result;
    }

    private static string NotFoundMessage(string text, string oldText)
    {
        var sb = new StringBuilder("oldText was not found in the file (also tried ignoring trailing whitespace, unicode quote/dash differences and indentation).");
        var hint = ClosestLine(text, oldText);
        if (hint is not null) sb.Append(' ').Append(hint);
        sb.Append(" Re-read the file and copy oldText exactly.");
        return sb.ToString();
    }

    /// <summary>Point the model at the most similar line to the first non-blank line of oldText.</summary>
    private static string? ClosestLine(string text, string oldText)
    {
        var first = oldText.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null || first.Length < 3) return null;
        var normFirst = NormalizeUnicode(first);
        var lines = text.Split('\n');
        var best = -1;
        var bestScore = 0.0;
        for (var i = 0; i < lines.Length && i < 200_000; i++)
        {
            var l = NormalizeUnicode(lines[i].Trim());
            if (l.Length == 0) continue;
            double score;
            if (l == normFirst) score = 1;
            else if (l.Contains(normFirst, StringComparison.Ordinal) || normFirst.Contains(l, StringComparison.Ordinal))
                score = 0.9 * Math.Min(l.Length, normFirst.Length) / Math.Max(l.Length, normFirst.Length);
            else score = TokenOverlap(l, normFirst);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best < 0 || bestScore < 0.5) return null;
        var shown = lines[best].Trim();
        if (shown.Length > 160) shown = shown[..160] + "…";
        return bestScore >= 1
            ? $"Its first line exists at line {best + 1}, but the following lines differ."
            : $"The most similar line is {best + 1}: `{shown}`.";
    }

    private static double TokenOverlap(string a, string b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Intersect(tb).Count();
        return (double)inter / Math.Max(ta.Count, tb.Count);
    }

    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) set.Add(sb.ToString());
        return set;
    }
}
