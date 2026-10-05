using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Files;

public enum GrepOutputMode { Content, Files, Count }

public sealed class GrepOptions
{
    public required string Pattern { get; init; }
    public bool IgnoreCase { get; init; }
    public bool Literal { get; init; }
    public int Context { get; init; }
    public int MaxResults { get; init; } = 200;
    public GrepOutputMode Mode { get; init; } = GrepOutputMode.Content;
    public bool Multiline { get; init; }
    public GlobFilter? Filter { get; init; }
    /// <summary>Output paths are relative to this directory (the tool's cwd).</summary>
    public required string DisplayBase { get; init; }
    public int MaxLineLength { get; init; } = 500;
    public long MaxFileSize { get; init; } = 32L * 1024 * 1024;
    public int MaxOutputChars { get; init; } = 50_000;
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Walk ignore rules (.gitignore + built-in skip list).</summary>
    public bool RespectIgnore { get; init; } = true;
}

public sealed class GrepResult
{
    public string Output { get; init; } = "";
    /// <summary>Matching lines (content), or matches (multiline).</summary>
    public int Matches { get; init; }
    public int FilesMatched { get; init; }
    public int FilesSearched { get; init; }
    public bool Truncated { get; init; }
    public List<string> Notes { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>
/// Parallel regex search over LF-normalized file contents. Skips binary files, ignored paths and huge files.
/// Results are ordered by path (walk order) and deterministic.
/// </summary>
public static class GrepEngine
{
    /// <summary>
    /// The shared work pool for grep: every search on the machine draws its file searches from these slots, so
    /// however many agents grep at once, the grep work in flight stays the size of one search — the CPU load and
    /// the in-flight file buffers are bounded together instead of stacking per search. Measured with the pool at
    /// the processor count: a single search is unchanged, and concurrent searches share the slots.
    /// </summary>
    private static readonly SemaphoreSlim Work = new(Math.Max(2, Environment.ProcessorCount));

    /// <summary>The high water mark of file searches running at once: the bound the tests check.</summary>
    internal static int PeakInFlight;
    private static int _inFlight;

    private sealed record OutLine(int Line, bool IsMatch, string Text);

    private sealed class FileResult
    {
        public required string Rel { get; init; }
        public int MatchCount { get; set; }
        /// <summary>Groups of contiguous lines (content mode).</summary>
        public List<List<OutLine>> Groups { get; } = [];
        public string? Skipped { get; set; }
    }

    public static Regex BuildRegex(GrepOptions o, out string? note)
    {
        note = null;
        var opts = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (o.IgnoreCase) opts |= RegexOptions.IgnoreCase;
        var pattern = o.Literal ? Regex.Escape(o.Pattern) : o.Pattern;
        try
        {
            return new Regex(pattern, opts, o.RegexTimeout);
        }
        catch (ArgumentException ex) when (!o.Literal)
        {
            note = $"[pattern is not a valid regular expression ({ex.Message.Split('\n')[0].Trim()}); searched for it literally]";
            return new Regex(Regex.Escape(o.Pattern), opts, o.RegexTimeout);
        }
    }

    public static async Task<GrepResult> RunAsync(string target, GrepOptions o, CancellationToken ct = default)
    {
        var regex = BuildRegex(o, out var regexNote);
        var notes = new List<string>();
        if (regexNote is not null) notes.Add(regexNote);
        // A whole-text pre-check is only equivalent to per-line matching without string anchors.
        var canPrecheck = !o.Multiline && !Regex.IsMatch(o.Literal ? "" : o.Pattern, @"\\[AzZG]");

        // Collect candidate files.
        List<(string Full, string Rel)> files;
        if (File.Exists(target))
        {
            files = [(Path.GetFullPath(target), PathDisplay.Relative(o.DisplayBase, Path.GetFullPath(target)))];
        }
        else
        {
            files = [];
            foreach (var e in FileWalker.Walk(target, new WalkOptions { IncludeDirs = false, RespectIgnore = o.RespectIgnore }, ct: ct))
            {
                if (o.Filter is { IsEmpty: false } f && !f.IsMatch(e.RelPath)) continue;
                files.Add((e.FullPath, PathDisplay.Relative(o.DisplayBase, e.FullPath)));
            }
        }

        var results = new List<FileResult>();
        int totalMatches = 0, filesMatched = 0, binary = 0, large = 0, timedOut = 0, unreadable = 0;
        var filesSearched = 0;
        var limitHit = false;
        const int chunk = 64;
        for (var start = 0; start < files.Count && !limitHit; start += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var slice = files.GetRange(start, Math.Min(chunk, files.Count - start));
            var chunkResults = new FileResult?[slice.Count];
            // Every grep on the machine draws from these shared slots: the work in flight is the size of one search. A
            // search waiting for a slot awaits it, so k searches at once park no k × cores pool threads on the semaphore.
            await Parallel.ForEachAsync(Enumerable.Range(0, slice.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (i, token) =>
            {
                await Work.WaitAsync(token).ConfigureAwait(false);
                var inFlight = Interlocked.Increment(ref _inFlight);
                if (inFlight > PeakInFlight) PeakInFlight = inFlight;
                try { chunkResults[i] = SearchFile(slice[i].Full, slice[i].Rel, regex, o, canPrecheck); }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    Work.Release();
                }
            }).ConfigureAwait(false);
            foreach (var r in chunkResults)
            {
                if (r is null) continue;
                filesSearched++;
                switch (r.Skipped)
                {
                    case "binary": binary++; continue;
                    case "large": large++; continue;
                    case "timeout": timedOut++; continue;
                    case "unreadable": unreadable++; continue;
                }
                if (r.MatchCount == 0) continue;
                results.Add(r);
                filesMatched++;
                totalMatches += r.MatchCount;
                // One more than the limit proves that results were cut.
                var reached = o.Mode == GrepOutputMode.Content ? totalMatches > o.MaxResults : filesMatched > o.MaxResults;
                if (reached) limitHit = true;
            }
        }

        // Format.
        var sb = new StringBuilder();
        var emittedMatches = 0;
        var emittedFiles = 0;
        var truncated = false;
        bool Append(string line)
        {
            if (sb.Length + line.Length + 1 > o.MaxOutputChars) { truncated = true; return false; }
            sb.Append(line).Append('\n');
            return true;
        }

        foreach (var r in results)
        {
            if (truncated) break;
            if (o.Mode == GrepOutputMode.Files)
            {
                if (emittedFiles >= o.MaxResults) { truncated = true; break; }
                if (!Append(r.Rel)) break;
                emittedFiles++;
                continue;
            }
            if (o.Mode == GrepOutputMode.Count)
            {
                if (emittedFiles >= o.MaxResults) { truncated = true; break; }
                if (!Append($"{r.Rel}: {r.MatchCount}")) break;
                emittedFiles++;
                continue;
            }
            foreach (var g in r.Groups)
            {
                if (emittedMatches >= o.MaxResults) { truncated = true; break; }
                if (o.Context > 0 && sb.Length > 0 && !Append("--")) break;
                foreach (var l in g)
                {
                    if (l.IsMatch && emittedMatches >= o.MaxResults) { truncated = true; break; }
                    var line = l.IsMatch ? $"{r.Rel}:{l.Line}: {l.Text}" : $"{r.Rel}-{l.Line}- {l.Text}";
                    if (!Append(line)) break;
                    if (l.IsMatch) emittedMatches++;
                }
                if (truncated) break;
            }
            if (truncated) break;
            emittedFiles++;
        }
        if (limitHit) truncated = true;

        // Skipped binaries/huge files only matter when they may explain a missing result.
        var explain = results.Count == 0 || files.Count == 1;
        if (binary > 0 && explain) notes.Add($"[{binary} binary file{(binary == 1 ? "" : "s")} skipped]");
        if (large > 0 && explain) notes.Add($"[{large} file{(large == 1 ? "" : "s")} larger than {PathDisplay.FormatSize(o.MaxFileSize)} skipped]");
        if (timedOut > 0) notes.Add($"[{timedOut} file{(timedOut == 1 ? "" : "s")} skipped: regex timed out (simplify the pattern)]");
        if (unreadable > 0) notes.Add($"[{unreadable} file{(unreadable == 1 ? "" : "s")} could not be read]");

        return new GrepResult
        {
            Output = sb.ToString().TrimEnd('\n'),
            Matches = o.Mode == GrepOutputMode.Content ? emittedMatches : totalMatches,
            FilesMatched = o.Mode == GrepOutputMode.Content ? emittedFiles : Math.Min(emittedFiles, filesMatched),
            // The files actually searched: the cap can stop the scan early, and the number must say so.
            FilesSearched = filesSearched,
            Truncated = truncated,
            Notes = notes,
        };
    }

    private static FileResult SearchFile(string full, string rel, Regex regex, GrepOptions o, bool canPrecheck)
    {
        var result = new FileResult { Rel = rel };
        byte[] bytes;
        try
        {
            var fi = new FileInfo(full);
            if (fi.Length > o.MaxFileSize) { result.Skipped = "large"; return result; }
            bytes = File.ReadAllBytes(full);
        }
        catch
        {
            result.Skipped = "unreadable";
            return result;
        }
        if (TextCodec.IsBinary(bytes)) { result.Skipped = "binary"; return result; }
        var text = TextCodec.Decode(bytes).Text;
        try
        {
            if (canPrecheck && !regex.IsMatch(text)) return result;
            if (o.Multiline) SearchMultiline(text, regex, o, result);
            else SearchLines(text, regex, o, result);
        }
        catch (RegexMatchTimeoutException)
        {
            result.Skipped = "timeout";
            result.Groups.Clear();
            result.MatchCount = 0;
        }
        return result;
    }

    private static void SearchLines(string text, Regex regex, GrepOptions o, FileResult result)
    {
        var lines = TextCodec.SplitLines(text);
        var matchCols = new Dictionary<int, int>();
        var matchIdx = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = regex.Match(lines[i]);
            if (!m.Success) continue;
            matchIdx.Add(i);
            matchCols[i] = m.Index;
            // Per-file cap: never need more than MaxResults matches from one file.
            if (o.Mode == GrepOutputMode.Content && matchIdx.Count > o.MaxResults) break;
            if (o.Mode == GrepOutputMode.Files) break;
        }
        result.MatchCount = matchIdx.Count;
        if (o.Mode != GrepOutputMode.Content || matchIdx.Count == 0) return;
        BuildGroups(lines, matchIdx.Select(i => (i, i)).ToList(), matchCols, o, result);
    }

    private static void SearchMultiline(string text, Regex regex, GrepOptions o, FileResult result)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        int LineAt(int index)
        {
            var k = lineStarts.BinarySearch(index);
            return k >= 0 ? k : ~k - 1;
        }
        var spans = new List<(int From, int To)>();
        var cols = new Dictionary<int, int>();
        var count = 0;
        for (var m = regex.Match(text); m.Success; m = m.NextMatch())
        {
            var from = LineAt(m.Index);
            var to = LineAt(Math.Max(m.Index, m.Index + m.Length - 1));
            if (m.Length > 0 && text[m.Index + m.Length - 1] == '\n' && to > from) to--; // match ending with the newline
            spans.Add((from, Math.Min(to, from + 50)));
            cols.TryAdd(from, m.Index - lineStarts[from]);
            count++;
            if (o.Mode == GrepOutputMode.Files || (o.Mode == GrepOutputMode.Content && count > o.MaxResults)) break;
        }
        result.MatchCount = count;
        if (o.Mode != GrepOutputMode.Content || count == 0) return;
        BuildGroups(TextCodec.SplitLines(text), spans, cols, o, result);
    }

    private static void BuildGroups(string[] lines, List<(int From, int To)> spans, Dictionary<int, int> cols, GrepOptions o, FileResult result)
    {
        var isMatch = new HashSet<int>();
        foreach (var (f, t) in spans) for (var i = f; i <= t && i < lines.Length; i++) isMatch.Add(i);
        var ordered = isMatch.OrderBy(i => i).ToList();
        List<OutLine>? group = null;
        var groupEnd = -1; // last line index included in the current group
        foreach (var m in ordered)
        {
            var from = Math.Max(0, m - o.Context);
            var to = Math.Min(lines.Length - 1, m + o.Context);
            if (group is null || from > groupEnd + 1)
            {
                group = [];
                result.Groups.Add(group);
                groupEnd = from - 1;
            }
            for (var i = Math.Max(from, groupEnd + 1); i <= to; i++)
                group.Add(new OutLine(i + 1, isMatch.Contains(i), Clip(lines[i], cols.TryGetValue(i, out var c) ? c : 0, o.MaxLineLength)));
            groupEnd = Math.Max(groupEnd, to);
        }
    }

    /// <summary>Shorten long lines, keeping the match column visible.</summary>
    internal static string Clip(string line, int matchCol, int max)
    {
        if (line.Length <= max) return line;
        var start = matchCol > max - 100 ? Math.Max(0, matchCol - 100) : 0;
        var len = Math.Min(max, line.Length - start);
        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        sb.Append(line, start, len);
        var rest = line.Length - start - len;
        if (rest > 0) sb.Append($" …[+{rest} chars]");
        return sb.ToString();
    }
}
