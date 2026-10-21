using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Files;

/// <summary>One text edit by position: 0-based line and UTF-16 column, as the Language Server Protocol counts them.</summary>
internal sealed record PositionEdit(int StartLine, int StartCol, int EndLine, int EndCol, string NewText);

/// <summary>The edits of one file.</summary>
internal sealed record FilePositionEdits(string Path, IReadOnlyList<PositionEdit> Edits);

/// <summary>
/// <c>files.applyEdits</c>: edits by position (a language server's rename, say) applied through the same text handling
/// as the edit tool: each file keeps its encoding, BOM and line endings. Every file is read, checked against the workspace
/// rule and edited in memory first; only when all of them are fine are any written, and a failed write puts the files
/// already written back.
/// </summary>
internal static class EditApplier
{
    public const int MaxFiles = 200;

    private sealed record Prepared(string Path, byte[] Original, byte[] Result, int Edits);

    /// <summary>Parse the RPC's <c>edits</c> array.</summary>
    public static List<FilePositionEdits> Parse(JsonElement edits)
    {
        if (edits.ValueKind != JsonValueKind.Array) throw new RpcException("bad_request", "edits must be an array of { path, edits: [{ startLine, startCol, endLine, endCol, newText }] }.");
        var files = new List<FilePositionEdits>();
        foreach (var file in edits.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                || !file.TryGetProperty("edits", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new RpcException("bad_request", $"edits[{files.Count}] needs path and edits.");
            var one = new List<PositionEdit>();
            foreach (var e in list.EnumerateArray())
            {
                int N(string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0
                    ? n : throw new RpcException("bad_request", $"{p.GetString()}: every edit needs {name} (a 0-based integer).");
                var text = e.TryGetProperty("newText", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw new RpcException("bad_request", $"{p.GetString()}: every edit needs newText.");
                one.Add(new PositionEdit(N("startLine"), N("startCol"), N("endLine"), N("endCol"), text));
            }
            files.Add(new FilePositionEdits(p.GetString()!, one));
        }
        return files;
    }

    /// <param name="root">What relative paths resolve against.</param>
    /// <param name="refusal">The workspace rule for a mutation of a full path: null to allow, else the refusal text.</param>
    /// <returns>Per file: its full path and the number of edits applied.</returns>
    public static async Task<List<(string Path, int Edits)>> ApplyAsync(string root, IReadOnlyList<FilePositionEdits> files,
        Func<string, CancellationToken, Task<string?>> refusal, CancellationToken ct)
    {
        if (files.Count == 0) throw new RpcException("bad_request", "No edits given.");
        if (files.Count > MaxFiles) throw new RpcException("bad_request", $"At most {MaxFiles} files per call.");
        var prepared = new List<Prepared>();
        var seen = new HashSet<string>(WorkspacePaths.Comparer);
        foreach (var file in files)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(file.Path) ? file.Path : Path.Combine(root, file.Path));
            if (!seen.Add(full)) throw new RpcException("bad_request", $"{full} is listed twice. Nothing was changed.");
            if (await refusal(full, ct).ConfigureAwait(false) is { } refused) throw new RpcException("refused", refused + " Nothing was changed.");
            if (!File.Exists(full)) throw new RpcException("not_found", $"File not found: {full}. Nothing was changed.");
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (TextCodec.IsBinary(bytes)) throw new RpcException("bad_request", $"{full} is a binary file. Nothing was changed.");
            var doc = TextCodec.Decode(bytes);
            string text;
            try { text = Apply(doc.Text, file.Edits); }
            catch (ArgumentException ex) { throw new RpcException("bad_request", $"{full}: {ex.Message} Nothing was changed."); }
            if (doc.Legacy && text != doc.Text && text.Any(c => c > 0xFF))
                throw new RpcException("bad_request", $"{full} is not valid UTF-8 and these edits add a character its encoding cannot hold. Nothing was changed.");
            var result = text == doc.Text ? bytes : TextCodec.Encode(text, doc.DetectedEol ?? EolStyle.Lf, doc.Bom, doc.Encoding);
            prepared.Add(new Prepared(full, bytes, result, file.Edits.Count));
        }

        var written = new List<Prepared>();
        foreach (var p in prepared.Where(p => !p.Result.AsSpan().SequenceEqual(p.Original)))
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!File.ReadAllBytes(p.Path).AsSpan().SequenceEqual(p.Original))
                    throw new IOException($"{p.Path} changed since it was read (another writer).");
                TextCodec.WriteAtomic(p.Path, p.Result);
                written.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                var notRestored = new List<string>();
                foreach (var w in written)
                {
                    try { TextCodec.WriteAtomic(w.Path, w.Original); }
                    catch (Exception) { notRestored.Add(w.Path); }
                }
                throw new RpcException("io", notRestored.Count == 0
                    ? $"Writing {p.Path} failed: {ex.Message} The files already written were restored; nothing was changed."
                    : $"Writing {p.Path} failed: {ex.Message} Restoring failed for {string.Join(", ", notRestored)}: check them.");
            }
        }
        return [.. prepared.Select(p => (p.Path, p.Edits))];
    }

    /// <summary>Apply position edits to LF-normalized text. Positions past a line's end or the text's end are clamped (LSP's rule); overlapping edits are refused.</summary>
    internal static string Apply(string text, IReadOnlyList<PositionEdit> edits)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        int Offset(int line, int col)
        {
            if (line >= starts.Count) return text.Length;
            var lineEnd = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length; // before the \n
            return Math.Min(starts[line] + col, lineEnd);
        }
        var spans = edits.Select((e, i) => (Start: Offset(e.StartLine, e.StartCol), End: Offset(e.EndLine, e.EndCol), e.NewText, Index: i)).ToList();
        foreach (var s in spans)
            if (s.End < s.Start) throw new ArgumentException($"edit {s.Index + 1} ends before it starts.");
        var ordered = spans.OrderBy(s => s.Start).ThenBy(s => s.End).ThenBy(s => s.Index).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Start < ordered[i - 1].End) throw new ArgumentException($"edits {ordered[i - 1].Index + 1} and {ordered[i].Index + 1} overlap.");
        var sb = new StringBuilder(text);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var s = ordered[i];
            sb.Remove(s.Start, s.End - s.Start).Insert(s.Start, TextCodec.NormalizeToLf(s.NewText));
        }
        return sb.ToString();
    }
}
