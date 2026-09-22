using System.Text;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Tools;

/// <summary>
/// Shared text-editing engine behind the <c>edit</c> and <c>replace</c> tools
/// (docs/plans/file-tool-reliability.md §2/§3C).
///
/// Guarantees:
/// - NEWLINE-TOLERANT matching: CRLF and LF compare equivalently (the comparison
///   view is normalized to LF; the file's actual bytes are never normalized
///   outside the replaced spans). Lone CR is NOT normalized — it stays literal.
/// - Matching/counting happens ONLY in the normalized view (never a raw
///   first-match fallback): a raw match that differs from another occurrence
///   only by line endings cannot hide it.
/// - Replacement spans are spliced back out of the ORIGINAL text, with the
///   matched original bytes replaced by the requested newText (whose own
///   CRLF/LF sequences are aligned to the file's line-ending style).
/// - Encoding/BOM preservation: strict UTF-8 (with or without BOM) and
///   BOM-marked UTF-16/UTF-32 are recognized (longer BOMs first); undecodable
///   or unsupported data is rejected instead of substituting characters.
///   Unchanged prefix/suffix BYTES and the BOM survive a write.
/// - Atomic, safe write: a sibling temporary file plus a same-volume replace;
///   the original is never touched on validation or pre-commit failure and the
///   temp is cleaned up on every failure path. A no-op (result identical to the
///   original) does not rewrite the file at all.
/// </summary>
public static class TextFileEditor
{
    /// <summary>
    /// Replace <paramref name="oldText"/> (exactly one literal occurrence,
    /// CRLF/LF-equivalent) with <paramref name="newText"/> in the file at
    /// <paramref name="path"/>. Returns a success message or a concise error
    /// (with recovery hint); the file is left untouched on every failure.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EditAsync(string path, string oldText, string newText, CancellationToken ct)
    {
        var (text, ok, err) = await LoadAsync(path, ct);
        if (!ok) return (false, err);

        var (spans, count) = FindAll(text, oldText);
        if (count == 0)
            return (false, $"0 matches for oldText in {path}; file unchanged (re-read the file or fix line endings/whitespace in oldText).");
        if (count > 1)
            return (false, $"Ambiguous: oldText matches {count} times in {path}; nothing written (add surrounding context so it is unique, or use replace with expectedCount={count}).");

        var noOp = Apply(text, newText, [spans[0]], out var updated);
        if (noOp) return (true, "no change needed (the span already contains newText).");
        var w = await WriteAsync(path, updated, ct);
        return (w.Ok, w.Ok ? $"Edited {path}." : $"write failed: {w.Message}");
    }


    /// <summary>
    /// Replace ALL non-overlapping literal occurrences of <paramref name="oldText"/>
    /// (CRLF/LF-equivalent) with <paramref name="newText"/>. Nothing is written
    /// unless the actual count equals <paramref name="expectedCount"/> exactly.
    /// </summary>
    public static async Task<(bool Ok, string Message, int Count)> ReplaceAsync(string path, string oldText, string newText, int expectedCount, CancellationToken ct)
    {
        var (text, ok, err) = await LoadAsync(path, ct);
        if (!ok) return (false, err, 0);

        var (spans, count) = FindAll(text, oldText);
        if (count != expectedCount)
            return (false, $"expected {expectedCount} occurrence(s) of oldText in {path} but found {count}; nothing written (re-read the file to check the count and text).", count);

        var noOp = Apply(text, newText, spans.ToArray(), out var updated);
        if (noOp) return (true, "no change needed (all spans already contain newText).", count);
        var w = await WriteAsync(path, updated, ct);
        return (w.Ok, w.Ok ? $"Replaced {count} occurrence(s) in {path}." : $"write failed: {w.Message}", count);
    }



    // ---- loading / encoding ----------------------------------------------

    /// <summary>
    /// Read + decode the file. Returns the text plus an error message on
    /// failure (missing file, undecodable, unsupported encoding, binary).
    /// </summary>
    internal static async Task<(string Text, bool Ok, string Error)> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return ("", false, $"File not found: {path}");
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, ct); }
        catch (Exception ex) { return ("", false, $"Read failed: {ex.Message}"); }

        var dec = Decode(bytes);
        return dec is { } d ? (d, true, "") : ("", false, RefusalMessage(path, bytes));
    }

    /// <summary>
    /// Decode supported encodings: UTF-8 (BOM optional), BOM-marked UTF-16
    /// (LE/BE) and UTF-32 (LE/BE). Longer BOMs are checked first. Null for
    /// undecodable or unsupported data — no silent substitution.
    /// </summary>
    internal static string? Decode(byte[] bytes)
    {
        if (StartsWith(bytes, Utf32LeBom))
        {
            var body = bytes[Utf32LeBom.Length..];
            return body.Length % 4 == 0 ? U32ToChars(body, littleEndian: true) : null;
        }
        if (StartsWith(bytes, Utf32BeBom))
        {
            var body = bytes[Utf32BeBom.Length..];
            return body.Length % 4 == 0 ? U32ToChars(body, littleEndian: false) : null;
        }
        if (StartsWith(bytes, Utf16LeBom))
        {
            var body = bytes[Utf16LeBom.Length..];
            return body.Length % 2 == 0 ? new string(Encoding.Unicode.GetChars(body)) : null;
        }
        if (StartsWith(bytes, Utf16BeBom))
        {
            var body = bytes[Utf16BeBom.Length..];
            return body.Length % 2 == 0 ? new string(Encoding.BigEndianUnicode.GetChars(body)) : null;
        }

        // Strict UTF-8, BOM optional: emit no BOM, THROW on invalid byte
        // sequences (throwOnInvalidBytes) — no silent substitution. The UTF-8
        // BOM is stripped above so it is never part of the decoded text.
        var utf8 = bytes;
        if (StartsWith(bytes, Utf8Bom)) utf8 = bytes[Utf8Bom.Length..];
        try { return new string(Utf8Strict.GetChars(utf8)); }
        catch (Exception) { return null; }
    }

    internal static string RefusalMessage(string path, byte[] bytes)
    {
        if (IsBinary(bytes))
            return $"Refusing to edit binary file {path} ({bytes.Length} bytes). Convert it to text first.";
        return $"Cannot decode {path} as text (supported: UTF-8 with/without BOM, BOM-marked UTF-16/UTF-32); file unchanged.";
    }

    internal static bool IsBinary(byte[] bytes, int sample = 4096)
    {
        var n = Math.Min(bytes.Length, sample);
        if (n == 0) return false;
        var suspicious = 0;
        for (var i = 0; i < n; i++)
        {
            var b = bytes[i];
            if (b == 0) return true;
            if (b < 32 && b is not (10 or 13 or 9 or 11)) suspicious++;
        }
        return suspicious > n / 16;
    }

    // ---- newline-tolerant matching ------------------------------------------

    /// <summary>
    /// Normalize the whole file to LF-only in a COMPARISON view and build the
    /// map normalizedIndex → original offset. CRLF and LF are equivalent; a
    /// lone CR stays literal (it occupies one normalized slot and does NOT
    /// combine with the following LF — per plan §2, lone CR is untouched).
    /// </summary>
    internal static (string Norm, int[] Map) BuildComparisonView(string original)
    {
        var norm = new StringBuilder(original.Length, original.Length + 1);
        var map = new List<int>(original.Length + 1) { 0 };
        // map[k] = original offset of the START of normalized char k (map[0] = 0).
        // norm[k] is produced from the original char(s) at i; the entry recorded
        // AFTER appending norm[k] must point past those original char(s), so the
        // next norm char maps to the original offset right after this one.
        for (var i = 0; i < original.Length; i++)
        {
            var c = original[i];
            if (c == '\r' && i + 1 < original.Length && original[i + 1] == '\n')
            {
                norm.Append('\n');
                map.Add(i + 2); // the CRLF pair spans TWO original chars
                i++; // consume the '\n'
            }
            else
            {
                norm.Append(c);
                map.Add(i + 1); // one original char consumed
            }
        }
        map.Add(original.Length); // sentinel: normalized end → original end
        return (norm.ToString(), map.ToArray());
    }

    /// <summary>
    /// Find ALL non-overlapping literal occurrences of <paramref name="needle"/>
    /// in the file, comparing in the normalized view ONLY (never raw). Returns
    /// the spans (as ORIGINAL-text index ranges, left-to-right) and the count.
    /// </summary>
    internal static (List<(int Start, int End)> Spans, int Count) FindAll(string original, string needle)
    {
        var spans = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(needle)) return (spans, 0);

        var (norm, map) = BuildComparisonView(original);
        var (nNorm, _) = BuildComparisonView(needle);
        if (nNorm.Length == 0 || nNorm.Length > norm.Length) return (spans, 0);

        var searchFrom = 0;
        while (true)
        {
            var at = norm.IndexOf(nNorm, searchFrom, StringComparison.Ordinal);
            if (at < 0) break;
            spans.Add((map[at], map[at + nNorm.Length]));
            searchFrom = at + nNorm.Length;
        }
        return (spans, spans.Count);
    }

    /// <summary>
    /// Splice the matched spans out of the ORIGINAL text, replacing each with
    /// <paramref name="newText"/> whose CRLF/LF line breaks are aligned to:
    /// 1) the first newline sequence inside the matched original span (if any),
    /// 2) otherwise the file's most frequent CRLF/LF sequence (first-seen on
    ///    ties), 3) otherwise LF. Returns true when the result is identical to
    ///    the original (no-op: callers must not rewrite the file).
    /// </summary>
    internal static bool Apply(string original, string newText, (int Start, int End)[] spans, out string result)
    {
        var fileStyle = DominantLineEnding(original);
        var sb = new StringBuilder(original.Length + newText.Length * spans.Length);
        var cursor = 0;
        foreach (var (start, end) in spans)
        {
            if (start < cursor) continue; // defensive: spans are ordered, non-overlapping
            sb.Append(original, cursor, start - cursor);

            var style = FirstLineEnding(original[start..end]) ?? fileStyle ?? "\n";
            sb.Append(AlignLineEndings(newText, style));
            cursor = end;
        }
        sb.Append(original, cursor, original.Length - cursor);

        result = sb.ToString();
        return result == original;
    }

    /// <summary>The first CRLF/LF sequence inside <paramref name="s"/>, or null.</summary>
    internal static string? FirstLineEnding(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n') return "\n";
            if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') return "\r\n";
        }
        return null;
    }

    /// <summary>
    /// The file's most frequent CRLF/LF sequence; ties go to the FIRST sequence
    /// encountered. Null when the file has no newlines.
    /// </summary>
    internal static string? DominantLineEnding(string s)
    {
        var crlf = 0;
        var lf = 0;
        string? first = null;
        for (var i = 0; i < s.Length; i++)
        {
            string? seq = null;
            if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { seq = "\r\n"; i++; }
            else if (s[i] == '\n') seq = "\n";
            if (seq is null) continue;
            if (first is null) first = seq;
            if (seq == "\r\n") crlf++; else lf++;
        }
        if (crlf == 0 && lf == 0) return null;
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Convert every CRLF and LF in <paramref name="text"/> to <paramref name="style"/>.
    /// A lone CR is left literal (not a line ending in this version).
    /// </summary>
    internal static string AlignLineEndings(string text, string style)
    {
        if (style == "\n" && text.IndexOf('\r') < 0) return text;
        return text.Replace("\r\n", style).Replace("\n", style);
    }

    // ---- atomic, preservation-aware write ------------------------------------

    /// <summary>
    /// Atomically write <paramref name="text"/> to <paramref name="path"/>:
    /// sibling temp file + same-volume replace. The original's encoding/BOM is
    /// preserved by re-encoding the new text into the SAME encoding; the
    /// unchanged prefix/suffix bytes and the BOM survive exactly. On any
    /// failure the original is intact and the temp is removed.
    /// </summary>
    internal static async Task<(bool Ok, string Message)> WriteAsync(string path, string text, CancellationToken ct)
    {
        byte[] original;
        try { original = await File.ReadAllBytesAsync(path, ct); }
        catch (Exception ex) { return (false, $"cannot re-read original: {ex.Message}"); }

        var encoded = Encode(original, text);
        if (encoded is null)
            return (false, "cannot re-encode the file without changing its encoding/BOM; original left untouched.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.netpi-tmp-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(temp, encoded, ct);
            File.Move(temp, path, overwrite: true); // same volume: atomic replace
            return (true, "");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return (false, $"{ex.Message}; original left untouched.");
        }
    }

    /// <summary>
    /// Re-encode <paramref name="text"/> in the same encoding/BOM as
    /// <paramref name="original"/> (so unchanged bytes stay identical).
    /// Returns null when the original encoding is not supported (no silent
    /// re-encoding to something else).
    /// </summary>
    internal static byte[]? Encode(byte[] original, string text)
    {
        if (StartsWith(original, Utf32LeBom)) return Utf32LeBom.Concat(U32FromChars(text, littleEndian: true)).ToArray();
        if (StartsWith(original, Utf32BeBom)) return Utf32BeBom.Concat(U32FromChars(text, littleEndian: false)).ToArray();
        if (StartsWith(original, Utf16LeBom)) return Utf16LeBom.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        if (StartsWith(original, Utf16BeBom)) return Utf16BeBom.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();

        // UTF-8: preserve (or drop) the BOM to match the original.
        var withBom = StartsWith(original, Utf8Bom);
        var body = Encoding.UTF8.GetBytes(text);
        return withBom ? Utf8Bom.Concat(body).ToArray() : body;
    }

    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    /// <summary>UTF-32 (LE/BE) → chars. Throws OverflowException for code points &gt; U+10FFFF.</summary>
    internal static string U32ToChars(byte[] b, bool littleEndian)
    {
        var chars = new char[b.Length / 4];
        for (var i = 0; i < chars.Length; i++)
        {
            var off = i * 4;
            var lo = (uint)b[off] | (uint)b[off + 1] << 8 | (uint)b[off + 2] << 16 | (uint)b[off + 3] << 24;
            var hi = (uint)b[off + 3] | (uint)b[off + 2] << 8 | (uint)b[off + 1] << 16 | (uint)b[off];
            var cp = littleEndian ? lo : hi;
            // surrogates / > U+10FFFF are not valid scalar values in UTF-32
            if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                throw new ArgumentException("invalid UTF-32 code point");
            if (cp < 0x10000) { chars[i] = (char)cp; }
            else { chars[i] = char.ConvertFromUtf32((int)cp)[0]; i++; chars[i] = char.ConvertFromUtf32((int)cp)[1]; }
        }
        return new string(chars);
    }

    /// <summary>chars → UTF-32 (LE/BE); surrogate pairs become one 32-bit unit.</summary>
    /// <summary>chars → UTF-32 (LE/BE); surrogate pairs become one 32-bit unit.</summary>
    internal static byte[] U32FromChars(string text, bool littleEndian)
    {
        var units = new List<byte>(text.Length * 4);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            uint cp;
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            { cp = (uint)char.ConvertToUtf32(ch, text[i + 1]); i++; }
            else if (char.IsSurrogate(ch)) throw new ArgumentException("lone surrogate in string");
            else cp = ch;
            if (littleEndian)
            { units.Add((byte)cp); units.Add((byte)(cp >> 8)); units.Add((byte)(cp >> 16)); units.Add((byte)(cp >> 24)); }
            else
            { units.Add((byte)(cp >> 24)); units.Add((byte)(cp >> 16)); units.Add((byte)(cp >> 8)); units.Add((byte)cp); }
        }
        return units.ToArray();
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
        => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static readonly byte[] Utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BeBom = [0x00, 0x00, 0xFE, 0xFF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0x00, 0xFE];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
}

/// <summary>
/// A multi-occurrence, guard-counted literal replace tool
/// (docs/plans/file-tool-reliability.md §2). Replaces ALL non-overlapping
/// literal occurrences of oldText; writes nothing unless the actual count
/// equals expectedCount exactly. No regex, no occurrence selector, no cascade
/// (replacement text is not rescanned).
/// </summary>
public sealed class ReplaceTool : IAgentTool, IFileTargetTool
{
    public string Name => "replace";

    public string Description =>
        "Replace ALL non-overlapping literal occurrences of oldText with newText in one file. " +
        "Writes nothing unless the actual occurrence count equals expectedCount exactly (the guard). " +
        "Matching is case-sensitive and CRLF/LF-equivalent; a lone CR is literal. " +
        "Use edit when the span is unique and replace when it occurs multiple times.";

    public IReadOnlyList<string> Guidelines => [
        "expectedCount is a GUARD: the tool writes only if the number of non-overlapping occurrences of oldText is exactly expectedCount (1 is valid, but edit is the usual unique-span tool).",
        "No regex and no fuzzy matching — oldText is literal; tabs/spaces/case must match exactly. The replacement text is not rescanned (no cascade).",
        "An explicit empty newText is valid deletion; whitespace search text is not trimmed.",
        "On a count mismatch the file is left untouched and the error reports the actual count — re-read the file to fix oldText or expectedCount.",
        "Same-file calls in one response run in your original order; a failed call in a file group skips the rest of that file's calls in this batch.",
    ];

    public JsonElement Parameters { get; } = Args.FileTargetSchema(
        ("path", "string", "File path to edit (required)."),
        ("oldText", "string", "Exact literal text to find (required, must not be empty)."),
        ("newText", "string", "Replacement text (required; must be present — an explicit \"\" deletes the matches)."),
        ("expectedCount", "integer", "The number of non-overlapping occurrences you EXPECT (required, positive integer, minimum 1). Nothing is written unless the actual count equals this exactly."));

    /// <summary>Declare the target path without reading the file (validation first).</summary>
    public string? GetTargetPath(ToolContext context)
        => Args.ValidPath(context) is { } p ? context.ResolvePath(p) : null;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext ctx, CancellationToken ct)
    {
        if (!Args.TryPath(ctx.Arguments, out var path))
            return Error("path is required (a nonblank string).");
        if (!Args.TryNonEmptyString(ctx.Arguments, "oldText", out var oldText))
            return Error("oldText is required (a nonempty string).");
        if (!Args.TryStringPresent(ctx.Arguments, "newText", out var newText))
            return Error("newText is required (a present string; \"\" is a valid deletion).");
        if (!Args.TryPositiveInt(ctx.Arguments, "expectedCount", out var expectedCount))
            return Error("expectedCount is required (a positive integer; fractional, zero or negative values are rejected).");

        var resolved = ctx.ResolvePath(path);
        var (ok, message, count) = await TextFileEditor.ReplaceAsync(resolved, oldText, newText, expectedCount, ct);
        if (!ok) return Error(message);
        return new ToolResult("tool", Name, [new TextPart($"{message} (count={count}).")], false);
    }

    private ToolResult Error(string message)
        => new("tool", Name, [new TextPart(message)], IsError: true);
}
