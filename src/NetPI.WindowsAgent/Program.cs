// netpi-windows-agent: UI Automation for the windows tool (plugins/NetPI.Tools.Windows), one JSON request per stdin
// line, one JSON answer per stdout line ({ id, ... } or { id, error }). Grown from decisions-lab's uia-agent, which the
// live computer-use measurements ran on (docs/DECISION-MODELS.md, "Live computer use").
//
//   { cmd: "windows" }                                   visible top-level windows
//   { cmd: "launch", exe, args?, title? }                start a program, wait for its new window
//   { cmd: "snapshot", hwnd, offscreen? }                the window's controls (plus its menus, popups, owned dialogs),
//                                                        numbered: a control keeps its number while it lives
//   { cmd: "act", hwnd, n, action, text?, direction? }   click | double | right | hover | type | toggle | expand | collapse |
//                                                        select | scroll | focus | read on control n
//   { cmd: "keys", hwnd, keys }                          key chords to the window (it is brought to the front)
//   { cmd: "front", hwnd } · { cmd: "close", hwnd } · { cmd: "capture", hwnd }  (a PNG of the window, base64)
//
// Actions use UIA patterns first (Invoke, Toggle, SelectionItem, ExpandCollapse, Value, RangeValue, Scroll): they work
// without the focus. Real input (the mouse, keys) is the fallback, sent only after the window is confirmed in front, so
// nothing is typed into another window.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Automation;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
string? line;
while ((line = Console.ReadLine()) != null)
{
    if (line.Trim().Length == 0) continue;
    JsonNode? id = null;
    JsonObject res;
    try
    {
        var req = JsonNode.Parse(line)!.AsObject();
        id = req["id"]?.DeepClone();
        res = Agent.Handle(req);
    }
    catch (Exception e) { res = new JsonObject { ["error"] = $"{e.GetType().Name}: {e.Message}" }; }
    res["id"] = id;
    Console.WriteLine(res.ToJsonString());
}

internal static class Agent
{
    /// <summary>What a window's controls are numbered: runtime id → number, and number → element of the last snapshot.</summary>
    private sealed class WindowState
    {
        public readonly Dictionary<string, int> Numbers = [];
        public readonly Dictionary<int, AutomationElement> Elements = [];
        public HashSet<IntPtr> Baseline = [];
        public List<IntPtr> Roots = [];
        public int Next = 1;
    }

    private static readonly Dictionary<IntPtr, WindowState> States = [];

    private static WindowState State(IntPtr hwnd)
    {
        if (!States.TryGetValue(hwnd, out var s))
        {
            States[hwnd] = s = new WindowState { Baseline = Win.Top().Select(w => w.Hwnd).ToHashSet() };
            s.Baseline.Remove(hwnd);
        }
        return s;
    }

    private static IntPtr Hwnd(JsonObject c) => (IntPtr)(long)c["hwnd"]!;

    public static JsonObject Handle(JsonObject c) => (string)c["cmd"]! switch
    {
        "windows" => new JsonObject { ["windows"] = new JsonArray(Win.Top().Where(w => w.Title.Length > 0).Select(w => (JsonNode)w.ToJson()).ToArray()) },
        "launch" => Launch(c),
        "front" => new JsonObject { ["front"] = Win.Foreground(Hwnd(c), Roots(Hwnd(c))) },
        "snapshot" => Snapshot(c),
        "act" => Act(c),
        "keys" => Keys(c),
        "close" => Close(c),
        "capture" => Capture(c),
        "info" => Win.IsWindow(Hwnd(c)) ? Info(Hwnd(c)) : throw new InvalidOperationException("the window is gone"),
        var x => throw new ArgumentException($"unknown cmd {x}"),
    };

    private static JsonObject Info(IntPtr hwnd)
    {
        var j = new TopWin(hwnd, Win.Pid(hwnd), Win.Title(hwnd), "").ToJson();
        j["front"] = Win.IsFront(hwnd, Roots(hwnd));
        return j;
    }

    private static JsonObject Launch(JsonObject c)
    {
        var before = Win.Top().Select(w => w.Hwnd).ToHashSet();
        var exe = (string)c["exe"]!;
        var p = Process.Start(new ProcessStartInfo(exe, (string?)c["args"] ?? "") { UseShellExecute = true });
        var pid = p?.Id ?? 0;
        var rx = c["title"] is JsonNode t && ((string?)t)?.Length > 0 ? new Regex((string)t!, RegexOptions.IgnoreCase) : null;
        var sw = Stopwatch.StartNew();
        var timeout = (int?)c["timeout"] ?? 20000;
        while (sw.ElapsedMilliseconds < timeout)
        {
            var fresh = Win.Top().Where(w => !before.Contains(w.Hwnd) && w.Title.Length > 0).ToList();
            // the program's own window first; a store app's window belongs to its frame host, so any new one after that
            var w = rx is not null ? fresh.FirstOrDefault(x => rx.IsMatch(x.Title))
                : fresh.FirstOrDefault(x => x.Pid == pid) ?? (sw.ElapsedMilliseconds > 1500 ? fresh.FirstOrDefault() : null);
            if (w != null)
            {
                Thread.Sleep((int?)c["settle"] ?? 800);  // let the app build its UI
                var j = w.ToJson();
                j["ms"] = sw.ElapsedMilliseconds;
                return j;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException(rx is null ? $"{exe} opened no new window within {timeout / 1000} s" : $"no new window matching {rx} within {timeout / 1000} s");
    }

    // The window, its owned windows (dialogs) and new popups of its process (menus, dropdowns).
    private static readonly HashSet<string> PopupClasses = ["#32768", "ComboLBox", "Xaml_WindowedPopupClass",
        "Microsoft.UI.Content.PopupWindowSiteBridge", "DropDown", "Net UI Tool Window", "WindowsForms10.Window.808.app"];

    private static List<IntPtr> Roots(IntPtr hwnd)
    {
        var pid = Win.Pid(hwnd);
        var baseline = State(hwnd).Baseline;
        var extra = new List<IntPtr>();
        foreach (var w in Win.Top())
        {
            if (w.Hwnd == hwnd) continue;
            var owned = false;
            for (var o = Win.Owner(w.Hwnd); o != IntPtr.Zero; o = Win.Owner(o)) if (o == hwnd) { owned = true; break; }
            var popup = w.Pid == pid && (PopupClasses.Contains(w.Cls) || w.Cls.StartsWith("WindowsForms10.Window", StringComparison.Ordinal)) && !baseline.Contains(w.Hwnd);
            if (owned || popup) extra.Add(w.Hwnd);
        }
        extra.Add(hwnd);  // dialogs and menus first: they are what the user is looking at
        return extra;
    }

    private static readonly HashSet<ControlType> Actionable = [ControlType.Button, ControlType.Edit, ControlType.MenuItem,
        ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.TreeItem, ControlType.Slider, ControlType.SplitButton, ControlType.Document,
        ControlType.DataItem, ControlType.HeaderItem, ControlType.Spinner, ControlType.MenuBar];
    private static readonly HashSet<ControlType> Context = [ControlType.Text, ControlType.Window, ControlType.StatusBar, ControlType.Group];

    private static JsonObject Snapshot(JsonObject c)
    {
        var hwnd = Hwnd(c);
        var offscreen = (bool?)c["offscreen"] ?? false;
        var sw = Stopwatch.StartNew();
        if (!Win.IsWindow(hwnd)) throw new InvalidOperationException("the window is gone");
        var state = State(hwnd);
        var roots = Roots(hwnd);
        state.Roots = roots;
        var cr = new CacheRequest { TreeFilter = Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
            AutomationElement.AutomationIdProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.BoundingRectangleProperty, AutomationElement.HasKeyboardFocusProperty, AutomationElement.RuntimeIdProperty,
            AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty,
            AutomationElement.IsValuePatternAvailableProperty, AutomationElement.IsSelectionItemPatternAvailableProperty,
            AutomationElement.IsExpandCollapsePatternAvailableProperty, ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty,
            TogglePattern.ToggleStateProperty, SelectionItemPattern.IsSelectedProperty, ExpandCollapsePattern.ExpandCollapseStateProperty,
            AutomationElement.IsRangeValuePatternAvailableProperty, RangeValuePattern.ValueProperty, RangeValuePattern.MinimumProperty,
            RangeValuePattern.MaximumProperty, AutomationElement.IsPasswordProperty })
            cr.Add(p);
        var view = AutomationElement.FromHandle(hwnd).Current.BoundingRectangle;
        var seen = new Queue<string>();
        var ids = new HashSet<string>();  // a popup can appear both as its own window and inside the main tree
        var items = new JsonArray();
        state.Elements.Clear();
        var total = 0;
        foreach (var r in roots)
        {
            AutomationElementCollection all;
            try
            {
                using (cr.Activate())
                {
                    var root = AutomationElement.FromHandle(r);
                    all = root.FindAll(TreeScope.Subtree, Automation.ControlViewCondition);
                }
            }
            catch (Exception) { continue; }  // a popup that closed meanwhile (ElementNotAvailable, COMException)
            foreach (AutomationElement e in all)
            {
                total++;
                var rid = Get(e, AutomationElement.RuntimeIdProperty) is int[] runtime ? string.Join('.', runtime) : null;
                if (rid is not null && !ids.Add(rid)) continue;
                string? t;
                try { t = Describe(e, seen, offscreen); } catch (Exception) { continue; }  // a control that went away
                if (t == null) continue;
                double dist = 0;
                if (r == hwnd && Get(e, AutomationElement.BoundingRectangleProperty) is System.Windows.Rect rc && !rc.IsEmpty)
                    dist = rc.Bottom < view.Top ? view.Top - rc.Bottom : rc.Top > view.Bottom ? rc.Top - view.Bottom : 0;
                var key = rid ?? $"{r}:{total}";
                if (!state.Numbers.TryGetValue(key, out var n)) state.Numbers[key] = n = state.Next++;
                state.Elements[n] = e;
                items.Add(new JsonObject { ["n"] = n, ["text"] = t, ["dist"] = dist });
            }
        }
        return new JsonObject
        {
            ["title"] = Win.Title(hwnd), ["pid"] = Win.Pid(hwnd), ["windows"] = roots.Count, ["total"] = total, ["ms"] = sw.ElapsedMilliseconds,
            ["front"] = Win.IsFront(hwnd, roots), ["elements"] = items,
        };
    }

    private static object? Get(AutomationElement e, AutomationProperty p)
    {
        var v = e.GetCachedPropertyValue(p, true);
        return v == AutomationElement.NotSupported ? null : v;
    }

    private static string? Describe(AutomationElement e, Queue<string> seen, bool offscreen)
    {
        var type = (ControlType)e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
        var name = Regex.Replace((string?)Get(e, AutomationElement.NameProperty) ?? "", @"\s+", " ").Trim();
        var aid = (string?)Get(e, AutomationElement.AutomationIdProperty) ?? "";
        var invoke = Get(e, AutomationElement.IsInvokePatternAvailableProperty) is true;
        var toggle = Get(e, AutomationElement.IsTogglePatternAvailableProperty) is true;
        var select = Get(e, AutomationElement.IsSelectionItemPatternAvailableProperty) is true;
        var expand = Get(e, AutomationElement.IsExpandCollapsePatternAvailableProperty) is true;
        var value = Get(e, AutomationElement.IsValuePatternAvailableProperty) is true;
        var keep = Actionable.Contains(type) || ((invoke || toggle || select) && name.Length > 0);
        var context = !keep && Context.Contains(type) && name.Length > 0;
        if (!keep && !context) return null;
        if (!offscreen && Get(e, AutomationElement.IsOffscreenProperty) is true && type != ControlType.Window) return null;
        if (Get(e, AutomationElement.BoundingRectangleProperty) is System.Windows.Rect rc && (rc.IsEmpty || rc.Width < 1 || rc.Height < 1)) return null;
        if (name.Length == 0 && (aid.Length == 0 || Regex.IsMatch(aid, @"^\d+$")) && !value) return null;
        if (context && seen.Contains(name)) return null;  // a label repeating its button's name
        seen.Enqueue(name);
        if (seen.Count > 4) seen.Dequeue();
        if (name.Length > 100) name = name[..100] + "…";
        var sb = new StringBuilder($"[{type.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant()}] {name}");
        if (aid.Length > 0 && !Regex.IsMatch(aid, @"^\d+$") && aid != name) sb.Append($" id=\"{aid}\"");
        if (Get(e, AutomationElement.IsPasswordProperty) is true) sb.Append(" (password)");
        else if (value && Get(e, ValuePattern.ValueProperty) is string v && v.Length > 0 && v != name)
            sb.Append($" value=\"{(v.Length > 80 ? v[..80] + "…" : v).Replace("\r", " ").Replace("\n", " ")}\"");
        if (Get(e, AutomationElement.IsRangeValuePatternAvailableProperty) is true && Get(e, RangeValuePattern.ValueProperty) is double rv)
            sb.Append(CultureInfo.InvariantCulture, $" position={rv:0.##} of {Get(e, RangeValuePattern.MinimumProperty):0.##}-{Get(e, RangeValuePattern.MaximumProperty):0.##}");
        if (toggle && Get(e, TogglePattern.ToggleStateProperty) is ToggleState ts) sb.Append(ts == ToggleState.On ? " (on)" : ts == ToggleState.Off ? " (off)" : "");
        if (select && Get(e, SelectionItemPattern.IsSelectedProperty) is true) sb.Append(" (selected)");
        if (expand && Get(e, ExpandCollapsePattern.ExpandCollapseStateProperty) is ExpandCollapseState es && es != ExpandCollapseState.LeafNode)
            sb.Append(es == ExpandCollapseState.Expanded ? " (expanded)" : " (collapsed)");
        if (Get(e, AutomationElement.HasKeyboardFocusProperty) is true) sb.Append(" (focused)");
        if (Get(e, AutomationElement.IsEnabledProperty) is false) sb.Append(" (disabled)");
        return sb.ToString();
    }

    private static JsonObject Act(JsonObject c)
    {
        var hwnd = Hwnd(c);
        var action = (string)c["action"]!;
        var text = (string?)c["text"] ?? "";
        var n = (int)c["n"]!;
        var state = State(hwnd);
        if (!state.Elements.TryGetValue(n, out var e))
            throw new ArgumentOutOfRangeException("n", state.Numbers.ContainsValue(n) ? $"control {n} is not on the window now (take a snapshot)" : $"no control {n}");
        var sw = Stopwatch.StartNew();
        var how = action switch
        {
            "click" => Timed(() => Click(e, hwnd)),
            "double" => Timed(() => Open(e, hwnd)),
            "right" => Timed(() => Mouse(e, hwnd, 1, right: true)),
            "hover" => Timed(() => Hover(e, hwnd)),
            "type" => Timed(() => Type(e, hwnd, text)),
            "toggle" => Timed(() => Try<TogglePattern>(e, TogglePattern.Pattern, out var tg) ? Do(tg.Toggle, "toggle") : Click(e, hwnd)),
            "expand" => Timed(() => Try<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, out var ec) ? Do(ec.Expand, "expand") : "it does not expand"),
            "collapse" => Timed(() => Try<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, out var ec) ? Do(ec.Collapse, "collapse") : "it does not collapse"),
            "select" => Timed(() => Try<SelectionItemPattern>(e, SelectionItemPattern.Pattern, out var si) ? Do(si.Select, "select") : Click(e, hwnd)),
            "scroll" => Timed(() => Scroll(e, hwnd, (string?)c["direction"] ?? "down")),
            "focus" => Timed(() => { e.SetFocus(); return "focus"; }),
            "read" => Read(e),
            _ => throw new ArgumentException($"unknown action {action}"),
        };
        return new JsonObject { ["how"] = how, ["ms"] = sw.ElapsedMilliseconds };
    }

    private static string Do(Action a, string how) { a(); return how; }

    // Some pattern calls block until a modal dialog they open closes: stop waiting after 3 s (the dialog is then in the next snapshot).
    private static string Timed(Func<string> f)
    {
        var t = Task.Run(f);
        return t.Wait(3000) ? t.Result : "pattern call still running (a modal dialog?)";
    }

    private static bool Try<T>(AutomationElement e, AutomationPattern p, out T pat) where T : class
    {
        pat = null!;
        if (!e.TryGetCurrentPattern(p, out var o)) return false;
        pat = (T)o;
        return true;
    }

    private static string Click(AutomationElement e, IntPtr hwnd)
    {
        var type = e.Current.ControlType;
        if ((type == ControlType.MenuItem || type == ControlType.ComboBox || type == ControlType.SplitButton) && Try<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, out var ec)
            && ec.Current.ExpandCollapseState != ExpandCollapseState.LeafNode)
        {
            if (ec.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) { ec.Expand(); return "expand"; }
            if (type == ControlType.ComboBox) { ec.Collapse(); return "collapse"; }
        }
        if (type == ControlType.ListItem && InComboList(e)) return Mouse(e, hwnd, 1);
        var selectable = type == ControlType.ListItem || type == ControlType.TreeItem || type == ControlType.TabItem || type == ControlType.DataItem || type == ControlType.RadioButton;
        if (selectable && Try<SelectionItemPattern>(e, SelectionItemPattern.Pattern, out var si)) { si.Select(); return "select"; }
        // a classic Win32 push button: its click is posted, not invoked. Invoke waits for the click's handler, and a
        // handler that opens a modal dialog holds it — and every other UI Automation call into the app — until the
        // dialog closes
        if (type == ControlType.Button && e.Current.NativeWindowHandle is var native and not 0
            && Win.Class((IntPtr)native).Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
        {
            Win.PostClick((IntPtr)native);
            return "click (posted)";
        }
        if (Try<InvokePattern>(e, InvokePattern.Pattern, out var inv)) { inv.Invoke(); return "invoke"; }
        if (Try<TogglePattern>(e, TogglePattern.Pattern, out var tg)) { tg.Toggle(); return "toggle"; }
        if (Try<SelectionItemPattern>(e, SelectionItemPattern.Pattern, out si)) { si.Select(); return "select"; }
        if (Try<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, out ec))
        {
            if (ec.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) { ec.Expand(); return "expand"; }
            ec.Collapse();
            return "collapse";
        }
        return Mouse(e, hwnd, 1);
    }

    // An item of a Win32 combo box's dropdown (ComboLBox): selecting it through UIA does not tell the combo box.
    private static bool InComboList(AutomationElement e)
    {
        var w = TreeWalker.ControlViewWalker;
        for (var p = w.GetParent(e); p != null && p != AutomationElement.RootElement; p = w.GetParent(p))
            if (p.Current.ClassName == "ComboLBox") return true;
        return false;
    }

    private static string Open(AutomationElement e, IntPtr hwnd) => Mouse(e, hwnd, 2);

    private static string Type(AutomationElement e, IntPtr hwnd, string text)
    {
        if (Try<RangeValuePattern>(e, RangeValuePattern.Pattern, out var rv) && !rv.Current.IsReadOnly
            && Regex.Match(text, @"-?\d+(\.\d+)?") is { Success: true } num)
        {
            var v = Math.Clamp(double.Parse(num.Value, CultureInfo.InvariantCulture), rv.Current.Minimum, rv.Current.Maximum);
            rv.SetValue(v);
            return $"rangevalue {v.ToString(CultureInfo.InvariantCulture)}";
        }
        var slider = e.Current.ControlType == ControlType.Slider;
        if (Try<ValuePattern>(e, ValuePattern.Pattern, out var vp) && !vp.Current.IsReadOnly)
        {
            try
            {
                vp.SetValue(text);
                // some controls take the call and keep their value (a WinForms track bar): the slider then goes by keys
                if (!slider || vp.Current.Value == text) return "setvalue";
            }
            catch (InvalidOperationException) { }
        }
        try { e.SetFocus(); } catch (Exception) { }
        if (slider && Regex.Match(text, @"^\s*\d+\s*$").Success)
        {
            if (!Win.Foreground(hwnd, State(hwnd).Roots)) return "not set: could not bring the window to the front";
            var steps = Math.Min(int.Parse(text.Trim(), CultureInfo.InvariantCulture), 1000);
            try { e.SetFocus(); } catch (Exception) { }
            Win.SendKeys("Home" + string.Concat(Enumerable.Repeat(" Right", steps)));
            return "keys (slider: Home, then Right × " + steps.ToString(CultureInfo.InvariantCulture) + ")";
        }
        if (!Win.Foreground(hwnd, State(hwnd).Roots)) return "not typed: could not bring the window to the front";
        Win.SendKeys("Ctrl+A");
        Win.SendText(text);
        return "keys (typed)";
    }

    private static string Mouse(AutomationElement e, IntPtr hwnd, int clicks, bool right = false)
    {
        if (!Win.Foreground(hwnd, State(hwnd).Roots)) return "not clicked: could not bring the window to the front";
        var r = e.Current.BoundingRectangle;
        var pt = e.TryGetClickablePoint(out var cp) ? cp : new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        Win.Click((int)pt.X, (int)pt.Y, clicks, right);
        return right ? "mouse right-click" : clicks == 2 ? "mouse double-click" : "mouse click";
    }

    private static string Hover(AutomationElement e, IntPtr hwnd)
    {
        var r = e.Current.BoundingRectangle;
        var pt = e.TryGetClickablePoint(out var cp) ? cp : new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        Win.Move((int)pt.X, (int)pt.Y);
        return "mouse moved";
    }

    private static string Scroll(AutomationElement e, IntPtr hwnd, string direction)
    {
        // the control's own scroll pattern, or its nearest scrolling ancestor's
        var w = TreeWalker.ControlViewWalker;
        for (var p = e; p != null && p != AutomationElement.RootElement; p = w.GetParent(p))
        {
            if (!Try<ScrollPattern>(p, ScrollPattern.Pattern, out var sp)) continue;
            var (h, v) = direction switch
            {
                "up" => (ScrollAmount.NoAmount, ScrollAmount.LargeDecrement),
                "left" => (ScrollAmount.LargeDecrement, ScrollAmount.NoAmount),
                "right" => (ScrollAmount.LargeIncrement, ScrollAmount.NoAmount),
                _ => (ScrollAmount.NoAmount, ScrollAmount.LargeIncrement),
            };
            if ((v != ScrollAmount.NoAmount && !sp.Current.VerticallyScrollable) || (h != ScrollAmount.NoAmount && !sp.Current.HorizontallyScrollable)) continue;
            sp.Scroll(h, v);
            return "scroll";
        }
        if (Try<ScrollItemPattern>(e, ScrollItemPattern.Pattern, out var si)) { si.ScrollIntoView(); return "scrolled into view"; }
        if (!Win.Foreground(hwnd, State(hwnd).Roots)) return "not scrolled: could not bring the window to the front";
        var r = e.Current.BoundingRectangle;
        Win.Move((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Win.Wheel(direction == "up" ? 3 : -3);
        return "mouse wheel";
    }

    private static string Read(AutomationElement e)
    {
        if (Try<TextPattern>(e, TextPattern.Pattern, out var tp)) return tp.DocumentRange.GetText(-1);
        if (Try<ValuePattern>(e, ValuePattern.Pattern, out var vp)) return vp.Current.Value;
        if (Try<RangeValuePattern>(e, RangeValuePattern.Pattern, out var rv)) return rv.Current.Value.ToString(CultureInfo.InvariantCulture);
        return e.Current.Name;
    }

    private static JsonObject Keys(JsonObject c)
    {
        var hwnd = Hwnd(c);
        if (!Win.Foreground(hwnd, Roots(hwnd))) return new JsonObject { ["how"] = "not sent: could not bring the window to the front" };
        var keys = (string?)c["keys"] ?? "";
        if ((bool?)c["text"] == true) Win.SendText(keys);
        else Win.SendKeys(keys);
        return new JsonObject { ["how"] = "keys" };
    }

    private static JsonObject Close(JsonObject c)
    {
        var hwnd = Hwnd(c);
        States.Remove(hwnd);
        if (!Win.IsWindow(hwnd)) return new JsonObject { ["how"] = "already gone" };
        var el = AutomationElement.FromHandle(hwnd);
        if (Try<WindowPattern>(el, WindowPattern.Pattern, out var wp)) { Timed(() => { wp.Close(); return ""; }); return new JsonObject { ["how"] = "WindowPattern.Close" }; }
        Win.PostClose(hwnd);
        return new JsonObject { ["how"] = "WM_CLOSE" };
    }

    private static JsonObject Capture(JsonObject c)
    {
        var hwnd = Hwnd(c);
        if (!Win.IsWindow(hwnd)) throw new InvalidOperationException("the window is gone");
        var (png, w, h) = Win.Capture(hwnd);
        return new JsonObject { ["data"] = Convert.ToBase64String(png), ["width"] = w, ["height"] = h, ["title"] = Win.Title(hwnd) };
    }
}

internal sealed record TopWin(IntPtr Hwnd, int Pid, string Title, string Cls)
{
    public JsonObject ToJson()
    {
        string process = "";
        try { process = Process.GetProcessById(Pid).ProcessName; } catch (Exception) { }
        return new() { ["hwnd"] = (long)Hwnd, ["pid"] = Pid, ["title"] = Title, ["cls"] = Cls, ["process"] = process };
    }
}

internal static class Win
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    public static List<TopWin> Top()
    {
        var list = new List<TopWin>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            // tool windows (floating palettes, the taskbar's helpers) are not what a user calls a window
            if ((GetWindowLong(h, -20) & 0x80) != 0 && Owner(h) == IntPtr.Zero && Title(h).Length == 0) return true;
            list.Add(new TopWin(h, Pid(h), Title(h), Cls(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static int Pid(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return pid; }
    public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    private static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static string Class(IntPtr h) => Cls(h);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);

    /// <summary>
    /// The button's click, posted: its parent gets the WM_COMMAND / BN_CLICKED a click sends (a dialog acts on it, WinForms
    /// reflects it to the button), so nobody waits for what the click does and the window needs no activation (a posted
    /// BM_CLICK can be dropped by a window that is not active). A button without a parent gets BM_CLICK.
    /// </summary>
    public static void PostClick(IntPtr h)
    {
        var parent = GetParent(h);
        if (parent == IntPtr.Zero) { PostMessage(h, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero); return; }
        PostMessage(parent, 0x0111 /* WM_COMMAND */, (IntPtr)(GetDlgCtrlID(h) & 0xFFFF /* BN_CLICKED = 0 in the high word */), h);
    }
    public static IntPtr Owner(IntPtr h) => GetWindow(h, 4 /* GW_OWNER */);
    public static void PostClose(IntPtr h) => PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Whether the foreground window is the target or one of its roots.</summary>
    public static bool IsFront(IntPtr hwnd, List<IntPtr> roots)
    {
        var fg = GetForegroundWindow();
        var root = GetAncestor(fg, 3 /* GA_ROOTOWNER */);
        return roots.Contains(fg) || roots.Contains(root) || fg == hwnd || root == hwnd;
    }

    /// <summary>Bring the target to the front and check that the foreground window is the target or one of its roots.</summary>
    public static bool Foreground(IntPtr hwnd, List<IntPtr> roots)
    {
        if (roots.Count == 0) roots = [hwnd];
        if (IsFront(hwnd, roots)) return true;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(me, fgThread, false);
        Thread.Sleep(150);
        return IsFront(hwnd, roots);
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    private static void Send(List<INPUT> l) => SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf<INPUT>());

    public static void SendText(string text)
    {
        var l = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\n') { l.Add(Key(0x0D, 0, 0)); l.Add(Key(0x0D, 0, 2)); continue; }
            if (ch == '\r') continue;
            l.Add(Key(0, ch, 4 /* UNICODE */));
            l.Add(Key(0, ch, 4 | 2 /* KEYUP */));
        }
        Send(l);
    }

    private static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["apps"] = 0x5D, ["menu"] = 0x5D,
    };

    // "Ctrl+Shift+S", "Enter", "F2", "a"; several chords separated by spaces.
    public static void SendKeys(string keys)
    {
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
            var vks = new List<ushort>();
            foreach (var p in parts)
            {
                if (Vk.TryGetValue(p, out var v)) vks.Add(v);
                else if (Regex.IsMatch(p, @"^F([1-9]|1[0-2])$", RegexOptions.IgnoreCase)) vks.Add((ushort)(0x6F + int.Parse(p[1..], CultureInfo.InvariantCulture)));
                else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vks.Add((ushort)char.ToUpperInvariant(p[0]));
                else throw new ArgumentException($"unknown key {p}");
            }
            var l = new List<INPUT>();
            foreach (var v in vks) l.Add(Key(v, 0, 0));
            for (var k = vks.Count - 1; k >= 0; k--) l.Add(Key(vks[k], 0, 2));
            Send(l);
            Thread.Sleep(15);
        }
    }

    public static void Move(int x, int y) => SetCursorPos(x, y);

    public static void Click(int x, int y, int clicks, bool right = false)
    {
        SetCursorPos(x, y);
        var (down, up) = right ? (0x0008u, 0x0010u) : (0x0002u, 0x0004u);
        var l = new List<INPUT>();
        for (var k = 0; k < clicks; k++)
        {
            l.Add(new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = down } } });
            l.Add(new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = up } } });
        }
        Send(l);
    }

    public static void Wheel(int notches) =>
        Send([new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)(notches * 120)) } } }]);

    /// <summary>The window as it draws itself (PrintWindow with full content, so it works behind other windows), as PNG.</summary>
    public static (byte[] Png, int Width, int Height) Capture(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        var (w, h) = (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        try
        {
            PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */);
            SelectObject(dc, old);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (ms.ToArray(), w, h);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
