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
/// <item><c>pre</c> (the Before command) — runs for every payload that arrives, before anything else. Standard input: <c>{ event: "received", webhook, title, alert }</c>.
/// It is also the sender's adapter: if its standard output is a JSON object, <c>skip</c> (true = ignore the payload), <c>start</c> (false = only join a conversation that is
/// already open, never start one), <c>key</c> (the conversation: the same key joins the chat that handles it), <c>text</c> (how the item reads in the prompt) and <c>title</c>
/// (the chat's title) shape what happens next. No command, no output, or a command that fails: every field takes its default (nothing skipped, no key, the payload as JSON) —
/// a broken filter never swallows a payload.</item>
/// <item><c>post</c> (the After command) — runs when the incident's chat ends or needs attention. <c>event</c> is <c>finished</c> (the run ended: <c>status</c>, the agent's
/// <c>result</c>, its parsed <c>verdict</c> and a one-paragraph <c>summary</c>), <c>asking</c> (the chat waits for the user: <c>question</c>), <c>timeout</c> (still running after the
/// webhook's <c>timeoutMinutes</c>, default 10, 0 = never: <c>elapsedMinutes</c>) or <c>failed</c> (the payloads could not be turned into a chat: <c>reason</c>). Standard input:
/// <c>{ event, webhook, title, key, count, sessionId, alert (the first payload), … }</c>.</item>
/// </list>
/// A hook is <c>{ command, args?, timeoutSeconds? }</c>: a program run with no shell and the JSON above on its standard input (alert text never reaches an argument or the
/// program name); exit code 0 = ok. All the routing (who to tell, when, how) is the script's business, not the webhook's. The verdict is the last <c>VERDICT: word</c> line of
/// the final answer (<c>unknown</c> when there is none, so a script that notifies "unless noise" also notifies when the agent said nothing, died or timed out). <c>asking</c> and
/// <c>timeout</c> fire once per wait for an answer. A failing hook is logged and recorded in the history, and never stops the webhook. Only the user can write hooks: an agent's
/// change to them pauses the webhook as a proposal.
/// </summary>
internal sealed class HookEngine(IPluginContext ctx, WebhookStore store, TimeProvider time)
{
    public const int DefaultTimeoutMinutes = 10;
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

    // ---------------------------------------------------------------- the Before command's answer

    /// <summary>What the Before command said about a payload. The default (no command, no output, a failure) changes nothing.</summary>
    internal sealed record Adapt(bool Skip = false, bool Start = true, string? Key = null, string? Text = null, string? Title = null)
    {
        public static Adapt From(JsonObject? o)
        {
            if (o is null) return new Adapt();
            static string? S(JsonNode? n, int max) => n is null ? null : Template.Show(n).Trim() is { Length: > 0 } s ? Template.Clip(s, max) : null;
            static bool Flag(JsonNode? n, bool fallback) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
            return new Adapt(Flag(o["skip"], false), Flag(o["start"], true), S(o["key"], 200), S(o["text"], 20_000), S(o["title"], 200));
        }

        /// <summary>What a program's standard output says: a JSON object is read, anything else is the default.</summary>
        public static Adapt Parse(string? output)
        {
            var text = output?.Trim();
            if (string.IsNullOrEmpty(text) || text[0] != '{') return new Adapt();
            try { return From(JsonNode.Parse(text) as JsonObject); }
            catch (System.Text.Json.JsonException) { return new Adapt(); }
        }
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
    /// Run the After command of <paramref name="webhook"/> for <paramref name="evt"/> (<c>finished</c>, <c>asking</c>, <c>timeout</c> or <c>failed</c>).
    /// <paramref name="context"/> is mutated (event and webhook are set). <paramref name="fired"/> is the incident's once-keys (null for a payload with no incident); true when it changed.
    /// </summary>
    public async Task<bool> FireAsync(JsonObject webhook, string evt, JsonObject context, JsonArray? fired, string? sessionId, string? incidentKey, CancellationToken ct)
    {
        if (webhook["post"] is not JsonObject hook) return false;
        var id = webhook["id"]!.GetValue<string>();
        string? once = null;
        if (evt == "timeout")
        {
            var after = TimeoutMinutes(webhook);
            if (after <= 0 || context["elapsedMinutes"] is not JsonValue ev || !ev.TryGetValue<double>(out var elapsed) || elapsed < after) return false;
            context["timeoutMinutes"] = after;
        }
        // asking and timeout fire once per wait for an answer (the incident's awaiting period)
        if (evt is "timeout" or "asking")
        {
            once = $"{evt}@{Template.Show(context["awaitingSince"])}";
            if (fired is not null && fired.Any(f => f?.GetValue<string>() == once)) return false;
        }
        context["event"] = evt;
        context["webhook"] = new JsonObject { ["id"] = id, ["name"] = webhook["name"]?.DeepClone() };
        string detail;
        try { detail = $"After command ({evt}): {(await RunAsync(hook, context, ct).ConfigureAwait(false)).Detail}"; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Webhook {Webhook}: the After command failed on {Event}", id, evt);
            detail = $"After command ({evt}): FAILED — {ex.Message}";
        }
        var changed = false;
        if (once is not null && fired is not null) { fired.Add(once); changed = true; }
        store.Record(id, time.GetUtcNow(), WebhookStore.Hook, detail, sessionId, incidentKey, Template.Clip(Template.Show(context["title"]) is { Length: > 0 } t ? t : evt, 160), null, touch: false);
        ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });
        return changed;
    }

    public static int TimeoutMinutes(JsonObject webhook) =>
        webhook["timeoutMinutes"] is JsonValue v && v.TryGetValue<int>(out var m) ? m : DefaultTimeoutMinutes;

    /// <summary>
    /// The Before command: run it on a payload and read what it says (see <see cref="Adapt"/>). Never throws and never blocks the payload: no command, a failure or a
    /// timeout all give the default. The run is a <c>hook</c> row in the history.
    /// </summary>
    public async Task<Adapt> PreAsync(JsonObject webhook, JsonNode payload, string title, CancellationToken ct)
    {
        if (webhook["pre"] is not JsonObject hook) return new Adapt();
        var id = webhook["id"]!.GetValue<string>();
        var context = new JsonObject
        {
            ["event"] = "received", ["title"] = title, ["alert"] = payload.DeepClone(),
            ["webhook"] = new JsonObject { ["id"] = id, ["name"] = webhook["name"]?.DeepClone() },
        };
        Adapt adapt;
        string detail;
        try
        {
            var run = await RunAsync(hook, context, ct).ConfigureAwait(false);
            adapt = Adapt.Parse(run.Output);
            detail = $"Before command: {run.Detail}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Webhook {Webhook}: the Before command failed; the payload goes on unchanged", id);
            adapt = new Adapt();
            detail = $"Before command: FAILED — {ex.Message} (the payload goes on unchanged)";
        }
        store.Record(id, time.GetUtcNow(), WebhookStore.Hook, detail, null, adapt.Key, Template.Clip(title, 160), null, touch: false);
        ctx.Events.Publish(WebhookRunner.ChangedEvent, new { id });
        return adapt;
    }

    /// <summary>How long the output pipes get to close after the hook exited before its remaining children are killed.</summary>
    internal static TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    internal sealed record HookRun(string Detail, string Output);

    /// <summary>Run one hook with a context on its standard input; the one-line result and the whole standard output. Used by the hooks and by <c>webhooks.testHook</c>.</summary>
    public async Task<HookRun> RunAsync(JsonObject hook, JsonObject context, CancellationToken ct)
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
        using (var job = new ProcessJob()) // the hook's tree ends with it: a timeout, a cancel or a child that keeps the pipes open
        {
            job.Assign(process);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            // read without the token: killing the job closes the pipes, which ends the reads
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            string o, e;
            try
            {
                await process.StandardInput.WriteAsync(context.ToJsonString().AsMemory(), timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                // the hook has exited; a child of it may still hold the pipes: give the reads a moment, then end the tree
                var reads = Task.WhenAll(output, errors);
                if (await Task.WhenAny(reads, Task.Delay(DrainGrace, timeout.Token)).ConfigureAwait(false) != reads)
                {
                    job.Kill(process);
                    await Task.WhenAny(reads, Task.Delay(DrainGrace, timeout.Token)).ConfigureAwait(false);
                }
                o = (output.IsCompletedSuccessfully ? output.Result : "").Trim();
                e = (errors.IsCompletedSuccessfully ? errors.Result : "").Trim();
            }
            catch (OperationCanceledException)
            {
                job.Kill(process);
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException($"{Path.GetFileName(program)} did not finish within {seconds} s and was stopped");
            }
            catch (IOException)
            {
                // the program closed its input early: its exit code says what happened
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    job.Kill(process);
                    if (ct.IsCancellationRequested) throw;
                    throw new InvalidOperationException($"{Path.GetFileName(program)} did not finish within {seconds} s and was stopped");
                }
                var reads = Task.WhenAll(output, errors);
                if (await Task.WhenAny(reads, Task.Delay(DrainGrace)).ConfigureAwait(false) != reads) job.Kill(process);
                await Task.WhenAny(reads, Task.Delay(DrainGrace)).ConfigureAwait(false);
                o = (output.IsCompletedSuccessfully ? output.Result : "").Trim();
                e = (errors.IsCompletedSuccessfully ? errors.Result : "").Trim();
            }
            var name = Path.GetFileName(program);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{name} exited {process.ExitCode}" + (FirstLine(e.Length > 0 ? e : o) is { Length: > 0 } why ? ": " + why : ""));
            return new HookRun($"{name} exited 0" + (FirstLine(o) is { Length: > 0 } said ? ": " + said : ""), o);
        }
    }

    private static string FirstLine(string text) =>
        Template.Clip(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "", 160);
}
