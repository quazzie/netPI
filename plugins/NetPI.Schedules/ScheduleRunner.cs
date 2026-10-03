using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Schedules;

/// <summary>
/// Starts a schedule's run as an ordinary chat — or says why it did not. The checks run before anything is created, so a
/// skipped run leaves no empty chat behind: the project still exists, an agent runtime is running, the previous run of the
/// same schedule has finished, the schedule is under its 24-hour cap, the agent it names exists and is available, and the
/// budget is not exhausted (unless the agent is free). The run itself is <c>CreateSession</c> (meta <c>schedule</c>),
/// <c>agents.use</c> and <c>IAgentRuntime.SendAsync</c> with <c>Source = "system"</c> — the same surfaces a user's chat and
/// <c>budget.allow</c> use, so the Work tab, <c>runs.list</c>, the slots and the budget see it like any other run.
/// </summary>
internal sealed class ScheduleRunner(IPluginContext ctx, ScheduleStore store, TimeProvider time)
{
    public const string Ran = "ran", Skipped = "skipped", Failed = "failed", Missed = "missed";
    public const string ChangedEvent = "schedules.changed", RanEvent = "schedules.ran";

    /// <summary>Claim what is due and start or skip each one; the number started.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var grace = TimeSpan.FromHours(Math.Clamp(ctx.Settings.Get("schedules.missedGraceHours", 24), 0, 24 * 30));
        var claims = store.ClaimDue(now, grace);
        if (claims.Count == 0) return 0;
        var started = 0;
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            if (claim.Missed)
            {
                Finish(claim.Schedule, now, Missed, $"due at {ScheduleStore.Iso(claim.Due)}, more than {grace.TotalHours:0} h ago (schedules.missedGraceHours): the host was not running", null);
                continue;
            }
            if ((await StartAsync(claim.Schedule, ct).ConfigureAwait(false)).Status == Ran) started++;
        }
        ctx.Events.Publish(ChangedEvent, new { });
        return started;
    }

    /// <summary>Start one run now (a due one, or <c>schedules.run</c>): <c>{ status, reason?, sessionId? }</c>.</summary>
    public async Task<RunResult> StartAsync(JsonObject schedule, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var id = schedule["id"]!.GetValue<string>();
        if (await CheckAsync(schedule, now, ct).ConfigureAwait(false) is { } reason)
            return Finish(schedule, now, Skipped, reason, null);

        var runtime = ctx.Services.Get<IAgentRuntime>()!;
        var name = schedule["name"]?.GetValue<string>() ?? id;
        var projectId = schedule["projectId"]?.GetValue<string>();
        var agent = schedule["agent"]?.GetValue<string>();
        SessionInfo session;
        try
        {
            session = ctx.Sessions.CreateSession(new SessionInfo
            {
                Title = Clip($"Scheduled: {name}", 80),
                ProjectId = projectId,
                Meta = new JsonObject { ["schedule"] = id },
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Finish(schedule, now, Failed, "the chat could not be created: " + ex.Message, null);
        }
        try
        {
            if (!string.IsNullOrEmpty(agent) && ctx.Rpc.Exists("agents.use"))
                await ctx.Rpc.InvokeAsync("agents.use", new JsonObject { ["sessionId"] = session.Id, ["agent"] = agent }, ct).ConfigureAwait(false);
            await runtime.SendAsync(session.Id, new UserInput
            {
                Text = schedule["prompt"]?.GetValue<string>() ?? "",
                Source = "system",
                Meta = new JsonObject { ["schedule"] = id, ["scheduleName"] = name },
            }, DeliveryMode.Auto, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // nothing started: the chat is not left behind
            try { ctx.Sessions.DeleteSession(session.Id); } catch { /* already gone */ }
            ctx.Logger.LogWarning(ex, "Schedule {Schedule}: the run did not start", id);
            return Finish(schedule, now, Failed, "the run did not start: " + ex.Message, null);
        }
        ctx.Logger.LogInformation("Schedule {Schedule} started a run in {Session}", id, session.Id);
        return Finish(schedule, now, Ran, null, session.Id);
    }

    /// <summary>Why this run must not start now, or null. Nothing here creates or changes anything.</summary>
    internal async Task<string?> CheckAsync(JsonObject schedule, DateTimeOffset now, CancellationToken ct)
    {
        var id = schedule["id"]!.GetValue<string>();
        if (schedule["projectId"]?.GetValue<string>() is { Length: > 0 } projectId && ctx.Sessions.GetProject(projectId) is null)
            return $"the project {projectId} no longer exists";
        if (ctx.Services.Get<IAgentRuntime>() is not { } runtime)
            return "no agent runtime is running (the netpi.runtime plugin)";
        if (schedule["lastSessionId"]?.GetValue<string>() is { Length: > 0 } last
            && runtime.GetBySession(last) is { Status: AgentStatus.Queued or AgentStatus.Running or AgentStatus.Yielded })
            return $"the previous run ({last}) is still going";
        var cap = Math.Clamp(ctx.Settings.Get("schedules.maxRunsPerDay", 48), 1, 1440);
        if (store.StartedSince(id, now.AddDays(-1)) >= cap)
            return $"it already ran {cap} times in the last 24 hours (schedules.maxRunsPerDay)";

        var agent = schedule["agent"]?.GetValue<string>();
        var slots = await ReadAsync("agents.list", ct).ConfigureAwait(false) is JsonArray list ? list.Where(n => n is not null).Select(n => n!).ToList() : null;
        var free = false;
        if (slots is not null && !string.IsNullOrEmpty(agent) && !string.Equals(agent, "any", StringComparison.OrdinalIgnoreCase))
        {
            var slot = slots.FirstOrDefault(s => string.Equals(Str(s["key"]), agent, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return $"the agent {agent} does not exist";
            if (Bool(slot["disabled"])) return $"the agent {agent} is disabled";
            if (slot["available"] is JsonValue av && av.TryGetValue<bool>(out var available) && !available)
                return $"the agent {agent} is unavailable" + (Str(slot["unavailable"]) is { Length: > 0 } why ? ": " + why : "");
            free = Bool(slot["free"]);
        }
        else if (slots is not null)
        {
            // any agent: one has to be there at all, and a free one can still run when the budget is spent
            var usable = slots.Where(s => !Bool(s["disabled"]) && !(s["available"] is JsonValue v && v.TryGetValue<bool>(out var a) && !a)).ToList();
            if (slots.Count > 0 && usable.Count == 0) return "no agent is available";
            free = usable.Count > 0 && usable.Any(s => Bool(s["free"]));
        }
        if (!free && await ReadAsync("budget.status", ct).ConfigureAwait(false) is JsonObject budget && Bool(budget["exhausted"]))
            return "the budget is exhausted (budget.status)";
        return null;
    }

    private RunResult Finish(JsonObject schedule, DateTimeOffset at, string status, string? reason, string? sessionId)
    {
        var id = schedule["id"]!.GetValue<string>();
        store.Record(id, at, status, reason, sessionId);
        if (status != Ran) ctx.Logger.LogInformation("Schedule {Schedule}: {Status} — {Reason}", id, status, reason);
        ctx.Events.Publish(RanEvent, new { scheduleId = id, status, reason, sessionId }, sessionId);
        return new RunResult(status, reason, sessionId);
    }

    /// <summary>Another plugin's read, or null when that plugin is not running (then its check does not apply).</summary>
    private async Task<JsonNode?> ReadAsync(string method, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists(method)) return null;
        try { return NetPiJson.ToNode(await ctx.Rpc.InvokeAsync(method, new JsonObject(), ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Schedules: {Method} failed; its check is skipped", method);
            return null;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    internal sealed record RunResult(string Status, string? Reason, string? SessionId);
}
