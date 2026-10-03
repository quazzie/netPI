using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Web;

/// <summary>
/// One control of a snapshot: the page element it acts on, and its line in the list. <see cref="Session"/> is the
/// DevTools session that owns the node ("" in the parser's tests: the page's own), <see cref="Number"/> the number the
/// model sees, kept for the node as long as the document lives.
/// </summary>
internal sealed record BrowserControl(int Backend, string Role, string Name, string Text, double Dist, string? Select, string? OptionOf, bool Password)
{
    public string Session { get; init; } = "";
    /// <summary>Bounds in the page's document coordinates (x, y, width, height), or null (no box / not known).</summary>
    public double[]? Bounds { get; init; }
    /// <summary>In a frame, not the page's own document (its bounds may measure from the frame).</summary>
    public bool InFrame { get; init; }
    public int Number { get; set; }
    public string Key => KeyOf(Session, Backend);
    public static string KeyOf(string session, int backend) => session + ":" + backend.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The page's snapshot: the accessibility tree (role, name, value, state) of every frame joined with the DOM snapshot
/// (bounds, ids, input types) and the layout metrics — parsed from the CDP answers — and the ways a tab shows the result:
/// the numbered list (nearest the visible part first), the changes an action made, and the find answer.
/// </summary>
internal static class AxSnapshot
{
    internal sealed record Snapshot(string Url, string Title, List<BrowserControl> Controls);

    /// <summary>
    /// One frame's trees. <paramref name="Session"/>: the session the nodes belong to (an out-of-process frame has its
    /// own). <paramref name="OwnerBackend"/>: the &lt;iframe&gt; element in <paramref name="ParentSession"/> the frame
    /// shows in (null for the page itself), where its controls are listed. <paramref name="OffsetX"/>/<paramref name="OffsetY"/>:
    /// where the frame's document starts in the page's document coordinates (an out-of-process frame's DOM snapshot
    /// measures from its own corner).
    /// </summary>
    internal sealed record Frame(string Session, JsonElement Ax, JsonElement Dom, int? OwnerBackend = null, string? ParentSession = null, double OffsetX = 0, double OffsetY = 0);

    private static readonly Dictionary<string, string> Types = new()
    {
        ["button"] = "button", ["link"] = "link", ["textbox"] = "textbox", ["searchbox"] = "textbox", ["combobox"] = "combobox",
        ["PopUpButton"] = "combobox", ["checkbox"] = "checkbox", ["switch"] = "checkbox", ["menuitemcheckbox"] = "checkbox",
        ["radio"] = "radio", ["menuitemradio"] = "radio", ["tab"] = "tab", ["menuitem"] = "menuitem", ["option"] = "option",
        ["listitem"] = "listitem", ["treeitem"] = "treeitem", ["slider"] = "slider", ["spinbutton"] = "spinbutton",
        ["columnheader"] = "columnheader", ["rowheader"] = "rowheader", ["cell"] = "cell", ["gridcell"] = "cell",
        ["RootWebArea"] = "document", ["Iframe"] = "frame", ["IframePresentational"] = "frame",
    };

    /// <summary>One page, one frame: what the parser's tests feed it.</summary>
    public static Snapshot Parse(JsonElement ax, JsonElement dom, JsonElement metrics, string url, string title, IReadOnlyCollection<string> openSelects) =>
        Parse([new Frame("", ax, dom)], metrics, url, title, openSelects);

    /// <summary>
    /// The page's controls: each frame's accessibility tree in page order (ignored nodes skipped; a frame's controls
    /// listed where its &lt;iframe&gt; is), joined with the DOM snapshot for bounds, ids and input types. Kept: controls
    /// by role, headings and text as context (a text repeating the name just before it is dropped), each with its name,
    /// value and states. Zero-size and layout-less nodes are left out, except the options of a &lt;select&gt; clicked open.
    /// </summary>
    /// <param name="frames">The page first, then its frames.</param>
    /// <param name="metrics">The <c>Page.getLayoutMetrics</c> answer (the visible part the distances measure from).</param>
    /// <param name="openSelects">The &lt;select&gt; boxes (control keys) clicked open: only their options are listed.</param>
    public static Snapshot Parse(IReadOnlyList<Frame> frames, JsonElement metrics, string url, string title, IReadOnlyCollection<string> openSelects)
    {
        var vp = metrics.GetProperty("cssVisualViewport");
        var top = vp.GetProperty("pageY").GetDouble();
        var bottom = top + vp.GetProperty("clientHeight").GetDouble();

        // the DOM facts per session (frames of one process share one DOM snapshot)
        var doms = new Dictionary<string, DomFacts>();
        foreach (var f in frames)
            if (!doms.ContainsKey(f.Session) && f.Dom.ValueKind == JsonValueKind.Object)
                doms[f.Session] = DomFacts.Read(f.Dom, f.OffsetX, f.OffsetY);

        var list = new List<BrowserControl>();
        var seen = new Queue<string>();
        var visited = new HashSet<Frame>(ReferenceEqualityComparer.Instance);

        void VisitFrame(Frame frame, int depth)
        {
            if (!visited.Add(frame) || frame.Ax.ValueKind != JsonValueKind.Object || !frame.Ax.TryGetProperty("nodes", out var nodesEl)) return;
            var dom = doms.GetValueOrDefault(frame.Session) ?? DomFacts.Empty;
            var axNodes = nodesEl.EnumerateArray().ToArray();
            var byId = new Dictionary<string, JsonElement>();
            foreach (var n in axNodes) byId[n.GetProperty("nodeId").GetString()!] = n;
            string Key(int backend) => BrowserControl.KeyOf(frame.Session, backend);
            bool IsSelect(JsonElement n) => n.TryGetProperty("childIds", out var cs) && cs.EnumerateArray().Any(c => byId.TryGetValue(c.GetString()!, out var ch) && Role(ch) == "MenuListPopup");

            void Describe(JsonElement n, string role, string? optionOf)
            {
                var type = Types.GetValueOrDefault(role);
                var context = type is null && role is "StaticText" or "heading";
                if (type is null && !context) return;
                // a frame's own document is not a line of its own: the <iframe> line says where it starts
                if (role == "RootWebArea" && frame.OwnerBackend is not null) return;
                var name = Regex.Replace(Val(n, "name") ?? "", @"\s+", " ").Trim();
                if (context && name.Length == 0) return;
                var backend = Backend(n) ?? 0;
                var b = dom.Bounds.GetValueOrDefault(backend);
                if (role != "RootWebArea" && optionOf is null && (b is null || b[2] < 1 || b[3] < 1)) return;
                var id = dom.Ids.GetValueOrDefault(backend) ?? "";
                var value = role switch { "link" => Prop(n, "url")?.GetString(), "RootWebArea" => url, _ => Val(n, "value") } ?? "";
                if (type == "frame") value = dom.Sources.GetValueOrDefault(backend) ?? value;
                if (name.Length == 0 && id.Length == 0 && value.Length == 0) return;
                if (context && seen.Contains(name)) return;
                seen.Enqueue(name);
                if (seen.Count > 4) seen.Dequeue();
                var shown = name.Length > 100 ? name[..100] + "…" : name;
                var sb = new StringBuilder($"[{(context ? (role == "heading" ? "heading" : "text") : type)}] {shown}");
                if (id.Length > 0 && id != name && !Regex.IsMatch(id, @"^[\d_:-]+$")) sb.Append($" id=\"{id}\"");
                if (role == "slider") sb.Append($" position={value} of {Prop(n, "valuemin")}-{Prop(n, "valuemax")}");
                else if (value.Length > 0 && value != name) sb.Append($" value=\"{(value.Length > 80 ? value[..80] + "…" : value).ReplaceLineEndings(" ")}\"");
                if (dom.Passwords.Contains(backend)) sb.Append(" (password)");
                if (dom.Files.Contains(backend)) sb.Append(" (file)");
                var select = role == "combobox" && IsSelect(n) ? Key(backend) : null;
                if (Prop(n, "checked") is { } chk)
                {
                    var on = chk.ValueKind == JsonValueKind.String ? chk.GetString() : chk.GetRawText();
                    sb.Append(type == "radio" ? (on == "true" ? " (selected)" : "") : on == "true" ? " (checked)" : on == "mixed" ? " (mixed)" : " (unchecked)");
                }
                if (Prop(n, "selected") is { ValueKind: JsonValueKind.True }) sb.Append(" (selected)");
                if (select is not null) sb.Append(openSelects.Contains(select) ? " (expanded)" : " (collapsed)");
                else if (Prop(n, "expanded") is { ValueKind: JsonValueKind.True or JsonValueKind.False } ex) sb.Append(ex.ValueKind == JsonValueKind.True ? " (expanded)" : " (collapsed)");
                if (Prop(n, "focused") is { ValueKind: JsonValueKind.True }) sb.Append(" (focused)");
                if (Prop(n, "disabled") is { ValueKind: JsonValueKind.True }) sb.Append(" (disabled)");
                var dist = b is null ? 0 : b[1] + b[3] < top ? top - (b[1] + b[3]) : b[1] > bottom ? b[1] - bottom : 0;
                list.Add(new BrowserControl(backend, role, name, sb.ToString(), dist, select, optionOf, dom.Passwords.Contains(backend))
                {
                    Session = frame.Session,
                    Bounds = b,
                    InFrame = frame.OwnerBackend is not null,
                });
            }

            // inPopup: the <select> (key) whose options are below; they are listed only while it is clicked open
            void Visit(JsonElement n, string? inPopup, int d)
            {
                if (d > 400) return;
                var role = Role(n);
                if (inPopup is { } s && !openSelects.Contains(s)) return;
                var popupOf = role == "MenuListPopup" && n.TryGetProperty("parentId", out var pid) && byId.TryGetValue(pid.GetString()!, out var parent) && Backend(parent) is { } pb
                    ? Key(pb) : inPopup;
                if (!(n.TryGetProperty("ignored", out var ig) && ig.GetBoolean())) Describe(n, role, inPopup);
                // a frame shows where its <iframe> is
                if (Backend(n) is { } own)
                    foreach (var child in frames)
                        if (child.OwnerBackend == own && (child.ParentSession ?? frames[0].Session) == frame.Session && depth < 8) VisitFrame(child, depth + 1);
                if (n.TryGetProperty("childIds", out var children))
                    foreach (var c in children.EnumerateArray())
                        if (byId.TryGetValue(c.GetString()!, out var ch)) Visit(ch, popupOf, d + 1);
            }

            var root = axNodes.FirstOrDefault(n => !n.TryGetProperty("parentId", out _));
            if (root.ValueKind == JsonValueKind.Object) Visit(root, null, 0);
        }

        if (frames.Count > 0) VisitFrame(frames[0], 0);
        // frames whose <iframe> was not in the tree (hidden, or the tree was cut): at the end
        foreach (var f in frames.Skip(1)) VisitFrame(f, 1);
        return new Snapshot(url, title, list);
    }

    private static string Role(JsonElement n) => n.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";

    private static string? Val(JsonElement n, string prop) => n.TryGetProperty(prop, out var o) && o.TryGetProperty("value", out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null : null;

    private static JsonElement? Prop(JsonElement n, string name)
    {
        if (!n.TryGetProperty("properties", out var ps)) return null;
        foreach (var p in ps.EnumerateArray())
            if (p.GetProperty("name").GetString() == name && p.TryGetProperty("value", out var v) && v.TryGetProperty("value", out var vv)) return vv;
        return null;
    }

    private static int? Backend(JsonElement n) => n.TryGetProperty("backendDOMNodeId", out var b) ? b.GetInt32() : null;

    /// <summary>What the DOM snapshot says about the nodes: their boxes, ids, password and file inputs, iframe sources.</summary>
    private sealed class DomFacts
    {
        public static readonly DomFacts Empty = new();
        public Dictionary<int, double[]> Bounds { get; } = [];
        public Dictionary<int, string> Ids { get; } = [];
        public Dictionary<int, string> Sources { get; } = [];
        public HashSet<int> Passwords { get; } = [];
        public HashSet<int> Files { get; } = [];

        public static DomFacts Read(JsonElement ds, double offsetX, double offsetY)
        {
            var facts = new DomFacts();
            var strings = ds.GetProperty("strings").EnumerateArray().Select(s => s.GetString() ?? "").ToArray();
            // a string index is -1 for a missing string (an attribute without a value)
            string Str(int i) => i >= 0 && i < strings.Length ? strings[i] : "";
            foreach (var doc in ds.GetProperty("documents").EnumerateArray())
            {
                var nodes = doc.GetProperty("nodes");
                var backendIds = nodes.GetProperty("backendNodeId").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var layout = doc.GetProperty("layout");
                var nodeIndex = layout.GetProperty("nodeIndex").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                var layoutBounds = layout.GetProperty("bounds").EnumerateArray().ToArray();
                for (var k = 0; k < nodeIndex.Length && k < layoutBounds.Length; k++)
                {
                    var b = layoutBounds[k].EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    if (b.Length >= 4) { b[0] += offsetX; b[1] += offsetY; }
                    facts.Bounds[backendIds[nodeIndex[k]]] = b;
                }
                if (!nodes.TryGetProperty("attributes", out var attrsEl)) continue;
                var attrs = attrsEl.EnumerateArray().ToArray();
                for (var ni = 0; ni < attrs.Length && ni < backendIds.Length; ni++)
                {
                    var a = attrs[ni].EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    for (var k = 0; k + 1 < a.Length; k += 2)
                    {
                        var name = Str(a[k]);
                        var value = Str(a[k + 1]);
                        if (name == "id") facts.Ids[backendIds[ni]] = value;
                        else if (name == "src") facts.Sources[backendIds[ni]] = value;
                        else if (name == "type" && value.Equals("password", StringComparison.OrdinalIgnoreCase)) facts.Passwords.Add(backendIds[ni]);
                        else if (name == "type" && value.Equals("file", StringComparison.OrdinalIgnoreCase)) facts.Files.Add(backendIds[ni]);
                    }
                }
            }
            return facts;
        }
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>The controls the list shows: all of them, or over <paramref name="max"/> (0: no cap) the ones nearest the visible part, in page order.</summary>
    public static List<BrowserControl> Window(List<BrowserControl> controls, int max) =>
        max <= 0 || controls.Count <= max ? controls
            : [.. controls.Select((c, k) => (c, k)).OrderBy(x => x.c.Dist).ThenBy(x => x.k).Take(max).OrderBy(x => x.k).Select(x => x.c)];

    /// <summary>The whole list: every control numbered, the ones nearest the visible part when there are more than max.</summary>
    public static ToolResult Render(string action, string result, Snapshot s, int max, ISet<int>? seen = null)
    {
        var sb = new StringBuilder();
        if (result.Length > 0) sb.Append(result).Append('\n');
        sb.Append($"Page: {s.Title} — {s.Url}\n");
        var shown = Window(s.Controls, max);
        if (shown.Count < s.Controls.Count)
            sb.Append($"({s.Controls.Count} controls; the {shown.Count} nearest the visible part are listed: scroll, find or snapshot with all: true for the others)\n");
        foreach (var c in shown)
        {
            sb.Append($"[{c.Number}] {c.Text}\n");
            seen?.Add(c.Number);
        }
        return new ToolResult
        {
            Content = sb.ToString().TrimEnd(),
            Details = new { action, url = s.Url, title = s.Title, controls = s.Controls.Count, shown = shown.Count, result },
        };
    }

    /// <summary>
    /// What an action changed, by number: the controls that are new or read differently (focus marks ignored), the ones
    /// now in view that the model has not been shown (a scroll), and how many went away. The rest kept their numbers and
    /// read as the model last saw them, so they are not repeated.
    /// </summary>
    public static ToolResult Changes(string action, string result, Snapshot s, List<BrowserControl> before, ISet<int> seen, int max)
    {
        static string Clean(string text) => text.Replace(" (focused)", "", StringComparison.Ordinal);
        var had = before.ToDictionary(c => c.Number, c => Clean(c.Text));
        var now = s.Controls.Select(c => c.Number).ToHashSet();
        var fresh = s.Controls.Where(c => !had.ContainsKey(c.Number)).ToList();
        var changed = s.Controls.Where(c => had.TryGetValue(c.Number, out var t) && t != Clean(c.Text)).ToList();
        var gone = before.Where(c => !now.Contains(c.Number)).Select(c => c.Number).ToList();
        var inView = Window(s.Controls, max).Where(c => c.Dist == 0 && !seen.Contains(c.Number) && had.ContainsKey(c.Number) && had[c.Number] == Clean(c.Text)).ToList();
        var sb = new StringBuilder();
        if (result.Length > 0) sb.Append(result).Append('\n');
        sb.Append($"Page: {s.Title} — {s.Url}\n");
        if (fresh.Count == 0 && changed.Count == 0 && gone.Count == 0 && inView.Count == 0)
        {
            sb.Append("No visible change.");
            return new ToolResult { Content = sb.ToString(), Details = new { action, url = s.Url, title = s.Title, controls = s.Controls.Count, shown = 0, result } };
        }
        var parts = new List<string>();
        if (fresh.Count > 0) parts.Add($"{fresh.Count} new");
        if (changed.Count > 0) parts.Add($"{changed.Count} changed");
        if (gone.Count > 0) parts.Add($"{gone.Count} gone ({Numbers(gone)})");
        if (inView.Count > 0) parts.Add($"{inView.Count} more in view");
        sb.Append("Changes: ").Append(string.Join(", ", parts)).Append(".\n");
        var listed = new HashSet<int>(fresh.Concat(changed).Concat(inView).Select(c => c.Number));
        var lines = s.Controls.Where(c => listed.Contains(c.Number)).ToList();
        var window = Window(lines, max);
        foreach (var c in window)
        {
            sb.Append($"[{c.Number}] {c.Text}\n");
            seen.Add(c.Number);
        }
        if (window.Count < lines.Count) sb.Append($"({lines.Count - window.Count} more changed further away: find or snapshot lists them)\n");
        var rest = s.Controls.Count - lines.Count;
        if (rest > 0) sb.Append($"The other {rest} controls keep their numbers; snapshot lists them all.");
        return new ToolResult
        {
            Content = sb.ToString().TrimEnd(),
            Details = new { action, url = s.Url, title = s.Title, controls = s.Controls.Count, shown = window.Count, result },
        };
    }

    /// <summary>"3, 5–9, 12": numbers in ranges.</summary>
    internal static string Numbers(IEnumerable<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j > i + 1 ? $"{sorted[i]}–{sorted[j]}" : j == i + 1 ? $"{sorted[i]}, {sorted[j]}" : $"{sorted[i]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    public static ToolResult Found(Snapshot s, string text)
    {
        var hits = s.Controls.Select((c, k) => (c, k)).Where(x => x.c.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(x => x.k).ToList();
        var sb = new StringBuilder($"Page: {s.Title} — {s.Url}\n");
        if (hits.Count == 0)
            return new ToolResult { Content = sb.Append($"No control contains \"{text}\" ({s.Controls.Count} controls).").ToString(), Details = new { action = "find", url = s.Url, text, hits = 0 } };
        sb.Append($"{hits.Count} control(s) contain \"{text}\"{(hits.Count > 20 ? "; the first 20 with the controls around them" : ", with the controls around them")}:\n");
        var lines = new SortedSet<int>();
        foreach (var h in hits.Take(20))
            for (var k = Math.Max(0, h - 3); k <= Math.Min(s.Controls.Count - 1, h + 6); k++) lines.Add(k);
        var last = -2;
        foreach (var k in lines)
        {
            if (k != last + 1 && last >= 0) sb.Append("…\n");
            sb.Append($"[{s.Controls[k].Number}] {s.Controls[k].Text}\n");
            last = k;
        }
        return new ToolResult { Content = sb.ToString().TrimEnd(), Details = new { action = "find", url = s.Url, text, hits = hits.Count } };
    }
}
