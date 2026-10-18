using System.Text.RegularExpressions;

namespace NetPI.CodeSearch;

/// <summary>A passage of a file: lines <paramref name="Start"/>..<paramref name="End"/> (1-based, inclusive), the
/// declaration it is (with its containers: <c>class Foo › void Bar()</c>) and its text.</summary>
internal sealed record Chunk(int Start, int End, string Signature, string Text);

/// <summary>
/// Files into passages by their symbols, without a language server: a declaration line (a class, a method, a function,
/// a markdown heading, per language by a regular expression) starts a passage, the comments and attributes right above it
/// go with it, and the declarations it sits in (by indentation) name its signature. Tiny passages (a field, a one-line
/// member) join the next one, long ones are cut into parts, and a file with no declaration the expressions know is cut into
/// line windows.
/// </summary>
internal static class Chunker
{
    /// <summary>A passage with fewer non-blank lines than this joins the next one.</summary>
    public const int MinLines = 4;
    /// <summary>The window of a file whose declarations are not known (and of each part of a long passage).</summary>
    public const int WindowLines = 60;
    private const int MaxSignature = 160;

    private static readonly RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // C#: types (class/record/struct/interface/enum, delegates), methods/constructors/operators/properties with a block,
    // namespaces. A line ending with ';' is a declaration without a body (a field, an abstract member): no passage.
    private static readonly Regex CSharpType = new(
        @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|file|ref|new)\s+)*(?:class|record(?:\s+struct|\s+class)?|struct|interface|enum|namespace)\s+[\w.]+", Opts);
    private static readonly Regex CSharpMember = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|sealed|abstract|async|override|virtual|unsafe|extern|new|partial|readonly|required|implicit|explicit)\s+)+[\w<>\[\],.?\s]*?[\w>\]?]\s+(?:operator\s*\S+|[\w.]+)\s*(?:<[^>]*>)?\s*\(", Opts);
    private static readonly Regex CSharpCtor = new(@"^\s*(?:public|private|protected|internal|static)\s+[A-Z]\w*\s*\(", Opts);
    // TS/JS/Svelte scripts
    private static readonly Regex JsDecl = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(?:function\*?\s+\w+|function\s*\(|class\s+\w+|interface\s+\w+|type\s+\w+\s*(?:<[^>]*>)?\s*=|enum\s+\w+|namespace\s+\w+)", Opts);
    private static readonly Regex JsArrow = new(
        @"^\s*(?:export\s+)?(?:const|let|var)\s+\w+\s*(?::[^=]+)?=\s*(?:async\s+)?(?:\([^)]*\)|\w+)\s*(?::[^=]+)?=>", Opts);
    private static readonly Regex JsMethod = new(
        @"^\s+(?:(?:public|private|protected|static|async|get|set|override|readonly)\s+)*#?\w+\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::\s*[^{]+)?\{\s*$", Opts);
    private static readonly Regex JsKeyword = new(@"^\s*(?:if|for|while|switch|catch|with|return|else|do|try)\b", Opts);
    private static readonly Regex SvelteBlock = new(@"^<(?:script|style)\b", Opts);
    private static readonly Regex Python = new(@"^\s*(?:async\s+)?(?:def|class)\s+\w+", Opts);
    private static readonly Regex Go = new(@"^(?:func|type)\s+", Opts);
    private static readonly Regex Rust = new(
        @"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:(?:async|const|unsafe|extern(?:\s+""\w+"")?)\s+)*(?:fn|struct|enum|trait|impl|mod|union|macro_rules!)\b", Opts);
    private static readonly Regex PowerShell = new(@"^\s*(?:function|filter|class)\s+[\w-]+", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex Markdown = new(@"^(#{1,6})\s+\S", Opts);
    private static readonly Regex Fence = new(@"^\s*(```|~~~)", Opts);

    /// <summary>The language a file's declarations are read as, or null (line windows).</summary>
    public static string? Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".tsx" or ".mts" or ".cts" or ".js" or ".jsx" or ".mjs" or ".cjs" => "js",
        ".svelte" or ".vue" => "svelte",
        ".py" or ".pyi" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".ps1" or ".psm1" => "powershell",
        ".md" or ".markdown" => "markdown",
        _ => null,
    };

    private sealed record Mark(int Line, int Level, string Text);

    /// <summary>The passages of a file's text. <paramref name="maxLines"/> caps a passage's lines, <paramref name="maxChars"/> its characters.</summary>
    public static List<Chunk> Split(string path, string text, int maxLines, int maxChars)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        if (lines.Length == 0) return [];
        maxLines = Math.Max(10, maxLines);
        maxChars = Math.Max(500, maxChars);
        var lang = Language(path);
        var marks = lang is null ? [] : Marks(lang, lines);
        var segments = new List<(int Start, int End, string Signature)>();
        if (marks.Count == 0)
        {
            Windows(lines, 0, lines.Length - 1, null, Math.Min(WindowLines, maxLines), segments);
        }
        else
        {
            var starts = marks.Select(m => LeadingStart(lang!, lines, m.Line, 0)).ToList();
            for (var i = 1; i < starts.Count; i++) starts[i] = Math.Max(starts[i], marks[i - 1].Line + 1);
            if (starts[0] > 0 && lines.Take(starts[0]).Any(l => l.Trim().Length > 0))
                segments.Add((0, starts[0] - 1, Path.GetFileName(path)));
            for (var i = 0; i < marks.Count; i++)
            {
                var end = i + 1 < marks.Count ? starts[i + 1] - 1 : lines.Length - 1;
                segments.Add((starts[i], end, SignatureOf(marks, i)));
            }
        }
        // join tiny passages (imports, a class's first line and its fields, one-line members) into the one after them, named
        // by that one: the most specific declaration of the passage (its containers are in its signature); then cut long ones
        var joined = new List<(int Start, int End, string Signature)>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end, sig) = segments[i];
            while (i + 1 < segments.Count && NonBlank(lines, start, end) < MinLines && segments[i + 1].End - start + 1 <= maxLines)
            {
                i++;
                (end, sig) = (segments[i].End, segments[i].Signature);
            }
            joined.Add((start, end, sig));
        }
        var chunks = new List<Chunk>();
        foreach (var (start, end, sig) in joined)
        {
            var (s, e) = Trim(lines, start, end);
            if (s > e) continue;
            var length = 0;
            for (var k = s; k <= e; k++) length += lines[k].Length + 1;
            if (e - s + 1 <= maxLines && length <= maxChars) { chunks.Add(Make(lines, s, e, sig)); continue; }
            var parts = new List<(int Start, int End, string Signature)>();
            Windows(lines, s, e, sig, Math.Min(maxLines, WindowLines), parts);
            foreach (var p in parts) chunks.AddRange(CutChars(lines, p.Start, p.End, p.Signature, maxChars));
        }
        return chunks;
    }

    private static Chunk Make(string[] lines, int s, int e, string sig) =>
        new(s + 1, e + 1, sig, string.Join("\n", lines[s..(e + 1)]));

    /// <summary>A part still over the character cap (very long lines) is cut by characters, at line ends where it can be.</summary>
    private static IEnumerable<Chunk> CutChars(string[] lines, int s, int e, string sig, int maxChars)
    {
        var from = s;
        var size = 0;
        for (var k = s; k <= e; k++)
        {
            if (k > from && size + lines[k].Length + 1 > maxChars)
            {
                yield return Make(lines, from, k - 1, sig);
                from = k;
                size = 0;
            }
            size += lines[k].Length + 1;
        }
        var last = Make(lines, from, e, sig);
        yield return last.Text.Length > maxChars ? last with { Text = last.Text[..maxChars] } : last;
    }

    /// <summary>Line windows over s..e, each cut at a blank line in its last third where there is one; parts are named "(part n)".</summary>
    private static void Windows(string[] lines, int s, int e, string? sig, int size, List<(int, int, string)> into)
    {
        var part = 0;
        var many = e - s + 1 > size;
        while (s <= e)
        {
            var end = Math.Min(e, s + size - 1);
            if (end < e)
                for (var k = end; k > s + size * 2 / 3; k--)
                    if (lines[k].Trim().Length == 0) { end = k; break; }
            part++;
            var name = sig ?? Clip(lines.Skip(s).Take(end - s + 1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "");
            into.Add((s, end, many && sig is not null ? $"{name} (part {part})" : name));
            s = end + 1;
        }
    }

    private static (int, int) Trim(string[] lines, int s, int e)
    {
        while (s <= e && lines[s].Trim().Length == 0) s++;
        while (e >= s && lines[e].Trim().Length == 0) e--;
        return (s, e);
    }

    private static int NonBlank(string[] lines, int s, int e)
    {
        var n = 0;
        for (var k = s; k <= e && k < lines.Length; k++) if (lines[k].Trim().Length > 0) n++;
        return n;
    }

    /// <summary>The declaration with the ones it sits in: the nearest earlier marks of a lower level, outermost first.</summary>
    private static string SignatureOf(List<Mark> marks, int i)
    {
        var chain = new List<string> { marks[i].Text };
        var level = marks[i].Level;
        for (var j = i - 1; j >= 0 && level > 0; j--)
            if (marks[j].Level < level)
            {
                chain.Insert(0, marks[j].Text);
                level = marks[j].Level;
            }
        // a long chain keeps its ends: the outermost (a file-scoped namespace says little) is dropped first
        while (chain.Count > 3) chain.RemoveAt(0);
        return string.Join(" › ", chain);
    }

    /// <summary>The first line of a declaration: the comments, attributes and decorators right above it (no blank line between).</summary>
    private static int LeadingStart(string lang, string[] lines, int line, int floor)
    {
        var at = line;
        while (at - 1 >= floor)
        {
            var t = lines[at - 1].TrimStart();
            var lead = t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("@")
                       || (lang == "csharp" && t.StartsWith('['))
                       || (lang == "rust" && t.StartsWith("#["))
                       || (lang is "python" or "powershell" && t.StartsWith('#'));
            if (!lead || t.Length == 0) break;
            at--;
        }
        return at;
    }

    private static List<Mark> Marks(string lang, string[] lines)
    {
        var marks = new List<Mark>();
        var fenced = false;
        var inScript = lang != "svelte";
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 400) continue;
            var indent = Indent(line);
            switch (lang)
            {
                case "markdown":
                    if (Fence.IsMatch(line)) { fenced = !fenced; continue; }
                    if (!fenced && Markdown.Match(line) is { Success: true } h) marks.Add(new Mark(i, h.Groups[1].Length - 1, line.Trim()));
                    continue;
                case "csharp":
                    var trimmed = line.TrimEnd();
                    if (trimmed.EndsWith(';') && !CSharpType.IsMatch(line)) continue;
                    if (trimmed.TrimStart().StartsWith("//")) continue;
                    if (CSharpType.IsMatch(line) || CSharpMember.IsMatch(line) || CSharpCtor.IsMatch(line))
                        marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
                case "svelte":
                    if (SvelteBlock.IsMatch(line)) { inScript = line.StartsWith("<script"); marks.Add(new Mark(i, 0, Signature(line))); continue; }
                    if (line.StartsWith("</script")) { inScript = false; continue; }
                    if (inScript && IsJs(line)) marks.Add(new Mark(i, indent + 1, Signature(line)));
                    continue;
                case "js":
                    if (IsJs(line)) marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
                case "python":
                    if (Python.IsMatch(line)) marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
                case "go":
                    if (Go.IsMatch(line)) marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
                case "rust":
                    if (Rust.IsMatch(line) && !line.TrimEnd().EndsWith(';')) marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
                case "powershell":
                    if (PowerShell.IsMatch(line)) marks.Add(new Mark(i, indent, Signature(line)));
                    continue;
            }
        }
        return marks;
    }

    private static bool IsJs(string line) =>
        JsDecl.IsMatch(line) || JsArrow.IsMatch(line) || (JsMethod.IsMatch(line) && !JsKeyword.IsMatch(line));

    private static int Indent(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>A declaration line as a signature: trimmed, an opening brace or arrow body left off, at most 160 characters.</summary>
    private static string Signature(string line)
    {
        var s = line.Trim();
        if (s.EndsWith('{')) s = s[..^1].TrimEnd();
        return Clip(s);
    }

    private static string Clip(string s) => s.Length > MaxSignature ? s[..MaxSignature] + "…" : s;
}
