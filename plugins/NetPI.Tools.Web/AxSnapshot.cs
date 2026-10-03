using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Web;

/// <summary>One control of a snapshot: the page element it acts on, and its line in the list.</summary>
internal sealed record BrowserControl(int Backend, string Role, string Name, string Text, double Dist, int? Select, int? OptionOf, bool Password);

/// <summary>
/// The page's snapshot: the accessibility tree (role, name, value, state) joined with the DOM snapshot (bounds, ids,
/// input types) and the layout metrics — parsed from the three CDP answers, and the three ways a tab shows the
/// result: the numbered list (nearest the visible part first), the find answer, and what an action changed.
/// </summary>
internal static class AxSnapshot
{
    internal sealed record Snapshot(string Url, string Title, List<BrowserControl> Controls);

    private static readonly Dictionary<string, string> Types = new()
    {
        ["button"] = "button", ["link"] = "link", ["textbox"] = "textbox", ["searchbox"] = "textbox", ["combobox"] = "combobox",
        ["PopUpButton"] = "combobox", ["checkbox"] = "checkbox", ["switch"] = "checkbox", ["menuitemcheckbox"] = "checkbox",
        ["radio"] = "radio", ["menuitemradio"] = "radio", ["tab"] = "tab", ["menuitem"] = "menuitem", ["option"] = "option",
        ["listitem"] = "listitem", ["treeitem"] = "treeitem", ["slider"] = "slider", ["spinbutton"] = "spinbutton",
        ["columnheader"] = "columnheader", ["rowheader"] = "rowheader", ["cell"] = "cell", ["gridcell"] = "cell",
        ["RootWebArea"] = "document",
    };

    /// <summary>
    /// The page's controls: the accessibility tree in page order (ignored nodes skipped), joined with DOMSnapshot for
    /// bounds, ids and input types. Kept: controls by role, headings and text as context (a text repeating the name just
    /// before it is dropped), each with its name, value and states. Zero-size and layout-less nodes are left out, except
    /// the options of a &lt;select&gt; clicked open.
    /// </summary>
    /// <param name="ax">The <c>Accessibility.getFullAXTree</c> answer.</param>
    /// <param name="dom">The <c>DOMSnapshot.captureSnapshot</c> answer.</param>
    /// <param name="metrics">The <c>Page.getLayoutMetrics</c> answer (the visible part the distances measure from).</param>
    /// <param name="openSelects">The &lt;select&gt; boxes (backend ids) clicked open: only their options are listed.</param>
    public static Snapshot Parse(JsonElement ax, JsonElement dom, JsonElement metrics, string url, string title, IReadOnlyCollection<int> openSelects)
    {
        var ds = dom;
        var strings = ds.GetProperty("strings").EnumerateArray().Select(s => s.GetString() ?? "").ToArray();
        // a string index is -1 for a missing string (an attribute without a value)
        string Str(int i) => i >= 0 && i < strings.Length ? strings[i] : "";
        var doc = ds.GetProperty("documents")[0];
        var nodes = doc.GetProperty("nodes");
        var backendIds = nodes.GetProperty("backendNodeId").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var bounds = new Dictionary<int, double[]>();
        var layout = doc.GetProperty("layout");
        var nodeIndex = layout.GetProperty("nodeIndex").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var layoutBounds = layout.GetProperty("bounds").EnumerateArray().ToArray();
        for (var k = 0; k < nodeIndex.Length; k++)
            bounds[backendIds[nodeIndex[k]]] = layoutBounds[k].EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var ids = new Dictionary<int, string>();
        var passwords = new HashSet<int>();
        var attrs = nodes.GetProperty("attributes").EnumerateArray().ToArray();
        for (var ni = 0; ni < attrs.Length; ni++)
        {
            var a = attrs[ni].EnumerateArray().Select(x => x.GetInt32()).ToArray();
            for (var k = 0; k + 1 < a.Length; k += 2)
            {
                var name = Str(a[k]);
                if (name == "id") ids[backendIds[ni]] = Str(a[k + 1]);
                else if (name == "type" && Str(a[k + 1]).Equals("password", StringComparison.OrdinalIgnoreCase)) passwords.Add(backendIds[ni]);
            }
        }
        var vp = metrics.GetProperty("cssVisualViewport");
        var top = vp.GetProperty("pageY").GetDouble();
        var bottom = top + vp.GetProperty("clientHeight").GetDouble();

        var axNodes = ax.GetProperty("nodes").EnumerateArray().ToArray();
        var byId = new Dictionary<string, JsonElement>();
        foreach (var n in axNodes) byId[n.GetProperty("nodeId").GetString()!] = n;
        string Role(JsonElement n) => n.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        string? Val(JsonElement n, string prop) => n.TryGetProperty(prop, out var o) && o.TryGetProperty("value", out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null : null;
        JsonElement? Prop(JsonElement n, string name)
        {
            if (!n.TryGetProperty("properties", out var ps)) return null;
            foreach (var p in ps.EnumerateArray())
                if (p.GetProperty("name").GetString() == name && p.TryGetProperty("value", out var v) && v.TryGetProperty("value", out var vv)) return vv;
            return null;
        }
        int? Backend(JsonElement n) => n.TryGetProperty("backendDOMNodeId", out var b) ? b.GetInt32() : null;
        bool IsSelect(JsonElement n) => n.TryGetProperty("childIds", out var cs) && cs.EnumerateArray().Any(c => byId.TryGetValue(c.GetString()!, out var ch) && Role(ch) == "MenuListPopup");

        var list = new List<BrowserControl>();
        var seen = new Queue<string>();
        void Describe(JsonElement n, string role, int? optionOf)
        {
            var type = Types.GetValueOrDefault(role);
            var context = type is null && role is "StaticText" or "heading";
            if (type is null && !context) return;
            var name = Regex.Replace(Val(n, "name") ?? "", @"\s+", " ").Trim();
            if (context && name.Length == 0) return;
            var backend = Backend(n) ?? 0;
            var b = bounds.GetValueOrDefault(backend);
            if (role != "RootWebArea" && optionOf is null && (b is null || b[2] < 1 || b[3] < 1)) return;
            var id = ids.GetValueOrDefault(backend) ?? "";
            var value = role switch { "link" => Prop(n, "url")?.GetString(), "RootWebArea" => url, _ => Val(n, "value") } ?? "";
            if (name.Length == 0 && id.Length == 0 && value.Length == 0) return;
            if (context && seen.Contains(name)) return;
            seen.Enqueue(name);
            if (seen.Count > 4) seen.Dequeue();
            var shown = name.Length > 100 ? name[..100] + "…" : name;
            var sb = new StringBuilder($"[{(context ? (role == "heading" ? "heading" : "text") : type)}] {shown}");
            if (id.Length > 0 && id != name && !Regex.IsMatch(id, @"^[\d_:-]+$")) sb.Append($" id=\"{id}\"");
            if (role == "slider") sb.Append($" position={value} of {Prop(n, "valuemin")}-{Prop(n, "valuemax")}");
            else if (value.Length > 0 && value != name) sb.Append($" value=\"{(value.Length > 80 ? value[..80] + "…" : value).ReplaceLineEndings(" ")}\"");
            if (passwords.Contains(backend)) sb.Append(" (password)");
            var select = role == "combobox" && IsSelect(n) ? backend : (int?)null;
            if (Prop(n, "checked") is { } chk)
            {
                var on = chk.ValueKind == JsonValueKind.String ? chk.GetString() : chk.GetRawText();
                sb.Append(type == "radio" ? (on == "true" ? " (selected)" : "") : on == "true" ? " (checked)" : on == "mixed" ? " (mixed)" : " (unchecked)");
            }
            if (Prop(n, "selected") is { ValueKind: JsonValueKind.True }) sb.Append(" (selected)");
            if (select is not null) sb.Append(openSelects.Contains(select.Value) ? " (expanded)" : " (collapsed)");
            else if (Prop(n, "expanded") is { ValueKind: JsonValueKind.True or JsonValueKind.False } ex) sb.Append(ex.ValueKind == JsonValueKind.True ? " (expanded)" : " (collapsed)");
            if (Prop(n, "focused") is { ValueKind: JsonValueKind.True }) sb.Append(" (focused)");
            if (Prop(n, "disabled") is { ValueKind: JsonValueKind.True }) sb.Append(" (disabled)");
            var dist = b is null ? 0 : b[1] + b[3] < top ? top - (b[1] + b[3]) : b[1] > bottom ? b[1] - bottom : 0;
            list.Add(new BrowserControl(backend, role, name, sb.ToString(), dist, select, optionOf, passwords.Contains(backend)));
        }
        // inPopup: the <select> (backend id) whose options are below; they are listed only while it is clicked open
        void Visit(JsonElement n, int? inPopup, int depth)
        {
            if (depth > 400) return;
            var role = Role(n);
            if (inPopup is { } s && !openSelects.Contains(s)) return;
            var popupOf = role == "MenuListPopup" && n.TryGetProperty("parentId", out var pid) && byId.TryGetValue(pid.GetString()!, out var parent) ? Backend(parent) : inPopup;
            if (!(n.TryGetProperty("ignored", out var ig) && ig.GetBoolean())) Describe(n, role, inPopup);
            if (n.TryGetProperty("childIds", out var children))
                foreach (var c in children.EnumerateArray())
                    if (byId.TryGetValue(c.GetString()!, out var child)) Visit(child, popupOf, depth + 1);
        }
        var root = axNodes.FirstOrDefault(n => !n.TryGetProperty("parentId", out _));
        if (root.ValueKind == JsonValueKind.Object) Visit(root, null, 0);
        return new Snapshot(url, title, list);
    }

    /// <summary>The list: every control numbered, the ones nearest the visible part shown when there are more than max.</summary>
    public static ToolResult Render(string action, string result, Snapshot s, int max)
    {
        var sb = new StringBuilder();
        if (result.Length > 0) sb.Append(result).Append('\n');
        sb.Append($"Page: {s.Title} — {s.Url}\n");
        var shown = s.Controls.Count <= max ? Enumerable.Range(0, s.Controls.Count)
            : s.Controls.Select((c, k) => (c.Dist, k)).OrderBy(x => x.Dist).Take(max).Select(x => x.k).Order();
        var shownList = shown.ToList();
        if (shownList.Count < s.Controls.Count)
            sb.Append($"({s.Controls.Count} controls; the {shownList.Count} nearest the visible part are listed: scroll or find for the others)\n");
        foreach (var k in shownList) sb.Append($"[{k + 1}] {s.Controls[k].Text}\n");
        return new ToolResult
        {
            Content = sb.ToString().TrimEnd(),
            Details = new { action, url = s.Url, title = s.Title, controls = s.Controls.Count, shown = shownList.Count, result },
        };
    }

    public static ToolResult Found(Snapshot s, string text)
    {
        var hits = s.Controls.Select((c, k) => (c, k)).Where(x => x.c.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(x => x.k).ToList();
        var sb = new StringBuilder($"Page: {s.Title} — {s.Url}\n");
        if (hits.Count == 0)
            return new ToolResult { Content = sb.Append($"No control contains \"{text}\" ({s.Controls.Count} controls).").ToString(), Details = new { action = "find", url = s.Url, text, hits = 0 } };
        sb.Append($"{hits.Count} control(s) contain \"{text}\"{(hits.Count > 10 ? "; the first 10 with the controls around them" : ", with the controls around them")}:\n");
        var lines = new SortedSet<int>();
        foreach (var h in hits.Take(10))
            for (var k = Math.Max(0, h - 3); k <= Math.Min(s.Controls.Count - 1, h + 6); k++) lines.Add(k);
        var last = -2;
        foreach (var k in lines)
        {
            if (k != last + 1 && last >= 0) sb.Append("…\n");
            sb.Append($"[{k + 1}] {s.Controls[k].Text}\n");
            last = k;
        }
        return new ToolResult { Content = sb.ToString().TrimEnd(), Details = new { action = "find", url = s.Url, text, hits = hits.Count } };
    }

    /// <summary>What an action changed: the first new controls, and how many went away (focus marks ignored).</summary>
    public static string Effect(List<BrowserControl> before, List<BrowserControl> after)
    {
        static string Clean(BrowserControl c) => c.Text.Replace(" (focused)", "", StringComparison.Ordinal);
        var b = before.Select(Clean).ToList();
        var a = after.Select(Clean).ToList();
        if (b.SequenceEqual(a)) return "No visible change.";
        var had = b.ToHashSet();
        var has = a.ToHashSet();
        var fresh = a.Where(x => !had.Contains(x)).ToList();
        var gone = b.Count(x => !has.Contains(x));
        if (fresh.Count == 0 && gone == 0) return "The order of the controls changed.";
        var parts = new List<string>();
        if (fresh.Count > 0)
            parts.Add("Now shows " + string.Join("; ", fresh.Take(3).Select(x => x.Length > 70 ? x[..70] + "…" : x)) + (fresh.Count > 3 ? $" (+{fresh.Count - 3} more)" : ""));
        if (gone > 0) parts.Add($"{gone} control(s) gone");
        return string.Join(", ", parts) + ".";
    }
}
