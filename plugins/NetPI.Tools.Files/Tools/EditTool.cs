using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Files;

public sealed class EditTool(ISettings? settings = null) : FileToolBase(settings)
{
    private static readonly string[] OldNames = ["oldText", "old_text", "old_string", "oldString", "old", "search", "find", "from", "original"];
    private static readonly string[] NewNames = ["newText", "new_text", "new_string", "newString", "new", "replace", "replacement", "to", "updated"];
    private static readonly string[] AllNames = ["replaceAll", "replace_all", "all", "global"];
    /// <summary>The several-files form: <c>files: [{ path, edits }]</c> (the shared names, so the guards read the same form).</summary>
    internal static readonly string[] FilesNames = ToolPathArgs.EditFilesNames;

    public const int ModelDiffLines = 80;
    public const int ModelDiffChars = 6000;
    /// <summary>The most files one call may change.</summary>
    public const int MaxFiles = 50;

    public sealed record EditSpec(string OldText, string NewText, bool ReplaceAll);

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "edit",
        Label = "Edit",
        Category = "files",
        ReadOnly = false,
        SummaryArg = "path",
        Description = "Edit files by exact text replacement: each oldText must match exactly one place (or use replaceAll). " +
                      "One file (path + edits), or several at once (files).",
        Help =
            "Several edits are applied in order and atomically: if any edit fails, nothing is written. Include enough surrounding " +
            "lines in oldText to be unique; an empty newText deletes. oldText/newText/replaceAll at the top level are a shorthand " +
            "for one edit. For a change across several files (a rename, an API and its callers, code and its docs) pass " +
            "files: [{path, edits}] instead of path: every file is matched first and nothing is written unless all of them match. " +
            "Line endings (CRLF/LF) do not matter: matching ignores them and each file keeps its original style and BOM.",
        Parameters = ToolSchema.Object(
            ("path", ToolSchema.Str("The file (or use files for several)"), false),
            ("edits", ToolSchema.Array("Applied in order", EditSchema()), false),
            ("oldText", ToolSchema.Str("One edit"), false),
            ("newText", ToolSchema.Str(""), false),
            ("replaceAll", ToolSchema.Bool(""), false),
            ("files", ToolSchema.Array("Several files in one call, all or nothing", ToolSchema.Object(
                ("path", ToolSchema.Str(""), true),
                ("edits", ToolSchema.Array("Applied in order", EditSchema()), true))), false)),
        PromptGuidelines = [UseFileTools, ChangeFiles],
    };

    private static JsonObject EditSchema() => ToolSchema.Object(
        ("oldText", ToolSchema.Str(""), true),
        ("newText", ToolSchema.Str(""), true),
        ("replaceAll", ToolSchema.Bool(""), false));

    /// <summary>Parse the edits array or the single-edit shorthand. Returns an error message on invalid input.</summary>
    public static (List<EditSpec> Edits, string? Error) ParseEdits(ToolArgs args)
    {
        var list = new List<EditSpec>();
        var items = args.List("edits", "changes", "replacements", "edit");
        if (items is not null && items.Count > 0 && items.Any(i => i.ValueKind == JsonValueKind.Object))
        {
            var n = 0;
            foreach (var item in items)
            {
                n++;
                if (item.ValueKind != JsonValueKind.Object) return ([], $"edits[{n - 1}] must be an object with oldText and newText.");
                var ea = new ToolArgs(item);
                var o = ea.Str(OldNames);
                var nw = ea.Str(NewNames);
                if (o is null) return ([], $"Edit {n}: missing oldText.");
                if (nw is null) return ([], $"Edit {n}: missing newText (use an empty string to delete).");
                list.Add(new EditSpec(o, nw, ea.Bool(AllNames) ?? false));
            }
            return (list, null);
        }
        var old = args.Str(OldNames);
        var @new = args.Str(NewNames);
        if (old is null && @new is null)
            return ([], "Missing edits. Pass {\"path\": …, \"edits\": [{\"oldText\": …, \"newText\": …}]} or the shorthand {\"path\": …, \"oldText\": …, \"newText\": …}.");
        if (old is null) return ([], "Missing oldText.");
        if (@new is null) return ([], "Missing newText (use an empty string to delete).");
        list.Add(new EditSpec(old, @new, args.Bool(AllNames) ?? false));
        return (list, null);
    }

    /// <summary>
    /// The paths an edit call changes, in the order the tool takes them: <c>path</c>, or each <c>files[].path</c>. The
    /// rule is the shared one (<see cref="ToolPathArgs.WriteTargets"/>): the hooks that judge a write by its path
    /// (guardrails, the workspace guard) read exactly what the tool reads.
    /// </summary>
    public static List<string> TargetPaths(ToolArgs args) => ToolPathArgs.WriteTargets("edit", args);

    /// <summary>One file's edits, matched and applied in memory, ready to be written.</summary>
    private sealed record Prepared(string Full, string Rel, byte[] Original, TextDocument Doc, string Text, EolStyle Eol,
        JsonArray Fuzzy, List<string> Notes, int Edits)
    {
        public bool Changed => Text != Doc.Text;
    }

    /// <summary>A file that cannot be edited as asked: the message for the model and the details for the UI.</summary>
    private sealed record Refusal(string Message, object? Details = null);

    protected override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        if (args.List(FilesNames) is { Count: > 0 } files)
        {
            if (args.Has(ToolPathArgs.PathNames))
                return ToolResult.Error("Pass either path (one file) or files (several), not both: put this file into files as well.");
            return await RunManyAsync(ctx, files, ct).ConfigureAwait(false);
        }

        var path = args.Str(ToolPathArgs.PathNames);
        if (string.IsNullOrWhiteSpace(path)) return MissingArg("path", "{\"path\": \"src/app.ts\", \"oldText\": \"…\", \"newText\": \"…\"}");
        var (edits, parseError) = ParseEdits(args);
        if (parseError is not null) return ToolResult.Error(parseError);

        var (p, refusal) = await PrepareAsync(ctx, path, edits, 0, 0, ct).ConfigureAwait(false);
        if (refusal is not null) return ToolResult.Error(refusal.Message, refusal.Details);
        if (!p!.Changed)
            return ToolResult.Ok($"No changes: the edits leave {p.Rel} unchanged.", new { path = p.Full, diff = "", added = 0, removed = 0, edits = edits.Count });

        TextCodec.WriteAtomic(p.Full, Encode(p));
        var (line, diff) = Summarize(p);
        var sb = new StringBuilder(line).Append('\n').Append('\n');
        sb.Append(ModelDiff(diff, ModelDiffLines, ModelDiffChars));
        return ToolResult.Ok(sb.ToString(), FileDetails(p, diff));
    }

    /// <summary>
    /// Several files in one call, all or nothing: every file is read and every edit matched in memory first; only when
    /// all of them match is anything written, and a write that fails midway puts the files already written back.
    /// </summary>
    private async Task<ToolResult> RunManyAsync(ToolContext ctx, List<JsonElement> files, CancellationToken ct)
    {
        if (files.Count > MaxFiles)
            return ToolResult.Error($"{files.Count} files is more than one edit call takes ({MaxFiles}). Split it into several calls.");

        var entries = new List<(string Path, List<EditSpec> Edits)>();
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].ValueKind != JsonValueKind.Object)
                return ToolResult.Error($"files[{i}] must be an object with path and edits.");
            var fa = new ToolArgs(files[i]);
            var path = fa.Str(ToolPathArgs.PathNames);
            if (string.IsNullOrWhiteSpace(path)) return ToolResult.Error($"File {i + 1} of {files.Count}: missing path.");
            var (edits, parseError) = ParseEdits(fa);
            if (parseError is not null) return ToolResult.Error($"File {i + 1} of {files.Count} ({path}): {parseError}");
            entries.Add((path, edits));
        }

        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new Dictionary<string, int>(comparer);
        var prepared = new List<Prepared>();
        for (var i = 0; i < entries.Count; i++)
        {
            var full = ctx.ResolvePath(entries[i].Path);
            if (seen.TryGetValue(full, out var first))
                return ToolResult.Error($"{Rel(ctx, full)} is listed twice (files {first + 1} and {i + 1}): put all its edits in one entry. No files were changed.");
            seen[full] = i;
            var (p, refusal) = await PrepareAsync(ctx, entries[i].Path, entries[i].Edits, i + 1, entries.Count, ct).ConfigureAwait(false);
            if (refusal is not null) return ToolResult.Error(refusal.Message, refusal.Details);
            prepared.Add(p!);
        }

        var changed = prepared.Where(p => p.Changed).ToList();
        if (changed.Count == 0)
            return ToolResult.Ok($"No changes: the edits leave all {prepared.Count} files unchanged.",
                new { files = prepared.Select(p => new { path = p.Full, diff = "", added = 0, removed = 0, edits = p.Edits }).ToArray(), diff = "", added = 0, removed = 0, edits = prepared.Sum(p => p.Edits) });

        var written = new List<Prepared>();
        foreach (var p in changed)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                TextCodec.WriteAtomic(p.Full, Encode(p));
                written.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                var notRestored = new List<string>();
                foreach (var w in written)
                {
                    try { TextCodec.WriteAtomic(w.Full, w.Original); }
                    catch (Exception) { notRestored.Add(w.Rel); }
                }
                var what = ex is OperationCanceledException ? "was cancelled" : $"failed: {ex.Message}";
                var back = written.Count == 0 ? "No files were changed."
                    : notRestored.Count == 0 ? $"The {written.Count} file{(written.Count == 1 ? "" : "s")} already written were restored, so no files were changed."
                    : $"Restoring failed for {string.Join(", ", notRestored)}: check them; the other files are unchanged.";
                if (ex is OperationCanceledException && notRestored.Count == 0) throw;
                return ToolResult.Error($"Writing {p.Rel} {what}. {back}", new { path = p.Full, failedFile = changed.IndexOf(p) + 1, files = changed.Count });
            }
        }

        var totalEdits = prepared.Sum(p => p.Edits);
        var summaries = prepared.Select(p => p.Changed ? ((string Line, UnifiedDiff? Diff))Summarize(p) : ($"{p.Rel}: no changes", null)).ToList();
        var added = summaries.Sum(s => s.Diff?.Added ?? 0);
        var removed = summaries.Sum(s => s.Diff?.Removed ?? 0);
        var sb = new StringBuilder($"Applied {totalEdits} edit{(totalEdits == 1 ? "" : "s")} to {changed.Count} file{(changed.Count == 1 ? "" : "s")} (+{added} −{removed})");
        // Each file gets its share of the diff budget the one-file form has, with a floor so a short change stays whole.
        var lines = Math.Max(20, ModelDiffLines / changed.Count);
        var chars = Math.Max(1500, ModelDiffChars / changed.Count);
        foreach (var (line, diff) in summaries)
        {
            sb.Append('\n').Append('\n').Append(line);
            if (diff is not null) sb.Append('\n').Append(ModelDiff(diff, lines, chars));
        }

        var details = new JsonArray();
        // One unified diff over every changed file, each with its ---/+++ header, for a viewer that shows one diff.
        var combined = new List<string>();
        for (var i = 0; i < prepared.Count; i++)
        {
            if (summaries[i].Diff is { } d) combined.Add(d.Text.TrimEnd('\n'));
            details.Add(JsonSerializer.SerializeToNode(summaries[i].Diff is { } fd ? FileDetails(prepared[i], fd)
                : new { path = prepared[i].Full, diff = "", added = 0, removed = 0, edits = prepared[i].Edits }));
        }
        return ToolResult.Ok(sb.ToString(), new
        {
            files = details,
            diff = string.Join('\n', combined),
            added,
            removed,
            edits = totalEdits,
        });
    }

    /// <summary>
    /// Read one file and apply its edits in memory. <paramref name="fileIndex"/>/<paramref name="fileCount"/> are 0 for the
    /// one-file form, whose messages stay as they always were; in the several-files form every message names the file.
    /// </summary>
    private async Task<(Prepared? File, Refusal? Refusal)> PrepareAsync(ToolContext ctx, string path, List<EditSpec> edits,
        int fileIndex, int fileCount, CancellationToken ct)
    {
        var many = fileCount > 0;
        var label = many ? $"File {fileIndex} of {fileCount}: " : "";
        var nothing = many ? " No files were changed (all-or-nothing)." : "";
        Refusal Fail(string message, object? details = null) => new(label + message + nothing, details);

        var full = ctx.ResolvePath(path);
        if (await WorkspacePaths.MutationRefusalAsync(ctx, full, ct).ConfigureAwait(false) is { } refusal) return (null, Fail(refusal));
        if (Directory.Exists(full)) return (null, Fail($"{full} is a directory, not a file."));
        if (!File.Exists(full)) return (null, Fail(NotFoundText(full, "To create a new file use the write tool.")));
        var size = new FileInfo(full).Length;
        if (size > MaxEditableBytes)
            return (null, Fail($"{full} is too large to edit ({PathDisplay.FormatSize(size)}; max {PathDisplay.FormatSize(MaxEditableBytes)})."));

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (TextCodec.IsBinary(bytes))
            return (null, Fail($"{full} is a binary file; edit only works on text files."));
        var doc = TextCodec.Decode(bytes);
        var rel = Rel(ctx, full);

        var text = doc.Text;
        var fuzzy = new JsonArray();
        var notes = new List<string>();
        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var outcome = EditMatcher.Apply(text, e.OldText, e.NewText, e.ReplaceAll);
            if (!outcome.Success)
            {
                if (many)
                {
                    var which = edits.Count == 1 ? $"File {fileIndex} of {fileCount}: edit failed" : $"File {fileIndex} of {fileCount}, edit {i + 1} of {edits.Count} failed";
                    return (null, new Refusal($"{which} in {rel}: {outcome.Error} No files were changed (all-or-nothing).",
                        new { path = full, failedFile = fileIndex, files = fileCount, failedEdit = i + 1, edits = edits.Count }));
                }
                var one = edits.Count == 1 ? "Edit failed" : $"Edit {i + 1} of {edits.Count} failed";
                var tail = edits.Count == 1 ? "The file was not changed." : "No edits were applied (all-or-nothing); the file was not changed.";
                return (null, new Refusal($"{one} in {rel}: {outcome.Error} {tail}", new { path = full, failedEdit = i + 1, edits = edits.Count }));
            }
            if (outcome.Strategy != MatchStrategy.Exact)
            {
                var name = EditMatcher.StrategyName(outcome.Strategy);
                fuzzy.Add(new JsonObject { ["edit"] = i + 1, ["strategy"] = name, ["line"] = outcome.Lines.FirstOrDefault() });
                notes.Add(outcome.Strategy switch
                {
                    MatchStrategy.TrailingWhitespace => $"edit {i + 1} matched at line {outcome.Lines[0]} ignoring trailing whitespace",
                    MatchStrategy.Unicode => $"edit {i + 1} matched at line {outcome.Lines[0]} after normalizing quotes/dashes/spaces",
                    _ => $"edit {i + 1} matched at line {outcome.Lines[0]} ignoring indentation (newText was re-indented)",
                });
            }
            if (e.ReplaceAll && outcome.Replacements > 1) notes.Add($"edit {i + 1} replaced {outcome.Replacements} occurrences");
            text = outcome.Text;
        }

        // A non-UTF-8 file decoded as Latin-1 round-trips byte-for-byte only while its text stays within U+00FF.
        // An edit that introduces a character beyond it cannot be written back in the file's encoding, and
        // TextCodec.Encode would fall back to UTF-8 and silently re-encode every unchanged byte (é E9 becomes
        // C3 A9). A fragment edit must not be a whole-file conversion: that is what the write tool is for.
        if (text != doc.Text && doc.Legacy && text.Any(c => c > 0xFF))
        {
            var bad = text.First(c => c > 0xFF);
            return (null, Fail($"{rel} is not valid UTF-8 (it was decoded as Latin-1) and this edit introduces " +
                $"U+{((int)bad):X4} ({bad}), which the file's encoding cannot hold. Writing back would re-encode the " +
                "whole file to UTF-8 and change every unchanged byte, so nothing was written. If re-encoding the file " +
                "to UTF-8 is deliberate, use the write tool with the full new content."));
        }

        var eol = doc.DetectedEol ?? NewFileEol();
        return (new Prepared(full, rel, bytes, doc, text, eol, fuzzy, notes, edits.Count), null);
    }

    private static byte[] Encode(Prepared p) => TextCodec.Encode(p.Text, p.Eol, p.Doc.Bom, p.Doc.Encoding);

    /// <summary>The file's result line (<c>Applied 2 edits to a.cs (+3 −1)</c> with its notes) and its diff.</summary>
    private static (string Line, UnifiedDiff Diff) Summarize(Prepared p)
    {
        var diff = LineDiff.Unified(p.Doc.Text, p.Text, p.Rel, context: 3, maxLines: WriteTool.MaxDiffLines);
        var sb = new StringBuilder();
        sb.Append($"Applied {p.Edits} edit{(p.Edits == 1 ? "" : "s")} to {p.Rel} (+{diff.Added} −{diff.Removed})");
        if (p.Notes.Count > 0) sb.Append(" [").Append(string.Join("; ", p.Notes)).Append(']');
        if (p.Doc.Stats.Mixed) sb.Append($" [file had mixed line endings; normalized to {TextCodec.EolName(p.Eol).ToUpperInvariant()}]");
        return (sb.ToString(), diff);
    }

    /// <summary>The diff for the model: hunks only (they carry the line numbers), cut to the budget.</summary>
    private static string ModelDiff(UnifiedDiff diff, int maxLines, int maxChars)
    {
        var body = string.Join('\n', diff.Text.Split('\n').SkipWhile(l => l.StartsWith("--- ", StringComparison.Ordinal) || l.StartsWith("+++ ", StringComparison.Ordinal)));
        return LineDiff.Cap(body, maxLines, maxChars, out _).TrimEnd('\n');
    }

    private static object FileDetails(Prepared p, UnifiedDiff diff) => new
    {
        path = p.Full,
        diff = diff.Text,
        added = diff.Added,
        removed = diff.Removed,
        edits = p.Edits,
        firstChangedLine = diff.FirstChangedLine,
        fuzzy = p.Fuzzy.Count > 0 ? p.Fuzzy : null,
        eol = TextCodec.EolName(p.Eol),
        bom = p.Doc.Bom,
    };
}
