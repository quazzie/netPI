using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.Skills;

/// <summary>
/// The YAML frontmatter of a SKILL.md, read leniently without a YAML library: top-level <c>key: value</c> pairs with
/// plain, quoted and block (<c>|</c>, <c>&gt;</c>) scalars, lists (<c>- item</c> or <c>[a, b]</c>) and one level of nested
/// map (<c>metadata</c>). A plain value may contain colons ("Use when: …"): strict YAML rejects that, but skills written
/// for other clients often have it. Values are strings, <c>List&lt;string&gt;</c> or <c>Dictionary&lt;string, string&gt;</c>.
/// </summary>
internal static partial class Frontmatter
{
    public sealed record Result(Dictionary<string, object> Fields, string Body, int BodyLine);

    [GeneratedRegex(@"^([A-Za-z0-9_][A-Za-z0-9_.-]*)\s*:(?:\s+(.*))?$")]
    private static partial Regex KeyRx();

    /// <summary>Null when the text doesn't start with a <c>---</c> line or the frontmatter isn't closed.</summary>
    public static Result? Parse(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].TrimEnd() != "---") return null;
        var start = ++i;
        var end = -1;
        for (; i < lines.Length; i++)
            if (lines[i].TrimEnd() is "---" or "...") { end = i; break; }
        if (end < 0) return null;
        var body = string.Join('\n', lines[(end + 1)..]);
        // the 1-based line of the body's first non-blank line (to say where a cut SKILL.md continues)
        var bodyLine = end + 2;
        foreach (var l in lines[(end + 1)..])
        {
            if (l.Trim().Length > 0) break;
            bodyLine++;
        }
        return new Result(ParseBlock(lines[start..end]), body.Trim(), bodyLine);
    }

    private static Dictionary<string, object> ParseBlock(string[] lines)
    {
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i++];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || char.IsWhiteSpace(line[0])) continue;
            var m = KeyRx().Match(line.TrimEnd());
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var rest = m.Groups[2].Value.Trim();

            // the value's own lines: indented ones (and blank ones between them); with an empty value also "- item" lines
            var block = new List<string>();
            while (i < lines.Length)
            {
                var l = lines[i];
                if (l.Trim().Length == 0 || char.IsWhiteSpace(l[0]) || (rest.Length == 0 && l.StartsWith('-'))) { block.Add(l); i++; }
                else break;
            }
            while (block.Count > 0 && block[^1].Trim().Length == 0) block.RemoveAt(block.Count - 1);
            fields[key] = Value(rest, block);
        }
        return fields;
    }

    private static object Value(string rest, List<string> block)
    {
        if (rest.StartsWith('|') || rest.StartsWith('>')) return BlockScalar(rest[0] == '|', block);
        if (rest.StartsWith('"') || rest.StartsWith('\''))
            return Quoted(string.Join(' ', new[] { rest }.Concat(block.Select(b => b.Trim())).Where(s => s.Length > 0)));
        if (rest.StartsWith('[')) return FlowList(string.Join(' ', new[] { rest }.Concat(block.Select(b => b.Trim()))));
        if (rest.Length > 0)
        {
            // a plain scalar, folded over its continuation lines
            var parts = new[] { StripComment(rest) }.Concat(block.Select(b => b.Trim()).Where(s => s.Length > 0));
            return string.Join(' ', parts);
        }
        var first = block.Select(b => b.Trim()).FirstOrDefault(s => s.Length > 0);
        if (first is null) return "";
        if (first == "-" || first.StartsWith("- ", StringComparison.Ordinal))
        {
            var items = new List<string>();
            foreach (var b in block.Select(b => b.Trim()))
                if (b == "-" || b.StartsWith("- ", StringComparison.Ordinal)) items.Add(Scalar(b[1..].Trim()));
                else if (b.Length > 0 && items.Count > 0) items[^1] = (items[^1] + " " + b).Trim();
            return items;
        }
        if (KeyRx().IsMatch(first))
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in block.Select(b => b.Trim()))
                if (KeyRx().Match(b) is { Success: true } km) map[km.Groups[1].Value] = Scalar(km.Groups[2].Value.Trim());
            return map;
        }
        return string.Join(' ', block.Select(b => b.Trim()).Where(s => s.Length > 0));
    }

    /// <summary><c>|</c> keeps the line breaks, <c>&gt;</c> folds lines into paragraphs; the chomping indicator is ignored (values are trimmed).</summary>
    private static string BlockScalar(bool literal, List<string> block)
    {
        var indent = block.Where(b => b.Trim().Length > 0).Select(b => b.Length - b.TrimStart().Length).DefaultIfEmpty(0).Min();
        var lines = block.Select(b => b.Length >= indent ? b[indent..].TrimEnd() : b.Trim()).ToList();
        if (literal) return string.Join('\n', lines).Trim();
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (l.Length == 0) sb.Append('\n');
            else if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' ').Append(l);
            else sb.Append(l);
        }
        return sb.ToString().Trim();
    }

    private static string Quoted(string s)
    {
        var q = s[0];
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (q == '"' && c == '\\' && i + 1 < s.Length)
            {
                var n = s[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', '"' => '"', '\\' => '\\', '/' => '/', _ => n });
            }
            else if (c == q)
            {
                if (q == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; }
                else return sb.ToString().Trim();
            }
            else sb.Append(c);
        }
        return sb.ToString().Trim(); // not closed: take what is there
    }

    private static List<string> FlowList(string s)
    {
        var inner = s.Trim().TrimStart('[');
        var close = inner.LastIndexOf(']');
        if (close >= 0) inner = inner[..close];
        return inner.Split(',').Select(x => Scalar(x.Trim())).Where(x => x.Length > 0).ToList();
    }

    private static string Scalar(string s) =>
        s.Length > 0 && (s[0] == '"' || s[0] == '\'') ? Quoted(s) : StripComment(s);

    private static string StripComment(string s)
    {
        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? s[..hash] : s).Trim();
    }

    public static string? Str(this Dictionary<string, object> fields, string key) => fields.GetValueOrDefault(key) switch
    {
        string s => s,
        List<string> l => string.Join(' ', l),
        _ => null,
    };

    public static bool Flag(this Dictionary<string, object> fields, string key) =>
        fields.Str(key)?.Trim().ToLowerInvariant() is "true" or "yes" or "on";

    public static IReadOnlyList<string> Words(this Dictionary<string, object> fields, string key) => fields.GetValueOrDefault(key) switch
    {
        List<string> l => l,
        string s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };
}
