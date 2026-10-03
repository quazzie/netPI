using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Windows;

/// <summary>
/// <c>windows</c>: an agent reads and uses the windows of native Windows apps through UI Automation. Each chat works on
/// one window at a time (with its menus, popups and dialogs); a window is a numbered list of controls, the numbers stay
/// with a control while it lives, and a result after an action lists only what changed. Actions go through UIA patterns
/// (invoke, toggle, select, expand, set value, scroll), which need neither the focus nor the user's mouse; real clicks
/// and keys are the fallback and bring the window to the front first. Every step can be journaled
/// (<c>windows.journal</c>). See docs/TOOLS.md.
/// </summary>
internal sealed class WindowsTool(IPluginContext ctx, WindowsAgent agent) : IAgentTool
{
    private sealed class ChatWindow(long hwnd)
    {
        public long Hwnd { get; } = hwnd;
        public List<Item> Last { get; set; } = [];
        public HashSet<int> Seen { get; } = [];
        public string Title { get; set; } = "";
    }

    private sealed record Item(int N, string Text, double Dist);

    private readonly ConcurrentDictionary<string, ChatWindow> _windows = new();
    private readonly SemaphoreSlim _journal = new(1, 1);

    private static readonly string[] StepActions = ["click", "hover", "type", "key", "toggle", "expand", "collapse", "select", "scroll", "focus", "wait"];

    public ToolDefinition Definition { get; } = new()
    {
        Name = "windows",
        Label = "Windows",
        Category = "computer",
        SummaryArg = "action",
        Description =
            "Read and use the windows of native Windows apps (UI Automation): list, open or use a window, then act on its " +
            "controls by number ([12] [button] Save). Numbers stay valid while a control lives; after an action only the changes are listed.",
        Help =
            "Actions: list (the open windows); open {app, args?, title?} (start a program — a name like notepad or calc, a path, " +
            "or a URI like ms-settings: — and use its new window); use {window} (a window from list: its number or part of its " +
            "title); snapshot {all?, offscreen?}; click {n, button?: left|right, clicks?: 2}; hover {n}; type {n, text, submit?} " +
            "(sets the text; submit: then Enter); key {keys} (Enter, Ctrl+S, Alt+F4, F2…) or key {text} (types text into the " +
            "focused control); toggle|expand|collapse|select {n}; scroll {n, direction?: down|up|left|right}; read {n} (the whole " +
            "text of a control, e.g. a document); find {text}; wait {text?, gone?, seconds?, timeout?}; focus (bring the window " +
            "to the front); screenshot; close (closes the window); steps {steps: [{action, …}, …]} (several in one call, one " +
            "result at the end).",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("list", "open", "use", "snapshot", "click", "hover", "type", "key", "toggle", "expand", "collapse",
                        "select", "scroll", "read", "find", "wait", "focus", "screenshot", "close", "steps"),
                },
                ["app"] = new JsonObject { ["type"] = "string" },
                ["args"] = new JsonObject { ["type"] = "string" },
                ["window"] = new JsonObject { ["type"] = "string" },
                ["n"] = new JsonObject { ["type"] = "integer" },
                ["text"] = new JsonObject { ["type"] = "string" },
                ["keys"] = new JsonObject { ["type"] = "string" },
                ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("down", "up", "left", "right") },
                ["steps"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
            },
            ["required"] = new JsonArray("action"),
        },
        PromptGuidelines =
        [
            "Use windows for native Windows apps (settings, installers, desktop programs); for web pages use browser, which works in the page itself.",
            "Most actions work with the window in the background. Real clicks, key and typing without a text field's value pattern bring the window to the front and use the mouse and keyboard: if the user may be working, tell them first.",
        ],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var action = (a.Str("action", "verb", "command") ?? "").Trim().ToLowerInvariant();
        if (action is "attach" or "switch") action = "use";
        if (action is "launch" or "start" or "run") action = "open";
        if (action is "press") action = "key";
        if (action is "windows" or "ls") action = "list";
        if (action.Length == 0) return ToolResult.Error("Give an action: list, open, use, snapshot, click, type, key, … (see the tool's help).");
        if (action == "screenshot" && context.Model?.InputModalities is { Count: > 0 } mods && !mods.Contains("image"))
            return ToolResult.Error($"The current model ({context.Model.Id}) can't see images: use snapshot or read.");
        ToolResult result;
        try { result = await RunAsync(action, a, context, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Logger.LogWarning(ex, "windows {Action} failed", action);
            result = ToolResult.Error($"windows {action} failed: {ex.Message}", new { action, error = ex.Message });
        }
        await JournalAsync(context.SessionId, action, args, result).ConfigureAwait(false);
        return result;
    }

    private async Task<ToolResult> RunAsync(string action, ToolArgs a, ToolContext context, CancellationToken ct)
    {
        var sid = context.SessionId;
        switch (action)
        {
            case "list":
                return await ListAsync(ct).ConfigureAwait(false);
            case "open":
            {
                var app = a.Str("app", "program", "exe", "path", "uri");
                if (string.IsNullOrWhiteSpace(app)) return ToolResult.Error("open needs an app: a name (notepad, calc), a path, or a URI (ms-settings:).");
                var req = new JsonObject { ["cmd"] = "launch", ["exe"] = app.Trim(), ["args"] = a.Str("args", "arguments") ?? "", ["title"] = a.Str("title") };
                var timeout = a.Int("timeout") ?? 20;
                req["timeout"] = timeout * 1000;
                var w = await agent.CallAsync(req, ct, timeout + 10).ConfigureAwait(false);
                var chat = _windows[sid] = new ChatWindow(w["hwnd"]!.GetValue<long>());
                return await SnapshotAsync(chat, $"Opened {app} — its window: {w["title"]} ({w["process"]}).", "open", ct, full: true).ConfigureAwait(false);
            }
            case "use":
            {
                var which = a.Str("window", "hwnd", "title", "name")?.Trim();
                if (string.IsNullOrEmpty(which)) return ToolResult.Error("use needs a window: its number from list, or part of its title.");
                var all = (await agent.CallAsync(new JsonObject { ["cmd"] = "windows" }, ct).ConfigureAwait(false))["windows"]!.AsArray()
                    .Select(x => x!.AsObject()).ToList();
                var number = long.TryParse(which.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : (long?)null;
                var matches = number is { } hw
                    ? all.Where(x => x["hwnd"]!.GetValue<long>() == hw).ToList()
                    : all.Where(x => (x["title"]?.GetValue<string>() ?? "").Contains(which, StringComparison.OrdinalIgnoreCase)
                                     || (x["process"]?.GetValue<string>() ?? "").Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) return ToolResult.Error($"No open window matches \"{which}\": action list shows them.");
                if (matches.Count > 1 && number is null)
                {
                    var exact = matches.Where(x => string.Equals(x["title"]?.GetValue<string>(), which, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Count == 1) matches = exact;
                    else return ToolResult.Error($"{matches.Count} windows match \"{which}\": use one by number.\n" + string.Join("\n", matches.Select(Line)));
                }
                var chat = _windows[sid] = new ChatWindow(matches[0]["hwnd"]!.GetValue<long>());
                return await SnapshotAsync(chat, $"Using {Line(matches[0])}.", "use", ct, full: true).ConfigureAwait(false);
            }
        }
        if (!_windows.TryGetValue(sid, out var win))
            return ToolResult.Error("This chat has no window yet: open a program, or list the windows and use one.");
        switch (action)
        {
            case "snapshot":
                return await SnapshotAsync(win, "", "snapshot", ct, full: true, all: a.Bool("all", "full") == true, offscreen: a.Bool("offscreen") == true).ConfigureAwait(false);
            case "find":
            {
                var text = a.Str("text", "query", "q")?.Trim();
                if (string.IsNullOrEmpty(text)) return ToolResult.Error("find needs a text.");
                var (title, items) = await ReadWindowAsync(win, offscreen: true, ct).ConfigureAwait(false);
                var hits = items.Where(i => i.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
                var sb = new StringBuilder($"Window: {title}\n");
                sb.Append(hits.Count == 0 ? $"No control contains \"{text}\" ({items.Count} controls)." : $"{hits.Count} control(s) contain \"{text}\":\n");
                foreach (var i in hits.Take(40)) sb.Append($"[{i.N}] {i.Text}\n");
                return new ToolResult { Content = sb.ToString().TrimEnd(), Details = new { action, window = win.Hwnd, title, text, hits = hits.Count } };
            }
            case "read":
            {
                var n = a.Int("n", "number", "ref");
                if (n is null) return ToolResult.Error("read needs a control's number: n.");
                var r = await agent.CallAsync(new JsonObject { ["cmd"] = "act", ["hwnd"] = win.Hwnd, ["n"] = n, ["action"] = "read" }, ct).ConfigureAwait(false);
                var text = r["how"]?.GetValue<string>() ?? "";
                return new ToolResult { Content = $"The text of [{n}] ({text.Length} characters):\n{text}", Details = new { action, window = win.Hwnd, n, chars = text.Length } };
            }
            case "screenshot":
            {
                var r = await agent.CallAsync(new JsonObject { ["cmd"] = "capture", ["hwnd"] = win.Hwnd }, ct).ConfigureAwait(false);
                return new ToolResult
                {
                    Content = $"Screenshot of the window {r["title"]} ({r["width"]}×{r["height"]}).",
                    Images = [new ImagePart { MediaType = "image/png", Data = r["data"]!.GetValue<string>() }],
                    Details = new { action, window = win.Hwnd, title = r["title"]?.GetValue<string>() },
                };
            }
            case "close":
            {
                var r = await agent.CallAsync(new JsonObject { ["cmd"] = "close", ["hwnd"] = win.Hwnd }, ct).ConfigureAwait(false);
                _windows.TryRemove(sid, out _);
                return new ToolResult { Content = $"Closed the window {win.Title} ({r["how"]}). If it asks to save, it shows a dialog: list the windows to see it.", Details = new { action, window = win.Hwnd } };
            }
            case "steps":
            {
                var list = a.List("steps", "actions") ?? [];
                if (list.Count == 0) return ToolResult.Error("steps needs a list of actions.");
                var lines = new List<string>();
                for (var k = 0; k < list.Count; k++)
                {
                    var step = new ToolArgs(list[k]);
                    var name = (step.Str("action", "verb") ?? "").Trim().ToLowerInvariant();
                    if (name == "press") name = "key";
                    if (!StepActions.Contains(name))
                        return await SnapshotAsync(win, string.Join("\n", lines.Append($"Step {k + 1}: \"{name}\" can't be a step ({string.Join(", ", StepActions)}). Stopped there.")), "steps", ct, error: true).ConfigureAwait(false);
                    var (text, failed) = await ActAsync(win, name, step, ct).ConfigureAwait(false);
                    lines.Add($"{k + 1}. {text}");
                    if (failed) return await SnapshotAsync(win, string.Join("\n", lines.Append($"Stopped at step {k + 1} of {list.Count}.")), "steps", ct, error: true).ConfigureAwait(false);
                }
                return await SnapshotAsync(win, string.Join("\n", lines), "steps", ct).ConfigureAwait(false);
            }
            default:
            {
                if (!StepActions.Contains(action)) return ToolResult.Error($"Unknown action \"{action}\": see the tool's help.");
                var (text, failed) = await ActAsync(win, action, a, ct).ConfigureAwait(false);
                return await SnapshotAsync(win, text, action, ct, error: failed).ConfigureAwait(false);
            }
        }
    }

    private static string Line(JsonObject w) => $"[{w["hwnd"]}] {w["title"]} — {w["process"]}";

    private async Task<ToolResult> ListAsync(CancellationToken ct)
    {
        var all = (await agent.CallAsync(new JsonObject { ["cmd"] = "windows" }, ct).ConfigureAwait(false))["windows"]!.AsArray();
        var sb = new StringBuilder($"{all.Count} windows (use one by its number):\n");
        foreach (var w in all) sb.Append(Line(w!.AsObject())).Append('\n');
        return new ToolResult
        {
            Content = sb.ToString().TrimEnd(),
            Details = new { action = "list", windows = all.Select(w => new { hwnd = w!["hwnd"]!.GetValue<long>(), title = w["title"]?.GetValue<string>(), process = w["process"]?.GetValue<string>() }) },
        };
    }

    /// <summary>One action on the chat's window: its line, and whether it failed.</summary>
    private async Task<(string Text, bool Failed)> ActAsync(ChatWindow win, string action, ToolArgs a, CancellationToken ct)
    {
        switch (action)
        {
            case "focus":
            {
                var r = await agent.CallAsync(new JsonObject { ["cmd"] = "front", ["hwnd"] = win.Hwnd }, ct).ConfigureAwait(false);
                return r["front"]?.GetValue<bool>() == true ? ("Brought the window to the front.", false) : ("Could not bring the window to the front.", true);
            }
            case "key":
            {
                var keys = a.Str("keys", "key");
                var text = keys is null ? a.Str("text") : null;
                if (string.IsNullOrEmpty(keys) && string.IsNullOrEmpty(text)) return ("key needs keys (Enter, Ctrl+S) or a text.", true);
                var r = await agent.CallAsync(new JsonObject { ["cmd"] = "keys", ["hwnd"] = win.Hwnd, ["keys"] = keys ?? text, ["text"] = keys is null }, ct).ConfigureAwait(false);
                var sent = r["how"]?.GetValue<string>() ?? "";
                return sent.StartsWith("not", StringComparison.Ordinal) ? ($"Keys {sent}.", true) : (keys is null ? $"Typed \"{text}\"." : $"Pressed {keys}.", false);
            }
            case "wait":
            {
                var text = a.Str("text", "for", "until")?.Trim();
                var seconds = a.Double("seconds", "s");
                if (string.IsNullOrEmpty(text))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds ?? 1)), ct).ConfigureAwait(false);
                    return ($"Waited {seconds ?? 1:0.#} s.", false);
                }
                var gone = a.Bool("gone") == true;
                var timeout = a.Double("timeout") ?? seconds ?? 30;
                for (var until = DateTime.UtcNow.AddSeconds(timeout); ;)
                {
                    var (_, items) = await ReadWindowAsync(win, offscreen: false, ct).ConfigureAwait(false);
                    if (items.Any(i => i.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) != gone) return (gone ? $"\"{text}\" is gone." : $"\"{text}\" is there.", false);
                    if (DateTime.UtcNow >= until) return (gone ? $"\"{text}\" was still there after {timeout:0.#} s." : $"\"{text}\" did not appear within {timeout:0.#} s.", true);
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
        }
        var n = a.Int("n", "number", "index", "ref", "element");
        if (n is null) return ($"{action} needs a control's number: n.", true);
        var item = win.Last.FirstOrDefault(i => i.N == n);
        var helperAction = action switch
        {
            "click" when (a.Str("button") ?? "").Equals("right", StringComparison.OrdinalIgnoreCase) => "right",
            "click" when (a.Int("clicks", "count") ?? (a.Bool("double") == true ? 2 : 1)) >= 2 => "double",
            _ => action,
        };
        var req = new JsonObject { ["cmd"] = "act", ["hwnd"] = win.Hwnd, ["n"] = n, ["action"] = helperAction };
        if (action == "type") req["text"] = a.Str("text", "value") ?? "";
        if (action == "scroll") req["direction"] = (a.Str("direction", "dir") ?? "down").ToLowerInvariant();
        var answer = await agent.CallAsync(req, ct).ConfigureAwait(false);
        var how = answer["how"]?.GetValue<string>() ?? "";
        var what = item is null ? $"[{n}]" : $"[{n}] {item.Text}";
        if (how.StartsWith("not ", StringComparison.Ordinal) || how.StartsWith("it does not", StringComparison.Ordinal)) return ($"{char.ToUpperInvariant(how[0])}{how[1..]}: {what}", true);
        if (action == "type" && a.Bool("submit", "enter") == true)
        {
            await agent.CallAsync(new JsonObject { ["cmd"] = "keys", ["hwnd"] = win.Hwnd, ["keys"] = "Enter" }, ct).ConfigureAwait(false);
            how += ", then Enter";
        }
        var verb = action switch
        {
            "click" => helperAction == "right" ? "Right-clicked" : helperAction == "double" ? "Double-clicked" : "Clicked",
            "hover" => "Hovered over",
            "type" => item?.Text.Contains("(password)", StringComparison.Ordinal) == true ? $"Typed {(a.Str("text", "value") ?? "").Length} characters in" : $"Typed \"{a.Str("text", "value")}\" in",
            "toggle" => "Toggled",
            "expand" => "Expanded",
            "collapse" => "Collapsed",
            "select" => "Selected",
            "scroll" => "Scrolled",
            _ => action,
        };
        return ($"{verb} {what} ({how}).", false);
    }

    private async Task<(string Title, List<Item> Items)> ReadWindowAsync(ChatWindow win, bool offscreen, CancellationToken ct)
    {
        var r = await agent.CallAsync(new JsonObject { ["cmd"] = "snapshot", ["hwnd"] = win.Hwnd, ["offscreen"] = offscreen }, ct).ConfigureAwait(false);
        var items = r["elements"]!.AsArray().Select(e => new Item(e!["n"]!.GetValue<int>(), e["text"]!.GetValue<string>(), e["dist"]!.GetValue<double>())).ToList();
        win.Title = r["title"]?.GetValue<string>() ?? "";
        return (win.Title + (r["front"]?.GetValue<bool>() == true ? " (in front)" : ""), items);
    }

    /// <summary>The window after an action: everything (a new window, snapshot), or what changed since the last list.</summary>
    private async Task<ToolResult> SnapshotAsync(ChatWindow win, string result, string action, CancellationToken ct,
        bool full = false, bool all = false, bool offscreen = false, bool error = false)
    {
        var max = all ? int.MaxValue : Math.Clamp(ctx.Settings.Get("windows.maxControls", 300), 50, 5000);
        string title;
        List<Item> items;
        try
        {
            // the app updates its tree a moment after a pattern call or a posted click: read again until something
            // changed (about a second at most), so the result shows what the action did
            (title, items) = await ReadWindowAsync(win, offscreen, ct).ConfigureAwait(false);
            for (var k = 0; !full && k < 8 && Same(win.Last, items); k++)
            {
                await Task.Delay(125, ct).ConfigureAwait(false);
                (title, items) = await ReadWindowAsync(win, offscreen, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("the window is gone", StringComparison.Ordinal))
        {
            return ToolResult.Error($"{result}\nThe window is gone (closed). List the windows to find a dialog it left, or open the program again.".TrimStart(),
                new { action, window = win.Hwnd, gone = true });
        }
        var before = win.Last;
        win.Last = items;
        var sb = new StringBuilder();
        if (result.Length > 0) sb.Append(result).Append('\n');
        sb.Append($"Window: {title}\n");
        List<Item> shown;
        if (full || before.Count == 0)
        {
            shown = Nearest(items, max);
            if (shown.Count < items.Count) sb.Append($"({items.Count} controls; the {shown.Count} nearest the visible part are listed: find or snapshot with all for the others)\n");
        }
        else
        {
            static string Clean(string t) => t.Replace(" (focused)", "", StringComparison.Ordinal);
            var had = before.ToDictionary(i => i.N, i => Clean(i.Text));
            var now = items.Select(i => i.N).ToHashSet();
            var fresh = items.Where(i => !had.ContainsKey(i.N)).ToList();
            var changed = items.Where(i => had.TryGetValue(i.N, out var t) && t != Clean(i.Text)).ToList();
            var gone = before.Where(i => !now.Contains(i.N)).Select(i => i.N).ToList();
            var unseen = items.Where(i => i.Dist == 0 && !win.Seen.Contains(i.N) && !fresh.Contains(i) && !changed.Contains(i)).ToList();
            if (fresh.Count + changed.Count + gone.Count + unseen.Count == 0) sb.Append("No visible change.");
            else
            {
                var parts = new List<string>();
                if (fresh.Count > 0) parts.Add($"{fresh.Count} new");
                if (changed.Count > 0) parts.Add($"{changed.Count} changed");
                if (gone.Count > 0) parts.Add($"{gone.Count} gone ({string.Join(", ", gone.Order())})");
                if (unseen.Count > 0) parts.Add($"{unseen.Count} more in view");
                sb.Append("Changes: ").Append(string.Join(", ", parts)).Append(".\n");
            }
            var listed = fresh.Concat(changed).Concat(unseen).Select(i => i.N).ToHashSet();
            shown = Nearest(items.Where(i => listed.Contains(i.N)).ToList(), max);
            if (items.Count - listed.Count > 0 && listed.Count > 0) sb.Append($"(The other {items.Count - listed.Count} controls keep their numbers.)\n");
        }
        foreach (var i in shown)
        {
            sb.Append($"[{i.N}] {i.Text}\n");
            win.Seen.Add(i.N);
        }
        var content = sb.ToString().TrimEnd();
        return new ToolResult { Content = content, IsError = error, Details = new { action, window = win.Hwnd, title, controls = items.Count, shown = shown.Count, result } };
    }

    private static bool Same(List<Item> a, List<Item> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.N == p.Second.N
            && p.First.Text.Replace(" (focused)", "", StringComparison.Ordinal) == p.Second.Text.Replace(" (focused)", "", StringComparison.Ordinal));

    private static List<Item> Nearest(List<Item> items, int max) =>
        items.Count <= max ? items : [.. items.Select((x, k) => (x, k)).OrderBy(p => p.x.Dist).ThenBy(p => p.k).Take(max).OrderBy(p => p.k).Select(p => p.x)];

    /// <summary>Every step as a JSON line in &lt;home&gt;/windows/journal-yyyyMMdd.jsonl (training data for a smaller control picker).</summary>
    private async Task JournalAsync(string sessionId, string action, JsonElement args, ToolResult result)
    {
        if (!ctx.Settings.Get("windows.journal", true)) return;
        try
        {
            var dir = Path.Combine(ctx.Paths.Home, "windows");
            Directory.CreateDirectory(dir);
            var line = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["session"] = sessionId,
                ["window"] = _windows.TryGetValue(sessionId, out var w) ? w.Title : null,
                ["action"] = action,
                ["args"] = JsonNode.Parse(args.GetRawText()),
                ["error"] = result.IsError,
                ["result"] = result.Content,
            }.ToJsonString();
            await _journal.WaitAsync().ConfigureAwait(false);
            try { await File.AppendAllTextAsync(Path.Combine(dir, $"journal-{DateTime.UtcNow:yyyyMMdd}.jsonl"), line + "\n").ConfigureAwait(false); }
            finally { _journal.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ctx.Logger.LogDebug(ex, "windows: the journal was not written"); }
    }
}
