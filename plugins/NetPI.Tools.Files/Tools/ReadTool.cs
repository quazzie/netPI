using System.Text;

namespace NetPI.Tools.Files;

public sealed class ReadTool(ISettings? settings = null) : FileToolBase(settings)
{
    public const int MaxLines = 2000;
    public const int MaxBytes = 50 * 1024;
    private const long MaxImageBytes = 20L * 1024 * 1024;
    private const long StreamingThreshold = 32L * 1024 * 1024;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "read",
        Label = "Read",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "path",
        Description =
            "Read a text file. Returns the file content (line endings normalized to \\n, no line-number prefixes), " +
            $"at most {MaxLines} lines / {MaxBytes / 1024}KB per call; use offset/limit to page through larger files. " +
            "Images (png, jpg, gif, webp) are returned as images when the model supports them.",
        Parameters = Schema.Object(
            ("path", Schema.Str("File path, absolute or relative to the working directory."), true),
            ("offset", Schema.Int("1-based line number to start from (default 1). Negative values count from the end."), false),
            ("limit", Schema.Int($"Maximum number of lines to return (default and max {MaxLines})."), false)),
        PromptGuidelines =
        [
            "Use read (not cat/head/tail in the shell) to look at files; always read a file before editing it.",
            $"read returns at most {MaxLines} lines / {MaxBytes / 1024}KB: when the output ends with a 'Use offset=N to continue' footer, call read again with that offset if you need more.",
            "Line endings are normalized to \\n in read output; the edit and write tools preserve each file's original CRLF/LF style automatically.",
        ],
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

        if (fi.Length > StreamingThreshold) return ReadLarge(ctx, fi, offsetArg, limit, ct);

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (TextCodec.IsBinary(bytes))
            return ToolResult.Error($"{full} appears to be a binary file ({PathDisplay.FormatSize(bytes.Length)}); read only displays text. Use the shell (e.g. `file`, `xxd | head`) to inspect it.");
        var doc = TextCodec.Decode(bytes);
        var lines = TextCodec.SplitLines(doc.Text);
        return Page(ctx, full, lines, lines.Length, offsetArg, limit, doc.DetectedEol, doc.Bom, doc.Legacy);
    }

    private static ToolResult Page(ToolContext ctx, string full, IReadOnlyList<string> lines, int total, int? offsetArg, int limit,
        EolStyle? eol, bool bom, bool legacy, int firstLineNumber = 1)
    {
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
        if (offset < 0) offset = Math.Max(1, total + offset + 1); // -100 = last 100 lines
        if (offset == 0) offset = 1;
        if (offset > total)
            return ToolResult.Error($"offset {offset} is past the end of the file: {Rel(ctx, full)} has {total} line{(total == 1 ? "" : "s")}.", Details(0, 0, false));

        var sb = new StringBuilder();
        var bytes = 0;
        var taken = 0;
        string? lineNote = null;
        for (var i = offset - firstLineNumber; i < lines.Count && taken < limit; i++)
        {
            var line = lines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + lineBytes > MaxBytes)
            {
                if (taken == 0)
                {
                    // A single enormous line (minified file): show its beginning.
                    var cut = CutToBytes(line, MaxBytes);
                    sb.Append(cut);
                    taken = 1;
                    lineNote = $"[Line {offset} is {PathDisplay.FormatSize(lineBytes)} long; showing its first {PathDisplay.FormatSize(MaxBytes)}. Use the shell (e.g. `cut -c`) or grep to inspect the rest.]";
                }
                break;
            }
            if (taken > 0) sb.Append('\n');
            sb.Append(line);
            bytes += lineBytes;
            taken++;
        }
        var end = offset + taken - 1;
        var truncated = end < total || lineNote is not null;
        if (lineNote is not null) sb.Append("\n\n").Append(lineNote);
        if (end < total)
            sb.Append("\n\n").Append($"[Showing lines {offset}-{end} of {total}. Use offset={end + 1} to continue.]");
        return ToolResult.Ok(sb.ToString(), Details(offset, end, truncated));
    }

    private static string CutToBytes(string s, int maxBytes)
    {
        var len = Math.Min(s.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, len)) > maxBytes) len = (int)(len * 0.9);
        if (len > 0 && len < s.Length && char.IsHighSurrogate(s[len - 1])) len--;
        return s[..len];
    }

    /// <summary>Huge files: stream lines instead of loading the whole file.</summary>
    private static ToolResult ReadLarge(ToolContext ctx, FileInfo fi, int? offsetArg, int limit, CancellationToken ct)
    {
        using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var head = new byte[64 * 1024];
        var headLen = fs.Read(head, 0, head.Length);
        if (TextCodec.IsBinary(head.AsSpan(0, headLen)))
            return ToolResult.Error($"{fi.FullName} appears to be a binary file ({PathDisplay.FormatSize(fi.Length)}); read only displays text.");
        var sample = TextCodec.Decode(head[..headLen]);
        fs.Position = 0;
        using var reader = new StreamReader(fs, TextCodec.Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

        // Negative offsets need the total first.
        var total = -1;
        var offset = offsetArg ?? 1;
        if (offset < 0)
        {
            total = 0;
            while (reader.ReadLine() is not null) { total++; if ((total & 0xFFFF) == 0) ct.ThrowIfCancellationRequested(); }
            offset = Math.Max(1, total + offset + 1);
            fs.Position = 0;
            reader.DiscardBufferedData();
        }
        if (offset == 0) offset = 1;
        var window = new List<string>(Math.Min(limit, 4096));
        var lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (lineNo >= offset && window.Count < limit) window.Add(line);
            if ((lineNo & 0xFFFF) == 0) ct.ThrowIfCancellationRequested(); // keep counting for the total
        }
        total = lineNo;
        return Page(ctx, fi.FullName, window, total, offset, limit, sample.DetectedEol, sample.Bom, sample.Legacy, firstLineNumber: offset);
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
