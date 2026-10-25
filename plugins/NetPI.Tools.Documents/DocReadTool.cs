using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Documents;

/// <summary>
/// <c>doc.read</c>: the text of a document (PDF, .docx, .xlsx, .pptx) with its page/section structure,
/// bounded per call (a page of at most <see cref="MaxBytes"/> under the tool result limit, paged with
/// offset/limit like <c>read</c>). The extracted text is cached per file (path + mtime + size + password),
/// so a repeated read of a long document does not re-extract it.
/// </summary>
public sealed class DocReadTool(ISettings? settings, DocCache cache) : IAgentTool
{
    /// <summary>A page holds at most this many lines (like <c>read</c>), and at most <see cref="MaxBytes"/>.</summary>
    public const int MaxLines = 2000;
    public const int MaxBytes = 50 * 1024;
    /// <summary>Larger files are refused up front: extraction loads the document's parts, and the bound keeps a
    /// pathological file from pinning the process's memory.</summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    public DocReadTool(ISettings? settings = null) : this(settings, new DocCache(settings)) { }

    private readonly DocCache _cache = cache;
    private readonly ISettings? _settings = settings;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "doc.read",
        Label = "Document",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "path",
        Description = "Read a document (PDF, .docx, .xlsx, .pptx) as text: pages and sections with structure, bounded per call (page with offset/limit).",
        Help =
            "Extracts the document's text with its structure - PDF pages ('== Page N =='), docx headings/paragraphs/table rows, " +
            "xlsx sheets and rows ('== Sheet: name ==', 'row: c1 | c2'), pptx slides ('== Slide N =='). " +
            $"At most {MaxLines} lines / {MaxBytes / 1024}KB per call; a longer document ends with the offset to continue. " +
            "offset is 1-based; negative counts from the end. password: for an encrypted PDF (without it the result says the document " +
            "is encrypted). Scanned PDFs (images, no text layer) are reported, not guessed. The extracted text is cached per file " +
            "(path + mtime + size), so repeated reads of a long document are cheap.",
        Parameters = ToolSchema.Object(
            ("path", ToolSchema.Str(""), true),
            ("password", ToolSchema.Str("The password of an encrypted PDF"), false),
            ("offset", ToolSchema.Int("1-based; negative from the end"), false),
            ("limit", ToolSchema.Int(""), false)),
        PromptGuidelines =
        [
            "Use doc.read for documents (PDF, .docx, .xlsx, .pptx); read is for text files.",
        ],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            return await RunAsync(context, new ToolArgs(args), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Error($"Permission denied: {ex.Message}");
        }
        catch (PathTooLongException ex)
        {
            return ToolResult.Error($"Path too long: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Error($"I/O error: {ex.Message}");
        }
        catch (DocumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"doc.read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var path = args.Str(ToolPathArgs.PathNames);
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error("Missing required argument 'path'. Example: {\"path\": \"report.pdf\"}");
        var full = ctx.ResolvePath(path);
        if (Directory.Exists(full))
            return ToolResult.Error($"{full} is a directory, not a file. Use the ls tool to list it.");
        if (!File.Exists(full))
            return ToolResult.Error($"File not found: {full}.");

        var fi = new FileInfo(full);
        if (fi.Length > MaxFileBytes)
            return ToolResult.Error($"{full} is {fi.Length / 1024 / 1024} MB; doc.read reads at most {MaxFileBytes / 1024 / 1024} MB.");

        var password = args.Str("password");
        var head = await ReadHeadAsync(full, ct).ConfigureAwait(false);
        var (format, formatError) = DetectFormat(full, head);
        if (formatError is not null)
            return ToolResult.Error($"{full}: {formatError}");
        if (format is null)
            return ToolResult.Error($"{full} is not a supported document format (pdf, docx, xlsx, pptx). Use read for text files, or the shell to inspect what it is.");

        // The cache key is the file's identity plus the password: a re-saved file (new mtime/size) or a
        // different password is a different extraction, and a hit is the whole point of the cache.
        var key = $"{full}\0{fi.LastWriteTimeUtc.Ticks}\0{fi.Length}\0{password ?? ""}";
        var cached = _cache.Get(key);
        string text;
        if (cached is not null)
        {
            text = cached;
        }
        else
        {
            text = format switch
            {
                "pdf" => PdfExtractor.Extract(full, password),
                "docx" => OfficeExtractors.ExtractDocx(full),
                "xlsx" => OfficeExtractors.ExtractXlsx(full),
                _ => OfficeExtractors.ExtractPptx(full),
            };
            _cache.Add(key, text);
        }
        // captured before the Add: "served from the cache" means it was there before this call
        var cacheHit = cached is not null;

        var offsetArg = args.Int("offset", "start_line", "startLine", "line", "from", "start");
        var limitArg = args.Int("limit", "lines", "count", "max_lines", "maxLines", "n");
        var limit = Math.Clamp(limitArg ?? MaxLines, 1, MaxLines);
        var maxBytes = PageBytes();
        return Page(ctx, full, format, text, offsetArg, limit, maxBytes, cacheHit);
    }

    /// <summary>The page's byte cap, kept under the tool result limit (agent.maxToolResultChars) with room for its notes.</summary>
    internal int PageBytes() => ToolResultLimit.Fit(_settings, MaxBytes, 400);

    /// <summary>One page of the extracted text, like <c>read</c>: at most <paramref name="limit"/> lines and
    /// <paramref name="maxBytes"/> bytes, ending with the offset to continue when more follows.</summary>
    private static ToolResult Page(ToolContext ctx, string full, string format, string text, int? offsetArg, int limit, int maxBytes, bool cacheHit)
    {
        var lines = text.Split('\n');
        var total = lines.Length;

        object Details(int start, int end, bool truncated) => new
        {
            path = full,
            format,
            startLine = start,
            endLine = end,
            totalLines = total,
            truncated,
            cacheHit,
            bytes = new FileInfo(full).Length,
        };

        if (total == 0) return ToolResult.Ok("(empty document)", Details(0, 0, false));

        var offset = offsetArg ?? 1;
        if (offset < 0)
        {
            offset = Math.Max(1, total + offset + 1); // -100 = last 100 lines
        }
        if (offset == 0) offset = 1;
        if (offset > total)
            return ToolResult.Error($"offset {offset} is past the end of the document: it has {total} lines.", Details(0, 0, false));

        var sb = new StringBuilder();
        var bytes = 0;
        var taken = 0;
        for (var i = offset - 1; i < lines.Length && taken < limit; i++)
        {
            var line = lines[i];
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + lineBytes > maxBytes)
            {
                if (taken == 0)
                {
                    // A single line longer than the whole page: show its beginning.
                    var cut = CutToBytes(line, maxBytes);
                    sb.Append(cut);
                    taken = 1;
                    sb.Append($"\n\n[Line {offset} is longer than the page; showing its first {cut.Length} characters.]");
                    break;
                }
                break;
            }
            if (taken > 0) sb.Append('\n');
            sb.Append(line);
            bytes += lineBytes;
            taken++;
        }
        var end = offset + taken - 1;
        var more = end < total;
        if (more)
            sb.Append($"\n\n[Showing lines {offset}-{end} of {total}. Use offset={end + 1} to continue.]");
        return ToolResult.Ok(sb.ToString(), Details(offset, end, more));
    }

    private static string CutToBytes(string s, int maxBytes)
    {
        var len = Math.Min(s.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, len)) > maxBytes) len = (int)(len * 0.9);
        if (len > 0 && len < s.Length && char.IsHighSurrogate(s[len - 1])) len--;
        return s[..len];
    }

    // ------------------------------------------------------------------ format detection

    /// <summary>
    /// The format by extension first, then by content (a missing or lying extension is answered by the bytes):
    /// %PDF is a PDF; a ZIP with word/, xl/ or ppt/ parts is the matching Office file. A mismatch (a .pdf that
    /// is a ZIP, a .docx that is not a ZIP) is a clear "not that format" answer (the <c>error</c>), not a guess.
    /// </summary>
    internal static (string? Format, string? Error) DetectFormat(string full, byte[] head)
    {
        var ext = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        if (head.Length >= 4 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F')
            return ext is "" or "pdf" ? ("pdf", null) : (null, $"it is a PDF, not a {Ext(ext)}; rename it to .pdf or read the .pdf file");
        if (head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 0x03 && head[3] == 0x04)
        {
            var office = SniffZipOffice(full);
            if (office is not null && (ext == "" || ext == office))
                return (office, null);
            if (office is not null)
                return (null, $"it is an {Ext(office)}, not a {Ext(ext)}; rename it or read the {Ext(office)} file");
            return ext == "pdf" ? (null, "it is a ZIP archive, not a PDF (a .pdf extension does not make it one)") : (null, null);
        }
        return ext switch
        {
            "pdf" => (null, "it does not start with the %PDF header, so it is not a PDF"),
            "docx" or "xlsx" or "pptx" => (null, $"it is not a ZIP archive, so it is not a valid {Ext(ext)}"),
            _ => (null, null),
        };
    }

    private static string Ext(string f) => "." + f;

    /// <summary>Which Office file a ZIP is, by its parts (word/, xl/, ppt/); null when it is a ZIP of something else.</summary>
    private static string? SniffZipOffice(string full)
    {
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var names = zip.Entries.Select(e => e.Name.ToLowerInvariant()).Take(200).ToList();
            var dirs = new HashSet<string>(zip.Entries.Where(e => e.FullName.Contains('/')).Select(e => e.FullName[..(e.FullName.IndexOf('/') + 1)].ToLowerInvariant()));
            if (dirs.Contains("word/") || names.Contains("document.xml")) return "docx";
            if (dirs.Contains("xl/") || names.Contains("workbook.xml")) return "xlsx";
            if (dirs.Contains("ppt/") || names.Contains("presentation.xml")) return "pptx";
            return null;
        }
        catch
        {
            return null; // a truncated ZIP: the extractor's own answer ("corrupt") is the right one
        }
    }

    private static async Task<byte[]> ReadHeadAsync(string full, CancellationToken ct)
    {
        var head = new byte[4096];
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var n = await fs.ReadAsync(head.AsMemory(0, head.Length), ct).ConfigureAwait(false);
        return head.AsSpan(0, n).ToArray();
    }
}
