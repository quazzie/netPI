namespace NetPI.Tools.Files;

public sealed class WriteTool(ISettings? settings = null) : FileToolBase(settings)
{
    internal static readonly string[] ContentNames = ["content", "contents", "text", "file_text", "fileText", "data", "body", "code"];

    public const int MaxDiffLines = 2000;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "write",
        Label = "Write",
        Category = "files",
        ReadOnly = false,
        SummaryArg = "path",
        Description = "Create a file or overwrite it completely.",
        Help = "Parent directories are created. An existing file keeps its line-ending style (CRLF, LF or CR) and BOM; new files use the configured default.",
        Parameters = ToolSchema.Object(
            ("path", ToolSchema.Str(""), true),
            ("content", ToolSchema.Str(""), true)),
        PromptGuidelines = [UseFileTools, ChangeFiles],
    };

    protected override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var path = args.Str(ToolPathArgs.PathNames);
        if (string.IsNullOrWhiteSpace(path)) return MissingArg("path", "{\"path\": \"notes.txt\", \"content\": \"…\"}");
        var content = args.Str(ContentNames);
        if (content is null) return MissingArg("content", "{\"path\": \"notes.txt\", \"content\": \"…\"}");
        var full = ctx.ResolvePath(path);
        if (await WorkspacePaths.MutationRefusalAsync(ctx, full, ct).ConfigureAwait(false) is { } refusal) return ToolResult.Error(refusal);
        if (Directory.Exists(full)) return ToolResult.Error($"{full} is a directory; give a file path.");

        var existed = File.Exists(full);
        TextDocument? old = null;
        byte[]? oldBytes = null;
        EolStyle? keptEol = null;
        bool keptBom = false;
        bool wasBinary = false;
        if (existed)
        {
            var len = new FileInfo(full).Length;
            if (len <= MaxEditableBytes)
            {
                oldBytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                if (TextCodec.IsBinary(oldBytes)) wasBinary = true;
                else old = TextCodec.Decode(oldBytes);
            }
            else
            {
                // too large to diff: the start of the file says what style it is in, so the write does not change it
                (keptEol, keptBom) = TextCodec.DetectEolSample(full);
            }
        }

        var text = TextCodec.NormalizeToLf(content);
        var eol = old?.DetectedEol ?? keptEol ?? NewFileEol();
        var bom = old?.Bom ?? keptBom;
        var data = TextCodec.Encode(text, eol, bom, old?.Encoding);
        var rel = Rel(ctx, full);
        var lines = TextCodec.CountLines(text);

        if (oldBytes is not null && oldBytes.AsSpan().SequenceEqual(data))
        {
            return ToolResult.Ok($"No changes: {rel} already has exactly this content.",
                new { path = full, created = false, bytes = data.Length, lines, added = 0, removed = 0, eol = TextCodec.EolName(eol), bom });
        }

        TextCodec.WriteAtomic(full, data);

        if (!existed)
        {
            return ToolResult.Ok($"Created {rel} ({lines} line{(lines == 1 ? "" : "s")}, {PathDisplay.FormatSize(data.Length)}).",
                new { path = full, created = true, bytes = data.Length, lines, eol = TextCodec.EolName(eol), bom });
        }

        UnifiedDiff? diff = old is null ? null : LineDiff.Unified(old.Text, text, rel, context: 3, maxLines: MaxDiffLines);
        var summary = $"Wrote {rel} ({lines} line{(lines == 1 ? "" : "s")}, {PathDisplay.FormatSize(data.Length)}";
        if (diff is not null) summary += $"; +{diff.Added} −{diff.Removed}";
        summary += $"; {TextCodec.EolName(eol).ToUpperInvariant()} line endings{(bom ? ", BOM" : "")} kept).";
        if (old is null)
            summary = wasBinary
                ? $"Overwrote {rel} (previous content was binary) with {lines} line{(lines == 1 ? "" : "s")}; the new text uses the default {TextCodec.EolName(eol).ToUpperInvariant()} line endings."
                : $"Overwrote {rel} (previous content was too large to diff) with {lines} line{(lines == 1 ? "" : "s")}; its {TextCodec.EolName(eol).ToUpperInvariant()} line endings{(bom ? " and BOM" : "")} were kept.";
        return ToolResult.Ok(summary, new
        {
            path = full,
            created = false,
            bytes = data.Length,
            lines,
            diff = diff?.Text,
            added = diff?.Added,
            removed = diff?.Removed,
            eol = TextCodec.EolName(eol),
            bom,
        });
    }
}
