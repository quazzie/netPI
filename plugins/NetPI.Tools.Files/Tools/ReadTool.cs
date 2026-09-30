using System.Text;

namespace NetPI.Tools.Files;

public sealed class ReadTool(ISettings? settings = null) : FileToolBase(settings)
{
    public const int MaxLines = 2000;
    public const int MaxBytes = 50 * 1024;
    private const long MaxImageBytes = 20L * 1024 * 1024;
    /// <summary>
    /// Above this size a file is paged by streaming instead of being read whole. A field, not a constant, so a test can
    /// put a small file on the streaming path and assert what it does (the production value is what ships).
    /// </summary>
    internal static long StreamingThresholdBytes = 32L * 1024 * 1024;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "read",
        Label = "Read",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "path",
        Description = $"Read a text file (up to {MaxLines} lines per call; page with offset/limit) or an image.",
        Help =
            "Returns the file content (line endings normalized to \\n, no line-number prefixes), " +
            $"at most {MaxLines} lines (and about {MaxBytes / 1024}KB) per call; a longer file ends with the offset to continue. " +
            "offset is 1-based; negative values count from the end. Images (png, jpg, gif, webp) are returned as images when " +
            "the model supports them.",
        Parameters = Schema.Object(
            ("path", Schema.Str(""), true),
            ("offset", Schema.Int("1-based; negative from the end"), false),
            ("limit", Schema.Int(""), false)),
        PromptGuidelines = [UseFileTools],
    };

    protected override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var path = args.Str(PathNames);
        if (string.IsNullOrWhiteSpace(path)) return MissingArg("path", "{\"path\": \"src/Program.cs\"}");
        var full = ctx.ResolvePath(path);
        if (Directory.Exists(full))
            return ToolResult.Error($"{full} is a directory, not a file. Use the ls tool to list it, or find/grep to search in it.");
        if (!File.Exists(full)) return NotFound(ctx, full);

        var fi = new FileInfo(full);
        if (TextCodec.IsImagePath(full)) return await ReadImageAsync(ctx, fi, ct).ConfigureAwait(false);

        var offsetArg = args.Int("offset", "start_line", "startLine", "line", "from", "start");
        var limitArg = args.Int("limit", "lines", "count", "max_lines", "maxLines", "n");
        var limit = Math.Clamp(limitArg ?? MaxLines, 1, MaxLines);

        var maxBytes = PageBytes();
        if (fi.Length > StreamingThresholdBytes) return ReadLarge(ctx, fi, offsetArg, limit, maxBytes, ct);

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (TextCodec.IsBinary(bytes))
            return ToolResult.Error($"{full} appears to be a binary file ({PathDisplay.FormatSize(bytes.Length)}); read only displays text. Use the shell (e.g. `file`, `xxd | head`) to inspect it.");
        var doc = TextCodec.Decode(bytes);
        var lines = TextCodec.SplitLines(doc.Text);
        return Page(ctx, full, lines, lines.Length, offsetArg, limit, maxBytes, doc.DetectedEol, doc.Bom, doc.Legacy);
    }

    /// <summary>
    /// A page is at most MaxBytes, and fits the tool result limit (agent.maxToolResultChars): the runner would cut a longer
    /// result in the middle, while a page ends with the offset to continue.
    /// </summary>
    internal int PageBytes() => ToolResultLimit.Fit(Settings, MaxBytes, 400);

    /// <summary>
    /// One page. <paramref name="totalArg"/> null means the file was paged by streaming and its line count was never
    /// needed, so it is not known - reading a multi-gigabyte file to the end to print a number is the work this avoids.
    /// Everything that needs a count is guarded on it, and <paramref name="moreAfterWindow"/> says whether the window we
    /// were given is followed by more lines (the one extra line a streamed read costs, so the note the model reads is
    /// true rather than hopeful).
    /// </summary>
    private static ToolResult Page(ToolContext ctx, string full, IReadOnlyList<string> lines, int? totalArg, int? offsetArg, int limit,
        int maxBytes, EolStyle? eol, bool bom, bool legacy, int firstLineNumber = 1, bool moreAfterWindow = false)
    {
        var total = totalArg;
        object Details(int start, int end, bool truncated) => new
        {
            path = full,
            startLine = start,
            endLine = end,
            totalLines = total,
            truncated,
            eol = TextCodec.EolName(eol),
            bom,
            encoding = legacy ? "latin1" : null,
        };

        if (total == 0) return ToolResult.Ok("(empty file)", Details(0, 0, false));

        var offset = offsetArg ?? 1;
        if (offset < 0 && total is { } known) offset = Math.Max(1, known + offset + 1); // -100 = last 100 lines
        if (offset == 0) offset = 1;
        if (total is { } t && offset > t)
            return ToolResult.Error($"offset {offset} is past the end of the file: {Rel(ctx, full)} has {t} line{(t == 1 ? "" : "s")}.", Details(0, 0, false));

        var sb = new StringBuilder();
        var bytes = 0;
        var taken = 0;
        string? lineNote = null;
        for (var i = offset - firstLineNumber; i < lines.Count && taken < limit; i++)
        {
            var line = lines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + lineBytes > maxBytes)
            {
                if (taken == 0)
                {
                    // A single enormous line (minified file): show its beginning.
                    var cut = CutToBytes(line, maxBytes);
                    sb.Append(cut);
                    taken = 1;
                    lineNote = $"[Line {offset} is {PathDisplay.FormatSize(lineBytes)} long; showing its first {PathDisplay.FormatSize(maxBytes)}. Use the shell (e.g. `cut -c`) or grep to inspect the rest.]";
                }
                break;
            }
            if (taken > 0) sb.Append('\n');
            sb.Append(line);
            bytes += lineBytes;
            taken++;
        }
        var end = offset + taken - 1;
        var more = moreAfterWindow || (total is { } tt && end < tt);
        var truncated = more || lineNote is not null;
        if (lineNote is not null) sb.Append("\n\n").Append(lineNote);
        if (more)
            sb.Append("\n\n").Append(total is { } knownTotal
                ? $"[Showing lines {offset}-{end} of {knownTotal}. Use offset={end + 1} to continue.]"
                : $"[Showing lines {offset}-{end}; more lines follow. Use offset={end + 1} to continue.]");
        return ToolResult.Ok(sb.ToString(), Details(offset, end, truncated));
    }

    private static string CutToBytes(string s, int maxBytes)
    {
        var len = Math.Min(s.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, len)) > maxBytes) len = (int)(len * 0.9);
        if (len > 0 && len < s.Length && char.IsHighSurrogate(s[len - 1])) len--;
        return s[..len];
    }

    /// <summary>
    /// Huge files: stream the page instead of loading the whole file, and <b>stop at the end of the page</b>. Reading a
    /// 2 GB log to count its lines for every page is the work this avoids, so a page that does not reach the end of the
    /// file reports no line count (<c>totalLines</c> is null) and says that more lines follow - which is what one extra
    /// <see cref="StreamReader.ReadLine"/> buys. A page that <em>does</em> reach the end knows the exact total, and so
    /// does every negative offset: one pass with a ring buffer of the last <c>min(|offset|, limit)</c> lines answers both
    /// "how many lines" and "the last N", where the old code counted to the end and then started over.
    /// </summary>
    private static ToolResult ReadLarge(ToolContext ctx, FileInfo fi, int? offsetArg, int limit, int maxBytes, CancellationToken ct)
    {
        using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var head = new byte[64 * 1024];
        var headLen = fs.Read(head, 0, head.Length);
        if (TextCodec.IsBinary(head.AsSpan(0, headLen)))
            return ToolResult.Error($"{fi.FullName} appears to be a binary file ({PathDisplay.FormatSize(fi.Length)}); read only displays text.");
        // The encoding comes from the same sample the small path decodes, so a huge UTF-16 or Latin-1 file reads the way
        // its content says it should and not as whatever UTF-8 makes of it (a sample cut mid-character is not a legacy
        // file - DetectEncoding knows the difference).
        var (encoding, legacy, bomLength) = TextCodec.DetectEncoding(head.AsSpan(0, headLen));
        var sample = TextCodec.Decode(head.AsSpan(0, headLen).ToArray());
        fs.Position = bomLength;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);

        var offset = offsetArg ?? 1;
        var window = new List<string>(Math.Min(limit, 4096));
        var total = -1;          // -1 = not known (the page did not reach the end of the file)
        var moreAfterWindow = false;

        if (offset < 0)
        {
            var o = -offset; // the caller wants the last o lines
            if (o <= limit)
            {
                // The whole window is the last o lines, so one pass with a ring of o answers both the count and the page.
                var ring = new string?[o];
                var at = 0;
                var n = 0;
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    ring[at] = line;
                    at = (at + 1) % o;
                    n++;
                    if ((n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                }
                total = n;
                offset = Math.Max(1, total - o + 1);
                // The ring in file order: when it wrapped, the oldest entry is the slot the next write would take;
                // before that it is the first one written.
                var oldest = n < o ? 0 : n % o;
                for (var k = 0; k < Math.Min(n, o); k++) window.Add(ring[(oldest + k) % o]!);
                return Page(ctx, fi.FullName, window, total, offset, limit, maxBytes,
                    sample.DetectedEol, bomLength > 0, legacy || sample.Legacy, firstLineNumber: offset);
            }
            // More lines were asked for than a page holds, so the window ends before the last line and a ring would have
            // to be bigger than a page to catch it: count first, then read the page (as before), and the count is exact.
            var seen = 0;
            while (reader.ReadLine() is not null) { seen++; if ((seen & 0xFFFF) == 0) ct.ThrowIfCancellationRequested(); }
            total = seen;
            offset = Math.Max(1, total - o + 1);
            fs.Position = bomLength;
            reader.DiscardBufferedData();
            var lineNo = 0;
            string? line2;
            while ((line2 = reader.ReadLine()) is not null)
            {
                lineNo++;
                if (lineNo >= offset) window.Add(line2);
                if (window.Count >= limit) break;
            }
            moreAfterWindow = offset + window.Count - 1 < total;
        }
        else
        {
            if (offset == 0) offset = 1;
            var lineNo = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNo++;
                if (lineNo >= offset) window.Add(line);
                if (window.Count >= limit)
                {
                    // The page is full. One more line decides whether there is anything after it, which is the only
                    // reason to read past the page: the note the model reads has to be true.
                    moreAfterWindow = reader.ReadLine() is not null;
                    break;
                }
                if ((lineNo & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            }
            if (!moreAfterWindow) total = lineNo;   // the end of the file was reached: the count is exact
        }
        return Page(ctx, fi.FullName, window, total < 0 ? null : total, offset, limit, maxBytes,
            sample.DetectedEol, bomLength > 0, legacy || sample.Legacy, firstLineNumber: offset, moreAfterWindow: moreAfterWindow);
    }

    private static async Task<ToolResult> ReadImageAsync(ToolContext ctx, FileInfo fi, CancellationToken ct)
    {
        var mediaType = TextCodec.ImageMediaType(fi.FullName);
        var details = new { path = fi.FullName, image = true, mediaType, bytes = fi.Length };
        if (ctx.Model?.SupportsImages != true)
            return ToolResult.Ok($"{Rel(ctx, fi.FullName)} is an image ({mediaType}, {PathDisplay.FormatSize(fi.Length)}). The current model cannot view images, so its content is not shown.", details);
        if (fi.Length > MaxImageBytes)
            return ToolResult.Error($"{Rel(ctx, fi.FullName)} is too large to attach ({PathDisplay.FormatSize(fi.Length)}; max {PathDisplay.FormatSize(MaxImageBytes)}).", details);
        var data = await File.ReadAllBytesAsync(fi.FullName, ct).ConfigureAwait(false);
        return new ToolResult
        {
            Content = $"Image {Rel(ctx, fi.FullName)} ({mediaType}, {PathDisplay.FormatSize(fi.Length)})",
            Images = [new ImagePart { MediaType = mediaType, Data = Convert.ToBase64String(data) }],
            Details = details,
        };
    }
}
