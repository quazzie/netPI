using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Schedules;

/// <summary>
/// The <c>schedules</c> tool: the model-facing surface of scheduled runs (idea-1y1174's UI/tool half). It calls the
/// plugin's own <c>schedules.*</c> RPCs, so there is one validation path; the tool adds the model-facing guards the
/// RPCs deliberately leave to the user's tab — a chat started by a schedule cannot grow the schedule set, and the set
/// is capped at <see cref="MaxSchedules"/>. Deleting a schedule is up to the user, in the Schedules tab.
/// <para>
/// An add is a proposal: stored paused with <c>proposedBy</c>, and the call then waits for the user's OK on a card in
/// the chat (<see cref="PendingProposals"/>: <c>schedule.proposed</c> / <c>schedule.decided</c>, <c>schedules.decide</c>):
/// a wait for the user (<see cref="UserWait"/>, kind <c>schedule</c>), like <c>ask_user</c>, so the run ends at the call
/// and goes on with the decision. Allow enables the schedule, discard deletes it.
/// A subagent's add does not wait — nobody watches a subagent's chat — and stays a proposal in the Schedules tab.
/// </para>
/// </summary>
internal sealed class SchedulesTool(IPluginContext ctx) : IAgentTool, IReadOnlyCalls
{
    /// <summary>A runaway add loop is capped; the user deletes in the tab, the model does not.</summary>
    public const int MaxSchedules = 50;

    private readonly IPluginContext _ctx = ctx;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "schedules",
        Label = "Schedules",
        Category = "schedules",
        SummaryArg = "action",
        ReadOnly = false,
        Description = "Prompts that start on their own as a fresh chat: list, get, history, add, update (also pause), run now. Adds come back as a proposal the user allows or discards in a card in the chat, and enables what you add or change in the Schedules tab.",
        PromptGuidelines =
        [
            "Use the schedules tool to set up recurring or one-time prompts the user asks for (\"every morning\", \"tonight at 22:00\"); list first to avoid duplicates. An add waits for the user's OK on a card in the chat: it runs from the moment they allow it, and is gone if they discard it.",
        ],
        Help = """
            Actions:
            - list: every schedule (or only one project's, with projectId), one line each.
            - get: one schedule by id, with its full prompt.
            - history: a schedule's runs, newest first (limit, default 20): what ran, and why a run was skipped.
            - add: propose a new schedule. Needs prompt and cadence; the name defaults to the prompt's start, the project to this
              chat's. It is a proposal: a card in the chat asks the user to allow it (it runs from then on, with every tool) or
              discard it (it is deleted). A subagent's add has no card — nobody watches its chat — and stays a paused proposal
              in the Schedules tab.
            - update: change a schedule (prompt, name, cadence, project, agent) or pause it with enabled false. A change of what
              runs (prompt, cadence, project, agent) pauses it until the user enables it again; resuming is the user's.
              Changing the cadence counts the next run from now.
            - run: start a schedule's run now, beside its cadence.

            Cadence shapes:
            - once:  { "kind": "once",  "at": "2026-10-07T22:00:00+02:00" }  — a future ISO 8601 time with an offset.
            - every: { "kind": "every", "minutes": 60 }
            - daily: { "kind": "daily", "time": "07:30", "tz": "Europe/Stockholm" }  — tz is an IANA zone, default the machine's.

            Rules:
            - A once time must be in the future. The add result echoes the machine's current local time and offset, so you can compute it.
            - A run starts a fresh chat on the schedule's project. Runs are skipped when the previous run is still going,
              the schedule is over its per-day cap, its agent is unavailable, or the budget is exhausted; runs missed while
              the host was down are recorded, not replayed.
            - Deleting a schedule is up to the user, in the Schedules tab. Pause one with action update, enabled false.
            """,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "list | get | history | add | update | run (also: create, new, pause, resume, show)",
                    ["enum"] = new JsonArray(["list", "get", "history", "add", "update", "run"]),
                },
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "get/history/update/run: the schedule's id" },
                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "add/update: what the fresh chat is told" },
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "add/update: a short name; defaults to the prompt's start" },
                ["cadence"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "add/update: { kind: once|every|daily, at? (future ISO 8601 with offset), minutes? (at least the minimum), time? (\"HH:mm\"), tz? (IANA zone) }",
                },
                ["projectId"] = new JsonObject { ["type"] = "string", ["description"] = "add/update/list: a project id or name; add defaults to this chat's project; \"\" = none" },
                ["agent"] = new JsonObject { ["type"] = "string", ["description"] = "add/update: an agent key, \"any\", or \"\" to clear" },
                ["priority"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("background", "low", "normal", "high", "critical"), ["description"] = "add/update: the order its chats queue in while their agent is busy (default normal)" },
                ["enabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "update: false pauses (enabling is the user's, in the Schedules tab)" },
                ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "history: how many runs (default 20)" },
            },
        },
    };

    public bool IsReadOnly(JsonElement args)
    {
        var a = new ToolArgs(args);
        return Action(a, out _) is "list" or "get" or "history";
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            return await RunAsync(context, args, ct).ConfigureAwait(false);
        }
        catch (ToolCallError ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var action = Action(a, out var forcedEnabled);
        switch (action)
        {
            case "delete":
                return ToolResult.Error("Deleting a schedule is up to the user, in the Schedules tab. Pause it with action update, enabled false.");
            case "list":
            {
                var list = await RpcAsync<JsonArray>("schedules.list", new JsonObject
                    {
                        ["projectId"] = a.Str("projectId"),
                    }, ct).ConfigureAwait(false);
                var all = list.Where(n => n is not null).Cast<JsonObject>().ToList();
                var now = DateTimeOffset.Now;
                var shown = all.Take(MaxList).ToList();
                var lines = shown.Select(s => Line(s, now)).ToList();
                if (all.Count > MaxList) lines.Add($"… and {all.Count - MaxList} more (filter with projectId)");
                var head = all.Count == 0
                    ? "No schedules. Add one with action add."
                    : $"{all.Count} schedule{(all.Count == 1 ? "" : "s")} ({all.Count(s => Enabled(s))} enabled), now {Stamp(now)}:";
                var text = string.Join("\n", [head, .. lines]);
                return ToolResult.Ok(text, new { kind = "schedules", schedules = all, now = Stamp(now) });
            }
            case "get":
            {
                var id = RequireId(a);
                var s = (await RpcAsync<JsonObject>("schedules.get", new JsonObject { ["id"] = id }, ct).ConfigureAwait(false));
                var project = await ProjectNameAsync(s["projectId"]?.GetValue<string>(), ct).ConfigureAwait(false);
                var now = DateTimeOffset.Now;
                var prompt = s["prompt"]?.GetValue<string>() ?? "";
                var agent = s["agent"]?.GetValue<string>() ?? "default";
                var text = string.Join("\n", [Line(s, now), "prompt: " + prompt, "project: " + project, "agent: " + agent, "priority: " + (s["priority"]?.GetValue<string>() ?? "normal")]);
                return ToolResult.Ok(text, new { kind = "schedule", schedule = s });
            }
            case "history":
            {
                var id = a.Str("id");
                var runs = await RpcAsync<JsonArray>("schedules.history", new JsonObject
                    {
                        ["id"] = id,
                        ["limit"] = a.Int("limit") ?? 20,
                    }, ct).ConfigureAwait(false);
                var rows = runs.Where(n => n is not null).Cast<JsonObject>().ToList();
                var lines = new List<string>();
                if (rows.Count == 0)
                {
                    lines.Add("No runs yet.");
                }
                else
                {
                    lines.Add($"{rows.Count} runs, newest first:");
                    foreach (var h in rows)
                    {
                        var bits = new List<string> { Local(h["at"]?.GetValue<string>()), h["status"]?.GetValue<string>() ?? "?" };
                        if (h["reason"]?.GetValue<string>() is { Length: > 0 } r) bits.Add(r);
                        if (h["sessionId"]?.GetValue<string>() is { Length: > 0 } sid) bits.Add("chat " + sid);
                        lines.Add(string.Join(" · ", bits));
                    }
                }
                var text = string.Join("\n", lines);
                return ToolResult.Ok(text, new { kind = "schedule-history", scheduleId = id, runs = rows });
            }
            case "add":
                return await AddAsync(context, a, forcedEnabled, ct).ConfigureAwait(false);
            case "update":
                return await UpdateAsync(context, a, forcedEnabled, ct).ConfigureAwait(false);
            case "run":
                return await RunOneAsync(context, a, ct).ConfigureAwait(false);
            default:
                return ToolResult.Error($"Unknown schedules action '{a.Str("action")}' (list, get, history, add, update, run).");
        }
    }

    // --- the guards -------------------------------------------------------------

    private async Task<ToolResult> AddAsync(ToolContext context, ToolArgs a, bool? forcedEnabled, CancellationToken ct)
    {
        var own = OwnSchedule(context);
        if (own is not null)
            return ToolResult.Error("A scheduled run cannot create schedules; that is up to the user, in the Schedules tab.");
        var count = (await RpcAsync<JsonArray>("schedules.list", new JsonObject(), ct).ConfigureAwait(false)).Count(n => n is not null);
        if (count >= MaxSchedules)
            return ToolResult.Error($"There are already {MaxSchedules} schedules; ask the user to delete some (the Schedules tab) before adding more.");
        var p = new JsonObject { ["prompt"] = a.Str("prompt"), ["cadence"] = Cadence(a) };
        Fill(p, a, forcedEnabled, projectIdDefault: context.Project?.Id);
        p.Remove("enabled");
        p["proposedBy"] = Proposer(context);
        var s = await RpcAsync<JsonObject>("schedules.add", p, ct).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var project = await ProjectNameAsync(s["projectId"]?.GetValue<string>(), ct).ConfigureAwait(false);
        var name = s["name"]?.GetValue<string>() ?? "?";
        var when = s["when"]?.GetValue<string>() ?? "?";
        var sid = s["id"]?.GetValue<string>() ?? "?";

        // A subagent's chat nobody watches: no card, the proposal stays in the Schedules tab and the model tells its parent
        var runtime = context.Services.Get<IAgentRuntime>();
        if (runtime is null || runtime.Get(context.AgentId)?.IsSubagent == true)
            return ToolResult.Ok(
                $"Proposed schedule {sid} “{name}” ({when}): it does not run until the user enables it in the Schedules tab, so tell them it is there. A schedule runs on its own with every tool, which is why " +
                $"one an agent sets up waits for the user's OK. Now is {Stamp(now)}. Each run starts a fresh chat on project {project}.",
                Details(s, project, "proposal"));

        // the call waits for the user's decision (the run ends here and goes on with it: PendingProposals.DecideAsync)
        return ToolResult.WaitForUser(new UserWait
        {
            Id = "schq_" + Ids.Short(12),
            Kind = PendingProposals.Kind,
            Activity = "waiting for your decision on the schedule",
            Data = new JsonObject { ["schedule"] = s.DeepClone(), ["project"] = project },
            IfSteered = ToolResult.Ok(
                $"No decision: the user wrote a new message instead; it follows. The schedule “{name}” ({sid}, {when}) stays a proposal in the Schedules tab (paused) until they enable it there.",
                Details(s, project, "steered")),
            IfCancelled = ToolResult.Ok(
                $"No decision: the chat was stopped before the user decided. The schedule “{name}” ({sid}, {when}) stays a proposal in the Schedules tab (paused) until they enable it there.",
                Details(s, project, "cancelled")),
        });
    }

    /// <summary>What the waiting call reads when the user decided: allowed (the enabled schedule) or discarded.</summary>
    internal static ToolResult Decided(string decision, JsonObject proposed, JsonObject? updated, string? project)
    {
        var name = proposed["name"]?.GetValue<string>() ?? "?";
        var when = proposed["when"]?.GetValue<string>() ?? "?";
        var sid = proposed["id"]?.GetValue<string>() ?? "?";
        return decision == "allow"
            ? ToolResult.Ok($"The user allowed the schedule “{name}” ({sid}, {when}): it is enabled and runs from now on, each run a fresh chat on project {project}. Now is {Stamp(DateTimeOffset.Now)}.",
                Details(updated ?? proposed, project, "allowed"))
            : ToolResult.Ok($"The user discarded the schedule “{name}” ({sid}): it is deleted. Do not add it again unless asked.",
                Details(proposed, project, "discarded"));
    }

    private static object Details(JsonObject schedule, string? project, string status) => new JsonObject
    {
        ["kind"] = "schedule",
        ["schedule"] = schedule.DeepClone(),
        ["project"] = project,
        ["status"] = status,
        ["created"] = true,
    };

    private async Task<ToolResult> UpdateAsync(ToolContext context, ToolArgs a, bool? forcedEnabled, CancellationToken ct)
    {
        var id = RequireId(a);
        var own = OwnSchedule(context);
        if (own is not null)
        {
            // a digest run may pause itself — and only itself, and only pause: nothing else grows or moves the set
            if (!string.Equals(id, own, StringComparison.Ordinal) || (forcedEnabled ?? a.Bool("enabled") ?? true))
                return ToolResult.Error("A scheduled run may only pause its own schedule (action update, id its own schedule, enabled false).");
        }
        // Enabling (resuming) is the user's OK: a schedule runs unattended with every tool (idea-96bh9b).
        if ((forcedEnabled ?? a.Bool("enabled")) == true)
            return ToolResult.Error("Enabling or resuming a schedule is up to the user, in the Schedules tab; tell them which one. You may pause one (enabled false).");
        var p = new JsonObject { ["id"] = id };
        Fill(p, a, forcedEnabled);
        p["proposedBy"] = Proposer(context);
        var s = await RpcAsync<JsonObject>("schedules.update", p, ct).ConfigureAwait(false);
        var changed = p.Select(kvp => kvp.Key).Where(k => k is not ("id" or "proposedBy")).ToList();
        var proposed = s["proposedBy"] is not null && !Enabled(s);
        return ToolResult.Ok(
            $"Updated {id}: {string.Join(", ", changed.DefaultIfEmpty("enabled"))}. " +
            (proposed
                ? "It is paused as a proposal until the user enables it again in the Schedules tab (a change of what a schedule runs waits for their OK): tell them."
                : $"{(Enabled(s) ? "enabled" : "paused")}; next run {Local(s["nextRunAt"]?.GetValue<string>())}."),
            new { kind = "schedule", schedule = s, proposed });
    }

    /// <summary>Who proposes a change: the chat the tool runs in.</summary>
    private static string Proposer(ToolContext context) => "session:" + context.SessionId;

    private async Task<ToolResult> RunOneAsync(ToolContext context, ToolArgs a, CancellationToken ct)
    {
        var own = OwnSchedule(context);
        if (own is not null)
            return ToolResult.Error("A scheduled run cannot start schedules; that is up to the user, in the Schedules tab.");
        var id = RequireId(a);
        var r = await RpcAsync<JsonObject>("schedules.run", new JsonObject { ["id"] = id }, ct).ConfigureAwait(false);
        var status = r["status"]?.GetValue<string>() ?? "?";
        var reason = r["reason"]?.GetValue<string>();
        var sessionId = r["sessionId"]?.GetValue<string>();
        var details = new { kind = "schedule-run", id, status, reason, sessionId };
        return status switch
        {
            "ran" => ToolResult.Ok($"Started {id}: chat {sessionId}.", details),
            "skipped" => ToolResult.Error($"Skipped: {reason}", details),
            _ => ToolResult.Error($"Failed: {reason}", details),
        };
    }

    /// <summary>
    /// The schedule that started this chat (meta.schedule), or the chat a subagent works for (its parents, up to eight levels), or
    /// null for an ordinary one. A subagent of a scheduled run inherits its parent's tools, schedules included, and its own session
    /// carries no schedule mark: reading only its own let it grow the set the run itself may not (idea-96bh9b).
    /// </summary>
    private string? OwnSchedule(ToolContext context)
    {
        var id = context.SessionId;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(id); i++)
        {
            var session = _ctx.Sessions.GetSession(id);
            if (session?.Meta?["schedule"] is JsonValue v && v.TryGetValue<string>(out var schedule) && schedule.Length > 0) return schedule;
            id = session?.ParentSessionId;
        }
        return null;
    }

    // --- shared helpers ---------------------------------------------------------

    /// <summary>The action a call means: an explicit one (with synonyms) or inferred from what is present.</summary>
    private static string? Action(ToolArgs a, out bool? forcedEnabled)
    {
        forcedEnabled = null;
        var named = a.Str("action")?.Trim().ToLowerInvariant();
        switch (named)
        {
            case "create" or "new": return "add";
            case "pause": forcedEnabled = false; return "update";
            case "resume": forcedEnabled = true; return "update";
            case "edit" or "change": return "update";
            case "show": return "get";
            case "delete":
                return "delete";   // refused with the "up to the user" answer below
            case "list" or "get" or "history" or "add" or "update" or "run": return named;
            case null: break;
            default: return named;   // an unknown name: the switch answers it
        }
        var hasId = a.Str("id") is not null;
        var hasChange = a.Str("prompt") is not null || a.Has("cadence") || a.Str("name") is not null
            || a.Bool("enabled") is not null || a.Str("projectId") is not null || a.Str("agent") is not null || a.Str("priority") is not null;
        if (hasId && hasChange) return "update";
        if (hasId) return "get";
        if (a.Str("prompt") is not null && a.Has("cadence")) return "add";
        return "list";
    }

    /// <summary>Copy the fields a call carries into the RPC params (an empty string clears project/agent).</summary>
    private static void Fill(JsonObject p, ToolArgs a, bool? forcedEnabled, string? projectIdDefault = null)
    {
        if (a.Str("prompt") is { } prompt) p["prompt"] = prompt;
        if (a.Str("name") is { } name) p["name"] = name;
        if (a.Has("cadence")) p["cadence"] = Cadence(a);
        var project = a.Str("projectId") ?? projectIdDefault;
        if (project is not null) p["projectId"] = project;
        if (a.Str("agent") is { } agent) p["agent"] = agent;
        if (a.Str("priority") is { } priority) p["priority"] = priority;
        if (forcedEnabled is { } f) p["enabled"] = f;
        else if (a.Bool("enabled") is { } e) p["enabled"] = e;
    }

    /// <summary>The cadence argument: an object as sent, or a JSON string of one (the lenient reader's contract).</summary>
    private static JsonNode? Cadence(ToolArgs a)
    {
        if (!a.TryGet(out var v, "cadence")) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim() ?? "";
            if (s.Length == 0) return null;
            try { return JsonNode.Parse(s); } catch (JsonException) { return null; }   // the RPC's parse says what is wrong
        }
        return JsonNode.Parse(v.GetRawText());
    }

    private static string RequireId(ToolArgs a) =>
        a.Str("id")?.Trim() is { Length: > 0 } id ? id : throw new ToolCallError("this action needs an id (action list shows them)");

    private static bool Enabled(JsonObject s) => s["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>One schedule as the model reads it: id · name · when · state · next · last.</summary>
    private static string Line(JsonObject s, DateTimeOffset now)
    {
        var state = s["finishedAt"] is not null ? "finished" : Enabled(s) ? "enabled"
            : s["proposedBy"] is not null ? "proposed (the user enables it)" : "paused";
        var last = s["lastStatus"]?.GetValue<string>() is { } st
            ? st + (s["lastReason"]?.GetValue<string>() is { Length: > 0 } r ? $": {r}" : "") + " " + Ago(s["lastRunAt"]?.GetValue<string>(), now)
            : "never";
        return string.Join(" · ",
            s["id"]?.GetValue<string>() ?? "?",
            s["name"]?.GetValue<string>() ?? "?",
            s["when"]?.GetValue<string>() ?? "?",
            state,
            "next " + Local(s["nextRunAt"]?.GetValue<string>()),
            "last " + last);
    }

    /// <summary>A UTC ISO stamp as the machine's local time with offset (what the user's clock says).</summary>
    private static string Local(string? iso)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return "–";
        return at.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + Offset(at);
    }

    private static string Ago(string? iso, DateTimeOffset now)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return "";
        var d = now - at;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    private static string Stamp(DateTimeOffset now) => now.ToString("yyyy-MM-dd HH:mm") + Offset(now);

    private static string Offset(DateTimeOffset t) => (t.Offset >= TimeSpan.Zero ? "+" : "") + t.Offset.ToString("hh\\:mm");

    private async Task<string> ProjectNameAsync(string? projectId, CancellationToken ct)
    {
        if (projectId is null) return "no project";
        try
        {
            var list = await RpcAsync<JsonArray>("projects.list", new JsonObject(), ct).ConfigureAwait(false);
            var hit = list.FirstOrDefault(n => n?["id"]?.GetValue<string>() == projectId);
            return hit?["name"]?.GetValue<string>() ?? projectId;
        }
        catch (ToolCallError) { return projectId; }   // the env has no projects RPC: the id reads fine as-is
    }

    private async Task<T> RpcAsync<T>(string method, JsonObject p, CancellationToken ct) where T : class
    {
        try
        {
            var node = (JsonNode?)await _ctx.Rpc.InvokeAsync(method, p, ct).ConfigureAwait(false);
            return node is T typed ? typed : (T)(object)node!.DeepClone();
        }
        catch (RpcException ex)
        {
            var hint = ex.Code == "not_found" ? " Use action list for the ids." : "";
            throw new ToolCallError(ex.Message + hint);
        }
    }

    private const int MaxList = 100;

    /// <summary>A refused call: <c>ExecuteAsync</c>'s catch turns it into the model's error text.</summary>
    private sealed class ToolCallError(string message) : Exception(message);
}
