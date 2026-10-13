using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Webhooks;

/// <summary>
/// Hooks: two optional commands NetPI itself runs around a webhook's work, so a notification never depends on the agent remembering to send it.
/// <list type="bullet">
/// <item><c>pre</c> — runs for every payload that arrives (before any filter or grouping). Standard input: <c>{ event: "received", webhook, title, alert }</c>.</item>
/// <item><c>post</c> — runs when the incident's chat ends or needs attention. <c>event</c> is <c>finished</c> (the run ended: <c>status</c>, the agent's <c>result</c>,
/// its parsed <c>verdict</c> and a one-paragraph <c>summary</c>), <c>asking</c> (the chat waits for the user: <c>question</c>), <c>stalled</c> (still running after the webhook's
/// <c>stalledMinutes</c>, default 10, 0 = never: <c>elapsedMinutes</c>) or <c>failed</c> (the alerts could not be turned into a chat: <c>reason</c>). Standard input:
/// <c>{ event, webhook, title, key, count, sessionId, alert (the first alert), … }</c>.</item>
/// </list>
/// A hook is <c>{ command, args?, timeoutSeconds? }</c>: a program run with no shell and the JSON above on its standard input (alert text never reaches an argument or the
/// program name); exit code 0 = ok. All the routing (who to tell, when, how) is the script's business, not the webhook's. The verdict is the last <c>VERDICT: word</c> line of
/// the final answer (<c>unknown</c> when there is none, so a script that notifies "unless noise" also notifies when the agent said nothing, died or stalled). <c>asking</c> and
/// <c>stalled</c> fire once per wait for an answer. A failing hook is logged and recorded in the history, and never stops the webhook. Only the user can write hooks: an agent's
/// change to them pauses the webhook as a proposal.
/// </summary>
internal sealed class HookEngine(IPluginContext ctx, WebhookStore store, TimeProvider time)
{
    public const int DefaultStalledMinutes = 10;
    public static readonly string[] Slots = ["pre", "post"];
    private static readonly Regex VerdictLine = new(@"^\s*VERDICT:\s*([A-Za-z][A-Za-z_-]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // ---------------------------------------------------------------- validation (when a webhook is saved)

    /// <summary>One hook slot (<c>pre</c> or <c>post</c>) as saved: <c>{ command, args?, timeoutSeconds? }</c>; null (or an empty object) clears it.</summary>
    public static JsonObject? Validate(JsonNode? node, string what)
    {
        if (node is null || node is JsonObject { Count: 0 }) return null;
        if (node is not JsonObject h) throw new RpcException("bad_request", $"{what} must be an object: command, args?, timeoutSeconds?");
        if (h.Select(kv => kv.Key).FirstOrDefault(k => k is not ("command" or "args" or "timeoutSeconds")) is { } unknown)
            throw new RpcException("bad_request", $"{what}: unknown field “{unknown}” (command, args, timeoutSeconds)");
        var command = h["command"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(command)) throw new RpcException("bad_request", $"{what}: needs a command (the program to run)");
        var clean = new JsonObject { ["command"] = command };
        if (h["args"] is not null)
        {
            if (h["args"] is not JsonArray args || args.Count > 50 || args.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
                throw new RpcException("bad_request", $"{what}: args must be an array of at most 50 strings");
            clean["args"] = args.DeepClone();
        }
        if (h["timeoutSeconds"] is not null)
        {
            if (h["timeoutSeconds"] is not JsonValue tv || !tv.TryGetValue<int>(out var seconds) || seconds < 1 || seconds > 300) throw new RpcException("bad_request", $"{what}: timeoutSeconds must be 1–300");
            clean["timeoutSeconds"] = seconds;
        }
        return clean;
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
    /// Run the hook of <paramref name="webhook"/> that belongs to <paramref name="evt"/> (<c>received</c> → pre; <c>finished</c>, <c>asking</c>, <c>stalled</c>, <c>failed</c> → post).
    /// <paramref name="context"/> is mutated (event and webhook are set). <paramref name="fired"/> is the incident's once-keys (null for a payload with no incident); true when it changed.
    /// </summary>
    public async Task<bool> FireAsync(JsonObject webhook, string evt, JsonObject context, JsonArray? fired, string? sessionId, string? incidentKey, CancellationToken ct)
    {
        var slot = evt == "received" ? "pre" : "post";
        if (webhook[slot] is not JsonObject hook) return false;
        var id = webhook["id"]!.GetValue<string>();
        string? once = null;
        if (evt == "stalled")
        {
            var after = webhook["stalledMinutes"] is JsonValue av && av.TryGetValue<int>(out var am) ? am : DefaultStalledMinutes;
            if (after <= 0 || context["elapsedMinutes"] is not JsonValue ev || !ev.TryGetValue<double>(out var elapsed) || elapsed < after) return false;
        }
        // asking and stalled fire once per wait for an answer (the incident's awaiting period)
        if (evt is "stalled" or "asking")
        {
            once = $"{evt}@{Template.Show(context["awaitingSince"])}";
            if (fired is not null && fired.Any(f => f?.GetValue<string>() == once)) return false;
        }
        context["event"] = evt;
        context["webhook"] = new JsonObject { ["id"] = id, ["name"] = webhook["name"]?.DeepClone() };
        string detail;
        try { detail = $"{slot} hook ({evt}): {await RunAsync(hook, context, ct).ConfigureAwait(false)}"; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Webhook {Webhook}: the {Slot} hook failed on {Event}", id, slot, evt);
            detail = $"{slot} hook ({evt}): FAILED — {ex.Message}";
        }
        var changed = false;
        if (once is not null && fired is not null) { fired.Add(once); changed = true; }
        store.Record(id, time.GetUtcNow(), WebhookStore.Hook, detail, sessionId, incidentKey, Template.Clip(Template.Show(context["title"]) is { Length: > 0 } t ? t : evt, 160), null, touch: false);
        ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });
        return changed;
    }

    /// <summary>Run one hook with a context on its standard input; the one-line result. Used by the hooks and by <c>webhooks.testHook</c>.</summary>
    public async Task<string> RunAsync(JsonObject hook, JsonObject context, CancellationToken ct)
    {
        var program = hook["command"]!.GetValue<string>();
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (hook["args"] is JsonArray args) foreach (var arg in args) info.ArgumentList.Add(Template.Show(arg));
        var seconds = hook["timeoutSeconds"] is JsonValue tv && tv.TryGetValue<int>(out var t) ? t : 30;
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
                await process.StandardInput.WriteAsync(context.ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
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

    private static string FirstLine(string text) =>
        Template.Clip(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "", 160);
}
