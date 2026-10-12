using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Webhooks;

/// <summary>
/// Turns one accepted payload into a run — or says why not. The decision is made before anything is created, so a skipped
/// payload leaves no empty chat behind: the webhook is enabled, the payload passes its match conditions, no run with the same
/// dedupe key started inside the window, the webhook is under its hourly and concurrent caps, the project exists, an agent
/// runtime is running, the agent the webhook names (or any, or the first free-priced one) can take it, and the budget is not
/// exhausted unless the agent is free. The run itself is <c>CreateSession</c> (meta <c>webhook</c>), <c>agents.use</c>,
/// <c>profiles.applyAuto</c> and <c>IAgentRuntime.SendAsync</c> with <c>Source = "system"</c> — the surfaces a user's chat and a
/// scheduled run use, so the Work tab, <c>runs.list</c> and the budget see it like any other run.
/// </summary>
internal sealed class WebhookRunner(IPluginContext ctx, WebhookStore store, TimeProvider time)
{
    public const string ChangedEvent = "webhooks.changed", RanEvent = "webhooks.ran";
    public const int DefaultMaxPerHour = 6, DefaultMaxConcurrent = 2, DefaultDedupeMinutes = 60;

    /// <summary>Starts for one webhook go one at a time: the checks read the runs, and a second payload of the same alert arriving while the first is still creating its chat would pass them.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _gates = [];

    /// <summary>What a payload would do, without doing it: the filled-in prompt, title and key, and the reason it would be skipped.</summary>
    internal sealed record Plan(string Prompt, string Title, string? Key, string? Skip, string? AgentKey, bool Free);

    public async Task<Plan> PlanAsync(JsonObject hook, JsonNode? payload, bool check, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var prompt = Template.Expand(hook["prompt"]?.GetValue<string>() ?? "{{payload}}", payload, now);
        var name = hook["name"]?.GetValue<string>() ?? hook["id"]!.GetValue<string>();
        var title = hook["title"]?.GetValue<string>() is { Length: > 0 } t ? Template.Expand(t, payload, now) : name;
        var key = hook["dedupe"]?["key"]?.GetValue<string>() is { Length: > 0 } k ? Template.Expand(k, payload, now).Trim() : null;
        if (key is { Length: 0 }) key = null;

        string? skip = null;
        if (!WebhookStore.Enabled(hook)) skip = hook["proposedBy"] is not null ? "the webhook is a proposal the user has not enabled" : "the webhook is disabled";
        skip ??= Template.FailedCondition(hook["match"] as JsonArray, payload) is { } failed ? "filtered: " + failed : null;
        if (skip is not null || !check) return new Plan(prompt, title, key, skip, null, false);

        var id = hook["id"]!.GetValue<string>();
        if (key is not null && DedupeMinutes(hook) > 0 && store.LastStartedWithKey(id, key) is { } prior
            && DateTimeOffset.TryParse(prior["at"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            && now - at < TimeSpan.FromMinutes(DedupeMinutes(hook)))
        {
            var ago = (int)Math.Max(1, (now - at).TotalMinutes);
            skip = $"duplicate of “{key}”, which started a chat {ago} min ago" + (prior["sessionId"]?.GetValue<string>() is { Length: > 0 } sid ? $" ({sid})" : "") + $" (dedupe window {DedupeMinutes(hook)} min)";
        }
        var recent = store.StartedSince(id, now.AddHours(-1));
        var perHour = Math.Clamp(Int(hook, "maxPerHour", DefaultMaxPerHour), 1, 1000);
        if (skip is null && recent.Count >= perHour) skip = $"it already started {perHour} chats in the last hour (maxPerHour)";
        if (skip is null && ctx.Services.Get<IAgentRuntime>() is { } runtime)
        {
            var cap = Math.Clamp(Int(hook, "maxConcurrent", DefaultMaxConcurrent), 1, 100);
            var running = store.StartedSince(id, now.AddDays(-1)).Count(r =>
                r["sessionId"]?.GetValue<string>() is { Length: > 0 } s && runtime.GetBySession(s) is { Status: AgentStatus.Queued or AgentStatus.Running or AgentStatus.Yielded });
            if (running >= cap) skip = $"{running} of its chats are still going (maxConcurrent {cap})";
        }
        var (agentKey, free, agentSkip) = await ResolveAgentAsync(hook["agent"]?.GetValue<string>(), ct).ConfigureAwait(false);
        skip ??= agentSkip;
        skip ??= await CheckProjectAndBudgetAsync(hook, free, ct).ConfigureAwait(false);
        return new Plan(prompt, title, key, skip, agentKey, free);
    }

    /// <summary>Handle one authenticated payload: plan it, and start the chat unless it is skipped. Always records the outcome.</summary>
    public async Task<RunResult> HandleAsync(JsonObject hook, JsonNode? payload, string? from, CancellationToken ct)
    {
        var id = hook["id"]!.GetValue<string>();
        SemaphoreSlim gate;
        lock (_gates) gate = _gates.TryGetValue(id, out var g) ? g : _gates[id] = new SemaphoreSlim(1, 1);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var plan = await PlanAsync(hook, payload, check: true, ct).ConfigureAwait(false);
            if (plan.Skip is not null) return Finish(hook, now, WebhookStore.Skipped, plan.Skip, null, plan, from);
            return await StartAsync(hook, plan, now, from, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<RunResult> StartAsync(JsonObject hook, Plan plan, DateTimeOffset now, string? from, CancellationToken ct)
    {
        var id = hook["id"]!.GetValue<string>();
        var runtime = ctx.Services.Get<IAgentRuntime>()!;
        var projectId = hook["projectId"]?.GetValue<string>();
        SessionInfo session;
        try
        {
            session = ctx.Sessions.CreateSession(new SessionInfo
            {
                Title = Template.Clip("Webhook: " + plan.Title.ReplaceLineEndings(" "), 80),
                ProjectId = projectId,
                Meta = new JsonObject { ["webhook"] = id, ["webhookName"] = hook["name"]?.GetValue<string>() ?? id },
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Finish(hook, now, WebhookStore.Failed, "the chat could not be created: " + ex.Message, null, plan, from);
        }
        try
        {
            if (!string.IsNullOrEmpty(plan.AgentKey) && ctx.Rpc.Exists("agents.use"))
                await ctx.Rpc.InvokeAsync("agents.use", new JsonObject { ["sessionId"] = session.Id, ["agent"] = plan.AgentKey }, ct).ConfigureAwait(false);
            if (ctx.Rpc.Exists("profiles.applyAuto"))
                await ctx.Rpc.InvokeAsync("profiles.applyAuto", new JsonObject { ["sessionId"] = session.Id }, ct).ConfigureAwait(false);
            await runtime.SendAsync(session.Id, new UserInput
            {
                Text = plan.Prompt,
                Source = "system",
                Meta = new JsonObject { ["webhook"] = id, ["webhookName"] = hook["name"]?.GetValue<string>() ?? id },
            }, DeliveryMode.Auto, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try { ctx.Sessions.DeleteSession(session.Id); } catch { /* already gone */ }
            ctx.Logger.LogWarning(ex, "Webhook {Webhook}: the run did not start", id);
            return Finish(hook, now, WebhookStore.Failed, "the run did not start: " + ex.Message, null, plan, from);
        }
        ctx.Logger.LogInformation("Webhook {Webhook} started a run in {Session}", id, session.Id);
        return Finish(hook, now, WebhookStore.Started, null, session.Id, plan, from);
    }

    private RunResult Finish(JsonObject hook, DateTimeOffset at, string status, string? reason, string? sessionId, Plan plan, string? from)
    {
        var id = hook["id"]!.GetValue<string>();
        store.Record(id, at, status, reason, sessionId, plan.Key, Template.Clip(plan.Title.ReplaceLineEndings(" "), 160), from);
        if (status != WebhookStore.Started) ctx.Logger.LogInformation("Webhook {Webhook}: {Status} — {Reason}", id, status, reason);
        ctx.Events.Publish(RanEvent, new { webhookId = id, status, reason, sessionId }, sessionId);
        ctx.Events.Publish(ChangedEvent, new { id });
        return new RunResult(status, reason, sessionId);
    }

    // ---------------------------------------------------------------- agents, project, budget

    /// <summary>
    /// The agent a webhook asks for: empty = the default (nothing is set), <c>any</c> = whichever agent has a free instance first
    /// (the runtime's own "any"), <c>free</c> = the first available agent that costs nothing, <c>a,b,c</c> = the first of these that
    /// is available, otherwise one key. Returns the key to <c>agents.use</c>, whether that agent is free, and the reason it cannot run.
    /// </summary>
    internal async Task<(string? Key, bool Free, string? Skip)> ResolveAgentAsync(string? spec, CancellationToken ct)
    {
        spec = spec?.Trim();
        if (string.IsNullOrEmpty(spec)) return (null, false, null);
        var slots = await ReadAsync("agents.list", ct).ConfigureAwait(false) is JsonArray list ? list.OfType<JsonObject>().Where(s => s["key"] is not null).ToList() : null;
        if (slots is null) return (string.Equals(spec, "any", StringComparison.OrdinalIgnoreCase) ? "any" : spec.Split(',')[0].Trim(), false, null);   // no Agents plugin: nothing to check against

        bool Usable(JsonObject s) => !Bool(s["disabled"]) && !(s["available"] is JsonValue av && av.TryGetValue<bool>(out var a) && !a);
        if (string.Equals(spec, "any", StringComparison.OrdinalIgnoreCase))
        {
            var usable = slots.Where(Usable).ToList();
            if (slots.Count > 0 && usable.Count == 0) return (null, false, "no agent is available");
            return ("any", usable.Any(s => Bool(s["free"])), null);
        }
        if (string.Equals(spec, "free", StringComparison.OrdinalIgnoreCase))
        {
            var pick = slots.FirstOrDefault(s => Usable(s) && Bool(s["free"]));
            return pick is null ? (null, false, "no free agent is available (webhook agent “free”)") : (Str(pick["key"]), true, null);
        }
        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // a list is a preference order: the first agent that can run it now — a paid one is passed over while the budget is spent
        var spent = wanted.Length > 1 && await ReadAsync("budget.status", ct).ConfigureAwait(false) is JsonObject budget && Bool(budget["exhausted"]);
        foreach (var key in wanted)
        {
            var slot = slots.FirstOrDefault(s => string.Equals(Str(s["key"]), key, StringComparison.OrdinalIgnoreCase));
            if (slot is not null && Usable(slot) && (!spent || Bool(slot["free"]))) return (Str(slot["key"]), Bool(slot["free"]), null);
        }
        return (null, false, wanted.Length == 1 ? $"the agent {wanted[0]} is unavailable or does not exist"
            : $"none of the agents {string.Join(", ", wanted)} is available" + (spent ? " (the budget is spent, so only free ones count)" : ""));
    }

    private async Task<string?> CheckProjectAndBudgetAsync(JsonObject hook, bool free, CancellationToken ct)
    {
        if (hook["projectId"]?.GetValue<string>() is { Length: > 0 } projectId && ctx.Sessions.GetProject(projectId) is null)
            return $"the project {projectId} no longer exists";
        if (ctx.Services.Get<IAgentRuntime>() is null) return "no agent runtime is running (the netpi.runtime plugin)";
        if (!free && await ReadAsync("budget.status", ct).ConfigureAwait(false) is JsonObject budget && Bool(budget["exhausted"]))
            return "the budget is exhausted (budget.status)";
        return null;
    }

    private async Task<JsonNode?> ReadAsync(string method, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists(method)) return null;
        try { return NetPiJson.ToNode(await ctx.Rpc.InvokeAsync(method, new JsonObject(), ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Webhooks: {Method} failed; its check is skipped", method);
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    internal static int DedupeMinutes(JsonObject hook) =>
        hook["dedupe"]?["minutes"] is JsonValue v && v.TryGetValue<int>(out var m) ? Math.Clamp(m, 0, 24 * 60 * 14) : DefaultDedupeMinutes;

    private static int Int(JsonObject o, string name, int fallback) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal sealed record RunResult(string Status, string? Reason, string? SessionId);
}
