using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Webhooks;

/// <summary>
/// Hooks: actions NetPI itself runs at fixed points of a webhook's life, so a notification never depends on the agent remembering to
/// send it. A hook is <c>{ on, when?, afterMinutes?, once?, do }</c>:
/// <list type="bullet">
/// <item><c>received</c> — every payload that arrives (before any filter or grouping): the pre-hook; an immediate alert for the severe ones, a "clear" for a recovery.</item>
/// <item><c>started</c> — the incident's chat was started.</item>
/// <item><c>finished</c> — the chat's run ended (after the first alert, and again after each follow-up that woke it): the post-hook, with the agent's final answer and its parsed verdict.</item>
/// <item><c>asking</c> — the chat is waiting for the user (<c>ask_user</c>).</item>
/// <item><c>stalled</c> — the chat has been running for <c>afterMinutes</c> (default 10) without finishing.</item>
/// <item><c>failed</c> — the alerts could not be turned into a chat (the retries ran out, the project or profile is gone).</item>
/// </list>
/// <c>when</c> is a list of match conditions over the hook's context: the alert's own fields (<c>host</c>, <c>state</c>…) plus <c>event</c>,
/// <c>key</c>, <c>count</c>, <c>sessionId</c>, <c>title</c>, <c>status</c> (ok|error|no-answer), <c>verdict</c>, <c>result</c> and <c>summary</c>.
/// The verdict is the last <c>VERDICT: word</c> line of the final answer (<c>unknown</c> when there is none), so a gate fails open: a hook
/// that says "when the verdict is not noise" also fires when the agent said nothing, died, or stalled. <c>once</c> names a key that fires at most once
/// per incident (so a "notify" reached from two places notifies once). <c>do</c> is a <c>command</c> (a program run with no shell: its arguments, environment
/// and standard input are templates) or an <c>http</c> request; a <c>json</c> object is serialised after its strings are expanded, so no value can break the JSON.
/// The program of a command is never a template, and alert text should reach a program through standard input rather than arguments wherever a shell sits behind
/// it (ssh joins its arguments into one remote command line). Only the user can write hooks: an agent's change to them pauses the webhook as a proposal.
/// </summary>
internal sealed class HookEngine(IPluginContext ctx, WebhookStore store, TimeProvider time)
{
    public static readonly string[] Events = ["received", "started", "finished", "asking", "stalled", "failed"];
    public const int MaxHooks = 20, DefaultStalledMinutes = 10;
    private static readonly Regex VerdictLine = new(@"^\s*VERDICT:\s*([A-Za-z][A-Za-z_-]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // ---------------------------------------------------------------- validation (when a webhook is saved)

    public static JsonArray Validate(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray list) throw new RpcException("bad_request", "hooks must be an array of { on, when?, once?, do }");
        if (list.Count > MaxHooks) throw new RpcException("bad_request", $"at most {MaxHooks} hooks");
        var clean = new JsonArray();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject h) throw new RpcException("bad_request", $"hook {i + 1} must be an object");
            var what = $"hook {i + 1}";
            var on = h["on"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (on is null || !Events.Contains(on)) throw new RpcException("bad_request", $"{what}: on must be one of {string.Join(", ", Events)}");
            var allowed = new HashSet<string> { "on", "when", "once", "do", "afterMinutes", "name" };
            if (h.Select(kv => kv.Key).FirstOrDefault(k => !allowed.Contains(k)) is { } unknown) throw new RpcException("bad_request", $"{what}: unknown field “{unknown}”");
            var c = new JsonObject { ["on"] = on };
            if (h["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name) c["name"] = Template.Clip(name, 80);
            if (h["when"] is not null) c["when"] = Template.ValidateConditions(h["when"]);
            if (h["once"]?.GetValue<string>()?.Trim() is { Length: > 0 } once) c["once"] = Template.Clip(once, 50);
            if (h["afterMinutes"] is not null)
            {
                if (on != "stalled") throw new RpcException("bad_request", $"{what}: afterMinutes only belongs to a stalled hook");
                if (h["afterMinutes"] is not JsonValue v || !v.TryGetValue<int>(out var m) || m < 1 || m > 1440) throw new RpcException("bad_request", $"{what}: afterMinutes must be 1–1440");
                c["afterMinutes"] = m;
            }
            c["do"] = ValidateAction(h["do"], what);
            clean.Add(c);
        }
        return clean;
    }

    private static JsonObject ValidateAction(JsonNode? node, string what)
    {
        if (node is not JsonObject a) throw new RpcException("bad_request", $"{what}: do must be an object with a type of command or http");
        var type = a["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var clean = new JsonObject { ["type"] = type };
        switch (type)
        {
            case "command":
            {
                var command = a["command"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(command)) throw new RpcException("bad_request", $"{what}: a command action needs a command (the program to run)");
                if (command.Contains("{{")) throw new RpcException("bad_request", $"{what}: the program itself cannot be a template (alert text must never choose what runs)");
                clean["command"] = command;
                if (a["args"] is not null)
                {
                    if (a["args"] is not JsonArray args || args.Count > 50 || args.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
                        throw new RpcException("bad_request", $"{what}: args must be an array of at most 50 strings");
                    clean["args"] = args.DeepClone();
                }
                if (a["env"] is JsonObject env) clean["env"] = env.DeepClone();
                if (a["timeoutSeconds"] is not null)
                {
                    if (a["timeoutSeconds"] is not JsonValue tv || !tv.TryGetValue<int>(out var seconds) || seconds < 1 || seconds > 300) throw new RpcException("bad_request", $"{what}: timeoutSeconds must be 1–300");
                    clean["timeoutSeconds"] = seconds;
                }
                Body(a, clean, what, optional: true);   // json | text = what is written to the program's standard input
                break;
            }
            case "http":
                var url = a["url"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    throw new RpcException("bad_request", $"{what}: an http action needs an http(s) url");
                clean["url"] = url;
                var method = a["method"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "POST";
                clean["method"] = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" ? method : throw new RpcException("bad_request", $"{what}: method must be GET, POST, PUT, PATCH or DELETE");
                if (a["headers"] is JsonObject hd) clean["headers"] = hd.DeepClone();
                Body(a, clean, what, optional: true);
                break;
            default:
                throw new RpcException("bad_request", $"{what}: do.type must be command or http");
        }
        return clean;
    }

    private static void Body(JsonObject a, JsonObject clean, string what, bool optional = false)
    {
        if (a["json"] is JsonObject j) clean["json"] = j.DeepClone();
        else if (a["text"]?.GetValue<string>() is { } text) clean["text"] = text;
        else if (!optional) throw new RpcException("bad_request", $"{what}: give the payload as json (an object of templates) or text");
    }

    // ---------------------------------------------------------------- the verdict

    /// <summary>The last <c>VERDICT: word</c> line of an answer, lower-cased, else <c>unknown</c>. A deterministic read of a line the prompt asks for: a missing one is "unknown", never a guess.</summary>
    public static string Verdict(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return "unknown";
        string? last = null;
        try { foreach (Match m in VerdictLine.Matches(answer)) last = m.Groups[1].Value; }
        catch (RegexMatchTimeoutException) { return "unknown"; }
        return last?.ToLowerInvariant() ?? "unknown";
    }

    /// <summary>The answer without its verdict line, collapsed to one clipped paragraph: what a notification can carry.</summary>
    public static string Summary(string? answer, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(answer)) return "";
        string cleaned;
        try { cleaned = VerdictLine.Replace(answer, ""); } catch (RegexMatchTimeoutException) { cleaned = answer; }
        return Template.Clip(Regex.Replace(cleaned.Trim(), @"\s+", " "), max);
    }

    // ---------------------------------------------------------------- running hooks

    /// <summary>
    /// Run the hooks of <paramref name="webhook"/> for <paramref name="evt"/>. <paramref name="context"/> is mutated (event is set).
    /// <paramref name="fired"/> is the incident's once-keys (null for a payload with no incident); true when it changed.
    /// A failing action is logged and recorded in the history, and never stops the others or the webhook.
    /// </summary>
    public async Task<bool> FireAsync(JsonObject webhook, string evt, JsonObject context, JsonArray? fired, string? sessionId, string? incidentKey, CancellationToken ct)
    {
        if (webhook["hooks"] is not JsonArray hooks || hooks.Count == 0) return false;
        context["event"] = evt;
        var changed = false;
        var id = webhook["id"]!.GetValue<string>();
        for (var i = 0; i < hooks.Count; i++)
        {
            if (hooks[i] is not JsonObject h || h["on"]?.GetValue<string>() != evt) continue;
            if (evt == "stalled")
            {
                var after = h["afterMinutes"] is JsonValue av && av.TryGetValue<int>(out var am) ? am : DefaultStalledMinutes;
                if (context["elapsedMinutes"] is not JsonValue ev || !ev.TryGetValue<double>(out var elapsed) || elapsed < after) continue;
            }
            if (h["when"] is JsonArray when && Template.FailedCondition(when, context) is not null) continue;
            // a stalled or asking hook fires once per wait for an answer (the incident's awaiting period), whatever else it says
            var once = h["once"]?.GetValue<string>() ?? (evt is "stalled" or "asking" ? $"{evt}#{i}@{Template.Show(context["awaitingSince"])}" : null);
            if (once is not null && fired is not null && fired.Any(f => f?.GetValue<string>() == once)) continue;
            var name = h["name"]?.GetValue<string>() ?? $"{evt} #{i + 1}";
            string status, reason;
            try
            {
                var detail = await RunAsync(h["do"] as JsonObject ?? new JsonObject(), context, ct).ConfigureAwait(false);
                status = WebhookStore.Hook;
                reason = $"{name}: {detail}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Logger.LogWarning(ex, "Webhook {Webhook}: hook {Hook} failed", id, name);
                status = WebhookStore.Hook;
                reason = $"{name}: FAILED — {ex.Message}";
            }
            if (once is not null && fired is not null) { fired.Add(once); changed = true; }
            store.Record(id, time.GetUtcNow(), status, reason, sessionId, incidentKey, Template.Clip(Template.Show(context["title"]) is { Length: > 0 } t ? t : evt, 160), null, touch: false);
            ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });
        }
        return changed;
    }

    /// <summary>Run one action with a context; the one-line result. Used by the hooks and by <c>webhooks.testHook</c>.</summary>
    public async Task<string> RunAsync(JsonObject action, JsonObject context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        switch (action["type"]?.GetValue<string>())
        {
            case "command":
            {
                var program = action["command"]!.GetValue<string>();
                var info = new ProcessStartInfo(program)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                if (action["args"] is JsonArray args) foreach (var arg in args) info.ArgumentList.Add(Template.Expand(Template.Show(arg), context, now));
                if (action["env"] is JsonObject env) foreach (var (k, v) in env) info.Environment[k] = Template.Expand(Template.Show(v), context, now);
                var stdin = Payload(action, context, now);
                var seconds = action["timeoutSeconds"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 30;
                Process process;
                try { process = Process.Start(info) ?? throw new InvalidOperationException($"{program} did not start"); }
                catch (Win32Exception ex) { throw new InvalidOperationException($"{program} could not be started: {ex.Message}"); }
                using (process)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                    try
                    {
                        if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token).ConfigureAwait(false);
                        process.StandardInput.Close();
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                        throw new InvalidOperationException($"{Path.GetFileName(program)} did not finish within {seconds} s and was stopped");
                    }
                    catch (IOException) { /* the program closed its input early: its exit code says what happened */ }
                    var o = (await output.ConfigureAwait(false)).Trim();
                    var e = (await errors.ConfigureAwait(false)).Trim();
                    var name = Path.GetFileName(program);
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException($"{name} exited {process.ExitCode}" + (FirstLine(e.Length > 0 ? e : o) is { Length: > 0 } why ? ": " + why : ""));
                    return $"{name} exited 0" + (FirstLine(o) is { Length: > 0 } said ? ": " + said : "");
                }
            }
            case "http":
            {
                var url = Template.Expand(action["url"]!.GetValue<string>(), context, now);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException($"“{url}” is not an http(s) url");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var request = new HttpRequestMessage(new HttpMethod(action["method"]?.GetValue<string>() ?? "POST"), uri);
                if (action["headers"] is JsonObject headers)
                    foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, Template.Expand(Template.Show(v), context, now));
                if (Payload(action, context, now) is { } body)
                    request.Content = new StringContent(body, Encoding.UTF8, action["json"] is not null ? "application/json" : "text/plain");
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{request.Method} {uri.Host}{uri.AbsolutePath} answered HTTP {(int)response.StatusCode}");
                return $"{request.Method} {uri.Host}{uri.AbsolutePath} → {(int)response.StatusCode}";
            }
            default:
                throw new InvalidOperationException("unknown action type");
        }
    }

    private static string FirstLine(string text) =>
        Template.Clip(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "", 160);

    /// <summary>The action's body (for a command: its standard input): its <c>json</c> object with every string expanded and serialised, else its <c>text</c> expanded, else null.</summary>
    private static string? Payload(JsonObject action, JsonObject context, DateTimeOffset now)
    {
        if (action["json"] is JsonNode json) return ExpandJson(json, context, now)?.ToJsonString();
        return action["text"]?.GetValue<string>() is { } text ? Template.Expand(text, context, now) : null;
    }

    private static JsonNode? ExpandJson(JsonNode? node, JsonObject context, DateTimeOffset now) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, ExpandJson(kv.Value, context, now)))),
        JsonArray a => new JsonArray([.. a.Select(x => ExpandJson(x, context, now))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Template.Expand(s, context, now)),
        _ => node.DeepClone(),
    };
}
