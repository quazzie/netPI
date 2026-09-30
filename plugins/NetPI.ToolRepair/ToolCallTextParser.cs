using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.ToolRepair;

/// <summary>A tool call found in plain text.</summary>
public sealed class TextToolCall
{
    public string Name { get; set; } = "";
    /// <summary>XML-style parameters (<c>&lt;parameter=key&gt;value&lt;/parameter&gt;</c>), raw string values in order.</summary>
    public List<KeyValuePair<string, string>> Parameters { get; } = [];
    /// <summary>Arguments given as JSON (Hermes format or a JSON body inside &lt;function=…&gt;).</summary>
    public JsonObject? JsonArguments { get; set; }
    /// <summary>Span of the markup in the source text (removed when the call is converted).</summary>
    public int Start { get; set; }
    public int End { get; set; }
}

/// <summary>
/// Parses tool calls that models emitted as text instead of native function calls:
/// <list type="bullet">
/// <item>Qwen3-Coder XML: <c>&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=key&gt;value&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;</c></item>
/// <item>Hermes JSON: <c>&lt;tool_call&gt;{"name":"read","arguments":{"path":"x"}}&lt;/tool_call&gt;</c> (also arrays, "parameters", string arguments)</item>
/// <item>Bare <c>&lt;function=name&gt;…&lt;/function&gt;</c> and <c>&lt;function name="x"&gt;</c> / <c>&lt;parameter name="k"&gt;</c> variants</item>
/// </list>
/// Tolerates whitespace/newlines, missing closing tags at the end of the message and multi-line values
/// (one leading and one trailing newline of a value are trimmed).
/// </summary>
public static partial class ToolCallTextParser
{
    [GeneratedRegex(@"<tool_call\s*>", RegexOptions.IgnoreCase)] private static partial Regex OpenBlock();
    [GeneratedRegex(@"</tool_call\s*>", RegexOptions.IgnoreCase)] private static partial Regex CloseBlock();
    [GeneratedRegex(@"<function\s*=\s*[""']?(?<n>[^>""'\s]+)[""']?\s*>|<function\s+name\s*=\s*[""'](?<n>[^""']+)[""']\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex OpenFunc();
    [GeneratedRegex(@"</function\s*>", RegexOptions.IgnoreCase)] private static partial Regex CloseFunc();
    [GeneratedRegex(@"<parameter\s*=\s*[""']?(?<n>[^>""'\n]+?)[""']?\s*>|<parameter\s+name\s*=\s*[""'](?<n>[^""']+)[""']\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex OpenParam();
    [GeneratedRegex(@"</parameter\s*>", RegexOptions.IgnoreCase)] private static partial Regex CloseParam();
    [GeneratedRegex(@"<tool_call|<function\s*=|<function\s+name\s*=", RegexOptions.IgnoreCase)] private static partial Regex AnyMarkup();
    [GeneratedRegex(@"```[\w+-]*[ \t]*\r?\n?\s*```")] private static partial Regex EmptyFence();
    [GeneratedRegex(@"\n[ \t]*\n([ \t]*\n)+")] private static partial Regex ManyBlankLines();

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static bool ContainsMarkup(string? text) => !string.IsNullOrEmpty(text) && AnyMarkup().IsMatch(text);

    /// <summary>
    /// True when <paramref name="text"/> is <i>only</i> tool-call markup: the calls it parses cover everything but
    /// whitespace, optionally wrapped in a single code fence. Prose around the markup (an explanation, quoted source, a
    /// documentation example) is not an envelope, so it stays text and is never executed.
    /// </summary>
    public static bool IsCallEnvelope(string? text)
    {
        if (!ContainsMarkup(text)) return false;
        var t = UnwrapFence(text!.Trim());
        if (t is null) return false;
        var calls = Parse(t);
        if (calls.Count == 0) return false;
        var pos = 0;
        foreach (var call in calls.OrderBy(c => c.Start))
        {
            if (call.Start > pos && !IsBlank(t[pos..call.Start])) return false;
            if (call.End > call.Start) pos = Math.Max(pos, call.End);
        }
        return IsBlank(t[pos..]);
    }

    /// <summary>The content of a single code fence wrapping the whole text, or null when the text is not one fence.</summary>
    private static string? UnwrapFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var nl = text.IndexOf('\n');
        if (nl < 0 || !text.EndsWith("```", StringComparison.Ordinal) || text.Length < nl + 6) return null;
        var inner = text[(nl + 1)..^3].Trim();
        return inner.Length == 0 ? null : inner;
    }

    private static bool IsBlank(string s)
    {
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) return false;
        return true;
    }

    /// <summary>All tool calls in <paramref name="text"/>, in order.</summary>
    public static List<TextToolCall> Parse(string text)
    {
        var calls = new List<TextToolCall>();
        if (!ContainsMarkup(text)) return calls;
        var pos = 0;
        while (pos < text.Length)
        {
            var mb = OpenBlock().Match(text, pos);
            var mf = OpenFunc().Match(text, pos);
            if (!mb.Success && !mf.Success) break;

            if (mb.Success && (!mf.Success || mb.Index <= mf.Index))
            {
                var contentStart = mb.Index + mb.Length;
                var close = CloseBlock().Match(text, contentStart);
                var nextOpen = OpenBlock().Match(text, contentStart);
                int contentEnd, blockEnd;
                if (close.Success && (!nextOpen.Success || close.Index < nextOpen.Index))
                { contentEnd = close.Index; blockEnd = close.Index + close.Length; }
                else if (nextOpen.Success) contentEnd = blockEnd = nextOpen.Index; // unclosed block followed by another one
                else contentEnd = blockEnd = text.Length;                          // unclosed at the end of the message

                var found = ParseBlock(text, contentStart, contentEnd);
                if (found.Count == 0) { pos = contentStart; continue; }
                found[0].Start = mb.Index;
                found[^1].End = blockEnd;
                calls.AddRange(found);
                pos = blockEnd;
            }
            else
            {
                var call = ParseFunction(text, mf, text.Length);
                // A stray closing </tool_call> right after a bare function belongs to it.
                var after = SkipWhitespace(text, call.End, text.Length);
                var cb = CloseBlock().Match(text, after);
                if (cb.Success && cb.Index == after) call.End = cb.Index + cb.Length;
                calls.Add(call);
                pos = Math.Max(call.End, mf.Index + mf.Length);
            }
        }
        return calls;
    }

    /// <summary>Remove the given spans from <paramref name="text"/> and tidy up the leftovers.</summary>
    public static string RemoveSpans(string text, IEnumerable<(int Start, int End)> spans)
    {
        var ordered = spans.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var pos = 0;
        foreach (var (start, end) in ordered)
        {
            if (start < pos) { pos = Math.Max(pos, end); continue; }
            sb.Append(text, pos, start - pos);
            pos = end;
        }
        if (pos < text.Length) sb.Append(text, pos, text.Length - pos);
        var result = EmptyFence().Replace(sb.ToString(), "");
        result = ManyBlankLines().Replace(result, "\n\n");
        return result.Trim();
    }

    // ------------------------------------------------------------------ blocks

    private static List<TextToolCall> ParseBlock(string text, int start, int end)
    {
        var result = new List<TextToolCall>();
        var pos = start;
        var sawFunction = false;
        while (pos < end)
        {
            var mf = OpenFunc().Match(text, pos, end - pos);
            if (!mf.Success) break;
            sawFunction = true;
            var call = ParseFunction(text, mf, end);
            result.Add(call);
            pos = Math.Max(call.End, mf.Index + mf.Length);
        }
        if (sawFunction) return result;

        // Hermes-style JSON body.
        var body = StripFence(text[start..end].Trim());
        foreach (var obj in ParseJsonCalls(body))
        {
            var fn = obj["function"] as JsonObject;
            var name = Str(obj["name"]) ?? Str(fn?["name"]) ?? Str(obj["function"]) ?? Str(obj["tool"]) ?? Str(obj["tool_name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var args = obj["arguments"] ?? obj["parameters"] ?? obj["args"] ?? obj["input"] ?? fn?["arguments"] ?? fn?["parameters"];
            result.Add(new TextToolCall { Name = name.Trim(), JsonArguments = ToArgsObject(args), Start = start, End = end });
        }
        return result;
    }

    private static TextToolCall ParseFunction(string text, Match open, int limit)
    {
        var call = new TextToolCall { Name = open.Groups["n"].Value.Trim(), Start = open.Index };
        var p = open.Index + open.Length;
        var bodyStart = p;
        while (true)
        {
            var mp = OpenParam().Match(text, p, limit - p);
            var mc = CloseFunc().Match(text, p, limit - p);
            var mNextFunc = OpenFunc().Match(text, p, limit - p);
            // A new <function=…> before our close means ours was never closed.
            var stop = mc.Success ? mc.Index : limit;
            if (mNextFunc.Success && mNextFunc.Index < stop && (!mp.Success || mNextFunc.Index < mp.Index))
            {
                FinishBody(call, text, bodyStart, mNextFunc.Index);
                call.End = TrimEndIndex(text, call.Start, mNextFunc.Index);
                return call;
            }
            if (mp.Success && mp.Index < stop)
            {
                var key = mp.Groups["n"].Value.Trim();
                var vStart = mp.Index + mp.Length;
                var mpc = CloseParam().Match(text, vStart, limit - vStart);
                var mnp = OpenParam().Match(text, vStart, limit - vStart);
                int vEnd;
                if (mpc.Success && (!mnp.Success || mpc.Index < mnp.Index)) { vEnd = mpc.Index; p = mpc.Index + mpc.Length; }
                else if (mnp.Success) { vEnd = mnp.Index; p = vEnd; }
                else
                {
                    // Unterminated value: runs to </function>, </tool_call> or the end of the text.
                    var cf = CloseFunc().Match(text, vStart, limit - vStart);
                    var cb = CloseBlock().Match(text, vStart, limit - vStart);
                    vEnd = limit;
                    if (cf.Success) vEnd = cf.Index;
                    if (cb.Success && cb.Index < vEnd) vEnd = cb.Index;
                    p = vEnd;
                }
                call.Parameters.Add(new(key, TrimOneNewline(text[vStart..vEnd])));
                continue;
            }
            if (mc.Success)
            {
                FinishBody(call, text, bodyStart, mc.Index);
                call.End = mc.Index + mc.Length;
                return call;
            }
            // Unterminated function at the end of the text (or block).
            var cbEnd = CloseBlock().Match(text, p, limit - p);
            var end = cbEnd.Success ? cbEnd.Index : limit;
            FinishBody(call, text, bodyStart, end);
            call.End = end;
            return call;
        }
    }

    /// <summary>A function without &lt;parameter&gt; tags may carry a JSON arguments object as its body.</summary>
    private static void FinishBody(TextToolCall call, string text, int bodyStart, int bodyEnd)
    {
        if (call.Parameters.Count > 0 || bodyEnd <= bodyStart) return;
        var body = StripFence(text[bodyStart..bodyEnd].Trim());
        if (body.StartsWith('{') && TryParseObject(body, out var obj)) call.JsonArguments = obj;
    }

    private static int TrimEndIndex(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    private static int SkipWhitespace(string text, int pos, int limit)
    {
        while (pos < limit && char.IsWhiteSpace(text[pos])) pos++;
        return pos;
    }

    /// <summary>Trim exactly one leading and one trailing line break (values usually sit on their own lines).</summary>
    public static string TrimOneNewline(string s)
    {
        if (s.StartsWith("\r\n", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('\n')) s = s[1..];
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) s = s[..^2];
        else if (s.EndsWith('\n')) s = s[..^1];
        return s;
    }

    // ------------------------------------------------------------------ JSON

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var nl = s.IndexOf('\n');
        if (nl < 0) return s;
        s = s[(nl + 1)..];
        var endFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return (endFence >= 0 ? s[..endFence] : s).Trim();
    }

    private static IEnumerable<JsonObject> ParseJsonCalls(string body)
    {
        if (body.Length == 0) yield break;
        JsonNode? node = TryParse(body);
        if (node is null)
        {
            // Trailing garbage or a missing brace: try the outermost {...}.
            var a = body.IndexOf('{');
            var b = body.LastIndexOf('}');
            if (a >= 0 && b > a) node = TryParse(body[a..(b + 1)]);
            if (node is null && a >= 0) node = TryParse(body[a..] + "}") ?? TryParse(body[a..] + "}}");
        }
        switch (node)
        {
            case JsonObject o: yield return o; break;
            case JsonArray arr:
                foreach (var item in arr) if (item is JsonObject io) yield return io;
                break;
        }
    }

    private static JsonObject ToArgsObject(JsonNode? args)
    {
        switch (args)
        {
            case JsonObject o: return (JsonObject)o.DeepClone();
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Trim();
                if (s.Length == 0) return [];
                return TryParseObject(s, out var parsed) ? parsed : new JsonObject { ["input"] = s };
            default: return [];
        }
    }

    private static bool TryParseObject(string s, out JsonObject obj)
    {
        obj = null!;
        if (TryParse(s) is JsonObject o) { obj = o; return true; }
        return false;
    }

    private static JsonNode? TryParse(string s)
    {
        try { return JsonNode.Parse(s, null, DocOptions); } catch (JsonException) { return null; }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
