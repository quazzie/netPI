using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Web;

/// <summary>A chat's tab: its DevTools session, the last snapshot's controls, and the actions on them.</summary>
internal sealed class BrowserTab
{
    private static readonly Dictionary<string, (string Key, string Code, int Vk, string? Text)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = ("Enter", "Enter", 13, "\r"), ["return"] = ("Enter", "Enter", 13, "\r"), ["tab"] = ("Tab", "Tab", 9, null),
        ["esc"] = ("Escape", "Escape", 27, null), ["escape"] = ("Escape", "Escape", 27, null), ["space"] = (" ", "Space", 32, " "),
        ["backspace"] = ("Backspace", "Backspace", 8, null), ["delete"] = ("Delete", "Delete", 46, null), ["del"] = ("Delete", "Delete", 46, null),
        ["home"] = ("Home", "Home", 36, null), ["end"] = ("End", "End", 35, null), ["up"] = ("ArrowUp", "ArrowUp", 38, null),
        ["down"] = ("ArrowDown", "ArrowDown", 40, null), ["left"] = ("ArrowLeft", "ArrowLeft", 37, null), ["right"] = ("ArrowRight", "ArrowRight", 39, null),
        ["pageup"] = ("PageUp", "PageUp", 33, null), ["pagedown"] = ("PageDown", "PageDown", 34, null),
    };

    private static readonly Dictionary<string, int> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alt"] = 1, ["ctrl"] = 2, ["control"] = 2, ["meta"] = 4, ["cmd"] = 4, ["win"] = 4, ["shift"] = 8,
    };

    private readonly CdpConnection _cdp;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly HashSet<int> _openSelects = [];  // <select> boxes clicked open: their options are listed
    private List<BrowserControl> _controls = [];
    private string? _mainFrame;
    private volatile bool _loading;

    public string TargetId { get; private set; }
    public string SessionId { get; private set; } = "";
    /// <summary>A tab in the user's Chrome (not the hidden browser): buying and the like are refused there.</summary>
    public bool Attached { get; }
    public bool IsOpen => _cdp.IsOpen && SessionId.Length > 0;

    private BrowserTab(CdpConnection cdp, string targetId, bool attached)
    {
        _cdp = cdp;
        TargetId = targetId;
        Attached = attached;
    }

    public static async Task<BrowserTab> AttachAsync(CdpConnection cdp, string targetId, bool attached, CancellationToken ct)
    {
        var tab = new BrowserTab(cdp, targetId, attached);
        await tab.AttachToAsync(targetId, ct).ConfigureAwait(false);
        return tab;
    }

    private async Task AttachToAsync(string targetId, CancellationToken ct)
    {
        var r = await _cdp.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, null, ct).ConfigureAwait(false);
        TargetId = targetId;
        SessionId = r.GetProperty("sessionId").GetString()!;
        await Task.WhenAll(
            Send("Page.enable", null, ct),
            Send("Accessibility.enable", null, ct),
            // a page in a background tab or window behaves as if it had the focus
            Send("Emulation.setFocusEmulationEnabled", new { enabled = true }, ct)).ConfigureAwait(false);
        _mainFrame = (await Send("Page.getFrameTree", null, ct).ConfigureAwait(false)).GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        _openSelects.Clear();
    }

    public async Task FollowAsync(string targetId)
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try { await AttachToAsync(targetId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        finally { _busy.Release(); }
    }

    public void OnEvent(string method, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("frameId", out var f) || f.GetString() != _mainFrame) return;
        if (method == "Page.frameStartedLoading") _loading = true;
        else if (method == "Page.frameStoppedLoading") _loading = false;
    }

    public void Gone() => SessionId = "";

    /// <summary>Detaches from the tab and brings it forward in its window; the tab stays as it is.</summary>
    public async Task<(string Url, string Title)> LeaveAsync(CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await InfoAsync(ct).ConfigureAwait(false);
            try { await _cdp.SendAsync("Target.activateTarget", new { targetId = TargetId }, null, ct).ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await _cdp.SendAsync("Target.detachFromTarget", new { sessionId = SessionId }, null, ct).ConfigureAwait(false); } catch (InvalidOperationException) { }
            SessionId = "";
            return info;
        }
        finally { _busy.Release(); }
    }

    // In the user's Chrome the agent stops before a purchase, a payment or a booking, and does not accept every cookie.
    private static readonly Regex Checkout = new(
        @"\b(buy|book|booking|pay|payment|purchase|check ?out|order now|place (the |your )?order|confirm (and |& )?(pay|order|purchase|booking)|complete (the )?(purchase|order|booking|payment)|subscribe|donate)\b|\baccept all\b|\ballow all\b",
        RegexOptions.IgnoreCase);

    public async Task CloseAsync()
    {
        try { await _cdp.SendAsync("Target.closeTarget", new { targetId = TargetId }, null, CancellationToken.None, timeoutSeconds: 5).ConfigureAwait(false); }
        catch (Exception) { }
        SessionId = "";
    }

    private Task<JsonElement> Send(string method, object? parameters, CancellationToken ct) => _cdp.SendAsync(method, parameters, SessionId, ct);

    public async Task<ToolResult> RunAsync(string action, JsonElement args, int maxControls, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = _controls;
            var a = new ToolArgs(args);
            string result;
            switch (action)
            {
                case "open":
                    var url = a.Str("url", "href", "page")?.Trim();
                    if (string.IsNullOrEmpty(url)) return ToolResult.Error("open needs a url.");
                    if (!url.Contains("://", StringComparison.Ordinal))
                        url = (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + url;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
                        return ToolResult.Error($"Not a URL the browser can open: {url}");
                    _loading = true;
                    var nav = await Send("Page.navigate", new { url = uri.ToString() }, ct).ConfigureAwait(false);
                    if (nav.TryGetProperty("errorText", out var et) && et.GetString() is { Length: > 0 } navError)
                    {
                        _loading = false;
                        return ToolResult.Error($"Could not open {uri}: {navError}", new { action, url = uri.ToString(), error = navError });
                    }
                    before = [];
                    result = $"Opened {uri}.";
                    break;
                case "snapshot":
                    result = "";
                    before = [];
                    break;
                case "find":
                    var text = a.Str("text", "query", "q")?.Trim();
                    if (string.IsNullOrEmpty(text)) return ToolResult.Error("find needs a text.");
                    await WaitLoadAsync(ct).ConfigureAwait(false);
                    var page = await SnapshotAsync(ct).ConfigureAwait(false);
                    return AxSnapshot.Found(page, text);
                case "click":
                case "type":
                    var n = a.Int("n", "number", "index", "i", "ref", "element");
                    if (n is null || n < 1 || n > _controls.Count)
                        return ToolResult.Error(_controls.Count == 0 ? "Take a snapshot first: numbers refer to the last list of controls." : $"No control {n?.ToString(CultureInfo.InvariantCulture) ?? "given"}: the numbers are 1..{_controls.Count} from the last list.");
                    var c = _controls[n.Value - 1];
                    if (Attached && action == "click" && c.Role is "button" or "link" or "menuitem" or "StaticText" && Checkout.IsMatch(c.Name))
                        return ToolResult.Error($"Refused in the user's Chrome: [{n}] {c.Text} looks like buying, paying, booking or accepting all cookies. Stop here: use action leave to hand the tab to the user and tell them what to press.",
                            new { action, refused = c.Text });
                    result = action == "click"
                        ? await ClickAsync(c, ct).ConfigureAwait(false)
                        : await TypeAsync(c, a.Str("text", "value") ?? "", ct).ConfigureAwait(false);
                    result = $"{result} [{n}] {c.Text}";
                    break;
                case "key":
                    var keys = a.Str("keys", "key", "text")?.Trim();
                    if (string.IsNullOrEmpty(keys)) return ToolResult.Error("key needs keys, e.g. Enter or Ctrl+A.");
                    await KeysAsync(keys, ct).ConfigureAwait(false);
                    result = $"Pressed {keys}.";
                    break;
                case "scroll":
                    var up = (a.Str("direction", "dir") ?? "down").StartsWith("up", StringComparison.OrdinalIgnoreCase);
                    var vp = (await Send("Page.getLayoutMetrics", null, ct).ConfigureAwait(false)).GetProperty("cssVisualViewport");
                    var (w, h) = (vp.GetProperty("clientWidth").GetDouble(), vp.GetProperty("clientHeight").GetDouble());
                    await Send("Input.dispatchMouseEvent", new { type = "mouseWheel", x = w / 2, y = h / 2, deltaX = 0, deltaY = (up ? -0.8 : 0.8) * h }, ct).ConfigureAwait(false);
                    result = $"Scrolled {(up ? "up" : "down")}.";
                    break;
                case "back":
                    var hist = await Send("Page.getNavigationHistory", null, ct).ConfigureAwait(false);
                    var index = hist.GetProperty("currentIndex").GetInt32();
                    if (index <= 0) return ToolResult.Error("There is no earlier page in this tab.");
                    _loading = true;
                    await Send("Page.navigateToHistoryEntry", new { entryId = hist.GetProperty("entries")[index - 1].GetProperty("id").GetInt32() }, ct).ConfigureAwait(false);
                    result = "Went back.";
                    break;
                case "screenshot":
                    await WaitLoadAsync(ct).ConfigureAwait(false);
                    var shot = await Send("Page.captureScreenshot", new { format = "png" }, ct).ConfigureAwait(false);
                    var (sUrl, sTitle) = await InfoAsync(ct).ConfigureAwait(false);
                    return new ToolResult
                    {
                        Content = $"Screenshot of {sTitle} — {sUrl}.",
                        Images = [new ImagePart { MediaType = "image/png", Data = shot.GetProperty("data").GetString() ?? "" }],
                        Details = new { action, url = sUrl, title = sTitle },
                    };
                default:
                    return ToolResult.Error($"Unknown action \"{action}\": use open, snapshot, click, type, key, scroll, find, back, screenshot or close.");
            }
            if (action is not ("open" or "snapshot")) await Task.Delay(150, ct).ConfigureAwait(false);
            if (action == "open")
            {
                // until the new document has replaced the tab's about:blank (its "complete" state would pass the wait)
                for (var k = 0; k < 150 && await EvalAsync("location.href", ct).ConfigureAwait(false) is null or "about:blank"; k++) await Task.Delay(100, ct).ConfigureAwait(false);
            }
            await WaitLoadAsync(ct).ConfigureAwait(false);
            if (action is not ("open" or "snapshot")) await Task.Delay(250, ct).ConfigureAwait(false);  // scripts updating the page
            var snap = await SnapshotAsync(ct).ConfigureAwait(false);
            if (before.Count > 0) result += " " + AxSnapshot.Effect(before, snap.Controls);
            return AxSnapshot.Render(action, result, snap, maxControls);
        }
        finally { _busy.Release(); }
    }

    private async Task<string?> EvalAsync(string expression, CancellationToken ct)
    {
        try
        {
            var r = await Send("Runtime.evaluate", new { expression, returnByValue = true }, ct).ConfigureAwait(false);
            return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (InvalidOperationException) { return null; }  // a navigation replaced the context
    }

    private async Task WaitLoadAsync(CancellationToken ct)
    {
        for (var until = DateTime.UtcNow.AddSeconds(15); DateTime.UtcNow < until;)
        {
            if (!_loading && await EvalAsync("document.readyState", ct).ConfigureAwait(false) == "complete") return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        _loading = false;
    }

    private async Task<(string Url, string Title)> InfoAsync(CancellationToken ct)
    {
        var info = (await _cdp.SendAsync("Target.getTargetInfo", new { targetId = TargetId }, null, ct).ConfigureAwait(false)).GetProperty("targetInfo");
        return (info.GetProperty("url").GetString() ?? "", info.GetProperty("title").GetString() ?? "");
    }

    private async Task<AxSnapshot.Snapshot> SnapshotAsync(CancellationToken ct)
    {
        // until this snapshot succeeds, the numbers of the last one would point at a page that is gone
        _controls = [];
        var axTask = Send("Accessibility.getFullAXTree", null, ct);
        var domTask = Send("DOMSnapshot.captureSnapshot", new { computedStyles = Array.Empty<string>() }, ct);
        var metricsTask = Send("Page.getLayoutMetrics", null, ct);
        var infoTask = InfoAsync(ct);
        await Task.WhenAll(axTask, domTask, metricsTask, infoTask).ConfigureAwait(false);
        var (url, title) = infoTask.Result;
        var snap = AxSnapshot.Parse(axTask.Result, domTask.Result, metricsTask.Result, url, title, _openSelects);
        _controls = snap.Controls;
        return snap;
    }

    private async Task<JsonElement?> CallOnAsync(int backendNodeId, string function, CancellationToken ct, params object[] arguments)
    {
        var obj = await Send("DOM.resolveNode", new { backendNodeId }, ct).ConfigureAwait(false);
        var objectId = obj.GetProperty("object").GetProperty("objectId").GetString();
        var r = await Send("Runtime.callFunctionOn", new { objectId, functionDeclaration = function, arguments = arguments.Select(value => new { value }).ToArray(), returnByValue = true }, ct).ConfigureAwait(false);
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) ? v.Clone() : null;
    }

    private const string ChooseOption = """
        function(t) {
          t = t.trim().toLowerCase();
          const o = [...this.options].find(o => o.text.trim().toLowerCase() === t) ?? [...this.options].find(o => o.text.trim().toLowerCase().includes(t));
          if (!o) return null;
          this.value = o.value;
          this.dispatchEvent(new Event("input", { bubbles: true }));
          this.dispatchEvent(new Event("change", { bubbles: true }));
          return o.text.trim();
        }
        """;

    private async Task<string> ClickAsync(BrowserControl c, CancellationToken ct)
    {
        if (c.Select is { } select)
        {
            if (!_openSelects.Remove(select)) _openSelects.Add(select);
            return _openSelects.Contains(select) ? "Opened the list (its options are listed now):" : "Closed the list:";
        }
        if (c.OptionOf is { } of)
        {
            var chosen = await CallOnAsync(of, ChooseOption, ct, c.Name).ConfigureAwait(false);
            _openSelects.Remove(of);
            return chosen is { ValueKind: JsonValueKind.String } ? "Chose" : "Could not choose";
        }
        // a text node (a label, or the text of a clickable div) is scrolled and clicked through its element
        try { await Send("DOM.scrollIntoViewIfNeeded", new { backendNodeId = c.Backend }, ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { await CallOnAsync(c.Backend, "function() { (this.nodeType === 3 ? this.parentElement : this).scrollIntoView({ block: 'center' }); }", ct).ConfigureAwait(false); }
        double[]? quad = null;
        try
        {
            var quads = (await Send("DOM.getContentQuads", new { backendNodeId = c.Backend }, ct).ConfigureAwait(false)).GetProperty("quads");
            quad = quads.EnumerateArray().Select(q => q.EnumerateArray().Select(x => x.GetDouble()).ToArray())
                .FirstOrDefault(q => Math.Abs((q[4] - q[0]) * (q[5] - q[1])) > 0);
        }
        catch (InvalidOperationException) { }
        if (quad is null)
        {
            await CallOnAsync(c.Backend, "function() { (this.nodeType === 3 ? this.parentElement : this).click(); }", ct).ConfigureAwait(false);
            return "Clicked";
        }
        var x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4;
        var y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4;
        await Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct).ConfigureAwait(false);
        await Send("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct).ConfigureAwait(false);
        await Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct).ConfigureAwait(false);
        return "Clicked";
    }

    private async Task<string> TypeAsync(BrowserControl c, string text, CancellationToken ct)
    {
        if (c.Password) return "Not typed: a password field (the user types passwords themselves):";
        if (c.Select is { } select)
        {
            var chosen = await CallOnAsync(select, ChooseOption, ct, text).ConfigureAwait(false);
            _openSelects.Remove(select);
            return chosen is { ValueKind: JsonValueKind.String } v ? $"Chose \"{v.GetString()}\" in" : $"No option \"{text}\" in";
        }
        if (c.Role == "slider")
        {
            var number = Regex.Match(text, @"-?\d+(\.\d+)?");
            if (!number.Success) return "Not typed: a slider needs a number:";
            await CallOnAsync(c.Backend, "function(v) { this.value = v; this.dispatchEvent(new Event('input', { bubbles: true })); this.dispatchEvent(new Event('change', { bubbles: true })); }", ct, number.Value).ConfigureAwait(false);
            return $"Set {number.Value} in";
        }
        await Send("DOM.focus", new { backendNodeId = c.Backend }, ct).ConfigureAwait(false);
        await CallOnAsync(c.Backend, "function() { if (this.select) this.select(); else { const r = document.createRange(); r.selectNodeContents(this); const s = getSelection(); s.removeAllRanges(); s.addRange(r); } }", ct).ConfigureAwait(false);
        if (text.Length > 0) await Send("Input.insertText", new { text }, ct).ConfigureAwait(false);
        else await KeysAsync("Delete", ct).ConfigureAwait(false);
        return $"Typed \"{text}\" in";
    }

    private async Task KeysAsync(string spec, CancellationToken ct)
    {
        foreach (var chord in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
            var modifiers = 0;
            (string Key, string Code, int Vk, string? Text)? key = null;
            foreach (var p in parts)
            {
                if (parts.Length > 1 && Modifiers.TryGetValue(p, out var m)) modifiers |= m;
                else if (Keys.TryGetValue(p, out var k)) key = k;
                else if (Regex.IsMatch(p, "^[Ff]([1-9]|1[0-2])$")) key = (p.ToUpperInvariant(), p.ToUpperInvariant(), 111 + int.Parse(p[1..], CultureInfo.InvariantCulture), null);
                else if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
                {
                    var up = char.ToUpperInvariant(p[0]);
                    var ch = (modifiers & 8) != 0 ? up.ToString() : char.ToLowerInvariant(p[0]).ToString();
                    key = (ch, char.IsDigit(p[0]) ? $"Digit{p}" : $"Key{up}", up, ch);
                }
                else throw new InvalidOperationException($"unknown key \"{p}\" (use names like Enter, Escape, Tab, PageDown, Ctrl+A)");
            }
            if (key is not { } kk) throw new InvalidOperationException($"no key in \"{chord}\"");
            var text = (modifiers & 3) != 0 ? null : kk.Text;
            await Send("Input.dispatchKeyEvent", new { type = text is null ? "rawKeyDown" : "keyDown", key = kk.Key, code = kk.Code, windowsVirtualKeyCode = kk.Vk, nativeVirtualKeyCode = kk.Vk, modifiers, text, unmodifiedText = text }, ct).ConfigureAwait(false);
            await Send("Input.dispatchKeyEvent", new { type = "keyUp", key = kk.Key, code = kk.Code, windowsVirtualKeyCode = kk.Vk, nativeVirtualKeyCode = kk.Vk, modifiers }, ct).ConfigureAwait(false);
        }
    }
}
