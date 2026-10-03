using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Files;

public sealed class EditTool(ISettings? settings = null) : FileToolBase(settings)
{
    private static readonly string[] OldNames = ["oldText", "old_text", "old_string", "oldString", "old", "search", "find", "from", "original"];
    private static readonly string[] NewNames = ["newText", "new_text", "new_string", "newString", "new", "replace", "replacement", "to", "updated"];
    private static readonly string[] AllNames = ["replaceAll", "replace_all", "all", "global"];

    public const int ModelDiffLines = 80;
    public const int ModelDiffChars = 6000;

    public sealed record EditSpec(string OldText, string NewText, bool ReplaceAll);

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "edit",
        Label = "Edit",
        Category = "files",
        ReadOnly = false,
        SummaryArg = "path",
        Description = "Edit a file by exact text replacement: each oldText must match exactly one place (or use replaceAll).",
        Help =
            "Several edits are applied in order and atomically: if any edit fails, nothing is written. Include enough surrounding " +
            "lines in oldText to be unique; an empty newText deletes. oldText/newText/replaceAll at the top level are a shorthand " +
            "for one edit. Line endings (CRLF/LF) do not matter: matching ignores them and the file keeps its original style and BOM.",
        Parameters = ToolSchema.Object(
            ("path", ToolSchema.Str(""), true),
            ("edits", ToolSchema.Array("Applied in order", ToolSchema.Object(
                ("oldText", ToolSchema.Str(""), true),
                ("newText", ToolSchema.Str(""), true),
                ("replaceAll", ToolSchema.Bool(""), false))), false),
            ("oldText", ToolSchema.Str("One edit"), false),
            ("newText", ToolSchema.Str(""), false),
            ("replaceAll", ToolSchema.Bool(""), false)),
        PromptGuidelines = [UseFileTools, ChangeFiles],
    };

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

    protected override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var path = args.Str(PathNames);
        if (string.IsNullOrWhiteSpace(path)) return MissingArg("path", "{\"path\": \"src/app.ts\", \"oldText\": \"…\", \"newText\": \"…\"}");
        var (edits, parseError) = ParseEdits(args);
        if (parseError is not null) return ToolResult.Error(parseError);

        var full = ctx.ResolvePath(path);
        if (WorkspaceRefusal(ctx, full) is { } refusal) return ToolResult.Error(refusal);
        if (Directory.Exists(full)) return ToolResult.Error($"{full} is a directory, not a file.");
        if (!File.Exists(full)) return NotFound(ctx, full, "To create a new file use the write tool.");
        var size = new FileInfo(full).Length;
        if (size > MaxEditableBytes)
            return ToolResult.Error($"{full} is too large to edit ({PathDisplay.FormatSize(size)}; max {PathDisplay.FormatSize(MaxEditableBytes)}).");

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (TextCodec.IsBinary(bytes))
            return ToolResult.Error($"{full} is a binary file; edit only works on text files.");
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
                var which = edits.Count == 1 ? "Edit failed" : $"Edit {i + 1} of {edits.Count} failed";
                var tail = edits.Count == 1 ? "The file was not changed." : "No edits were applied (all-or-nothing); the file was not changed.";
                return ToolResult.Error($"{which} in {rel}: {outcome.Error} {tail}",
                    new { path = full, failedEdit = i + 1, edits = edits.Count });
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

        if (text == doc.Text)
            return ToolResult.Ok($"No changes: the edits leave {rel} unchanged.", new { path = full, diff = "", added = 0, removed = 0, edits = edits.Count });

        var eol = doc.DetectedEol ?? NewFileEol();
        // A non-UTF-8 file decoded as Latin-1 round-trips byte-for-byte only while its text stays within U+00FF.
        // An edit that introduces a character beyond it cannot be written back in the file's encoding, and
        // TextCodec.Encode would fall back to UTF-8 and silently re-encode every unchanged byte (é E9 becomes
        // C3 A9). A fragment edit must not be a whole-file conversion: that is what the write tool is for.
        if (doc.Legacy && text.Any(c => c > 0xFF))
        {
            var bad = text.First(c => c > 0xFF);
            return ToolResult.Error($"{rel} is not valid UTF-8 (it was decoded as Latin-1) and this edit introduces " +
                $"U+{((int)bad):X4} ({bad}), which the file's encoding cannot hold. Writing back would re-encode the " +
                "whole file to UTF-8 and change every unchanged byte, so nothing was written. If re-encoding the file " +
                "to UTF-8 is deliberate, use the write tool with the full new content.");
        }
        TextCodec.WriteAtomic(full, TextCodec.Encode(text, eol, doc.Bom, doc.Encoding));

        var diff = LineDiff.Unified(doc.Text, text, rel, context: 3, maxLines: WriteTool.MaxDiffLines);
        var sb = new StringBuilder();
        sb.Append($"Applied {edits.Count} edit{(edits.Count == 1 ? "" : "s")} to {rel} (+{diff.Added} −{diff.Removed})");
        if (notes.Count > 0) sb.Append(" [").Append(string.Join("; ", notes)).Append(']');
        if (doc.Stats.Mixed) sb.Append($" [file had mixed line endings; normalized to {TextCodec.EolName(eol).ToUpperInvariant()}]");
        sb.Append('\n').Append('\n');
        // Skip the ---/+++ header for the model; hunks carry the line numbers.
        var body = string.Join('\n', diff.Text.Split('\n').SkipWhile(l => l.StartsWith("--- ", StringComparison.Ordinal) || l.StartsWith("+++ ", StringComparison.Ordinal)));
        sb.Append(LineDiff.Cap(body, ModelDiffLines, ModelDiffChars, out _).TrimEnd('\n'));

        return ToolResult.Ok(sb.ToString(), new
        {
            path = full,
            diff = diff.Text,
            added = diff.Added,
            removed = diff.Removed,
            edits = edits.Count,
            firstChangedLine = diff.FirstChangedLine,
            fuzzy = fuzzy.Count > 0 ? fuzzy : null,
            eol = TextCodec.EolName(eol),
            bom = doc.Bom,
        });
    }
}
