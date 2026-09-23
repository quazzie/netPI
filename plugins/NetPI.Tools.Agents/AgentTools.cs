using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Agents;

/// <summary>Shared helpers for the agent orchestration tools. Services are resolved per call (plugins reload).</summary>
internal abstract class AgentToolBase(IPluginContext plugin) : IAgentTool
{
    public const int ReportChars = 12_000;

    protected IPluginContext Plugin { get; } = plugin;
    public abstract ToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var runtime = context.Services.Get<IAgentRuntime>();
        if (runtime is null) return ToolResult.Error("The agent runtime is not available (the netpi.agent plugin is not loaded).");
        try
        {
            return await RunAsync(runtime, context, ToolArgs.Unwrap(args), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or BudgetExceededException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    protected abstract Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct);

    protected static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return o;
    }

    protected static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    protected static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    protected static string Status(AgentStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    protected static JsonObject Details(AgentInfo a) => new()
    {
        ["agentId"] = a.Id,
        ["sessionId"] = a.SessionId,
        ["name"] = a.Name,
        ["status"] = Status(a.Status),
    };

    protected static bool IsBusy(AgentStatus s) => s is AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded;

    protected static string Truncate(string? text, int max, string note)
    {
        text ??= "";
        if (text.Length <= max) return text;
        return text[..max] + $"\n[... truncated: {text.Length - max:N0} more characters. {note}]";
    }

    protected static string Tokens(long n) => n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    protected static string Duration(AgentInfo a)
    {
        if (a.StartedAt is not { } start) return "";
        var end = a.FinishedAt ?? DateTimeOffset.UtcNow;
        var d = end - start;
        return d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.#}s" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m{d.Seconds:00}s" : $"{(int)d.TotalHours}h{d.Minutes:00}m";
    }

    protected static string Stats(AgentInfo a) =>
        $"{a.Turns} turns, {a.ToolCalls} tool calls, {Tokens(a.InputTokens)} in / {Tokens(a.OutputTokens)} out" +
        (Duration(a) is { Length: > 0 } d ? $", {d}" : "");

    /// <summary>Resolve an agent by id, session id, or name (the caller's children first).</summary>
    protected static AgentInfo? Resolve(IAgentRuntime runtime, string callerId, string idOrName)
    {
        idOrName = idOrName.Trim();
        if (runtime.Get(idOrName) is { } a) return a;
        if (runtime.GetBySession(idOrName) is { } b) return b;
        var all = runtime.List(true);
        return all.FirstOrDefault(x => x.ParentAgentId == callerId && string.Equals(x.Name, idOrName, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(x => string.Equals(x.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    protected static string Report(AgentInfo a)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(a.Name).Append(" (").Append(a.Id).Append(") — ");
        if (IsBusy(a.Status))
        {
            sb.Append("still ").Append(Status(a.Status));
            if (!string.IsNullOrEmpty(a.Activity)) sb.Append(" (").Append(a.Activity).Append(')');
            sb.Append("\nCall agent_wait again to keep waiting, or agent_cancel to stop it.");
            return sb.ToString();
        }
        sb.Append(Status(a.Status)).Append(" [").Append(Stats(a)).Append("]\n");
        if (!string.IsNullOrEmpty(a.Error) && a.Status != AgentStatus.Completed) sb.Append("Error: ").Append(a.Error).Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(a.Result)
            ? "(no final report)"
            : Truncate(a.Result.Trim(), ReportChars, $"The full report is in session {a.SessionId}."));
        return sb.ToString();
    }

    protected LanePoolInfo? PoolOf(ToolContext context, AgentInfo a)
    {
        var pools = context.Services.Get<ILaneScheduler>()?.Snapshot();
        if (pools is null) return null;
        if (a.Pool is not null && pools.FirstOrDefault(p => p.Key == a.Pool) is { } byKey) return byKey;
        var model = a.Model ?? Plugin.Models.DefaultModelRef;
        return model is null ? null : pools.FirstOrDefault(p => p.Models.Contains(model, StringComparer.OrdinalIgnoreCase));
    }

    // Tool-specific tips only: the context plugin's guidelines section explains lanes and delegation in general.
    protected static readonly string[] SpawnGuidelines =
    [
        "agent_spawn: the task must be complete and self-contained (goal, paths, constraints, what to report): the subagent does not see your conversation. Pick the model/pool with lanes_list when several are available.",
    ];
}

// ------------------------------------------------------------------ lanes_list

internal sealed class LanesListTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "lanes_list",
        Label = "Lanes",
        Description = "List the lane pools: each model pool has a fixed number of lanes (parallel agent slots). Shows busy/capacity, queued agents, the models of each pool and who holds its lanes. Use it before spawning subagents to pick a pool with free lanes.",
        ReadOnly = true,
        Category = "agents",
        Parameters = Schema(new JsonObject()),
    };

    protected override Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var scheduler = context.Services.Get<ILaneScheduler>();
        if (scheduler is null)
            return Task.FromResult(ToolResult.Ok("Lanes are not available (the lanes plugin is not loaded): agents run without lane limits."));
        var pools = scheduler.Snapshot();
        var me = runtime.Get(context.AgentId);
        var names = runtime.List(true).ToDictionary(a => a.Id, a => a.Name);
        string Who(LaneOwnerInfo o) => (names.TryGetValue(o.AgentId, out var n) ? n : o.Label ?? "?") + (o.AgentId == context.AgentId ? " (you)" : "");

        var sb = new StringBuilder();
        if (pools.Count == 0) sb.Append("No lane pools are known yet.");
        else sb.Append("Lane pools (busy/capacity):\n");
        foreach (var p in pools)
        {
            sb.Append("- ").Append(p.Key).Append(": ").Append(p.Busy).Append('/').Append(p.Capacity).Append(" busy");
            if (p.Queued > 0) sb.Append(", ").Append(p.Queued).Append(" queued");
            if (!string.IsNullOrEmpty(p.Status)) sb.Append(" [").Append(p.Status).Append(']');
            if (p.Models.Count > 0)
            {
                var models = p.Models.Count > 8 ? string.Join(", ", p.Models.Take(8)) + $", … (+{p.Models.Count - 8})" : string.Join(", ", p.Models);
                sb.Append("\n  models: ").Append(models);
            }
            if (p.Owners.Count > 0) sb.Append("\n  running: ").Append(string.Join(", ", p.Owners.Select(Who)));
            if (p.Waiters.Count > 0) sb.Append("\n  waiting: ").Append(string.Join(", ", p.Waiters.Select(Who)));
            sb.Append('\n');
        }
        if (me?.Pool is { } myPool) sb.Append("\nYou run on pool ").Append(myPool).Append('.');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject { ["pools"] = NetPiJson.ToNode(pools) }));
    }
}

// ------------------------------------------------------------------ agent_spawn

internal sealed class AgentSpawnTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_spawn",
        Label = "Spawn agent",
        Description = "Start a subagent on a task. It gets its own session, runs in the background on a lane of its model's pool, and ends with a final report that is returned to you. Returns immediately unless wait=true.",
        Category = "agents",
        SummaryArg = "name",
        PromptGuidelines = SpawnGuidelines,
        Parameters = Schema(new JsonObject
        {
            ["task"] = Prop("string", "Complete, self-contained task description (the subagent does not see your conversation): goal, relevant paths and context, constraints, and what to put in the final report."),
            ["name"] = Prop("string", "Short name for the subagent, e.g. \"tests\" or \"api-research\"."),
            ["model"] = Prop("string", "Model ref \"provider/model\" or a lane pool key (see lanes_list). Default: your model."),
            ["tools"] = StringArray("Restrict the subagent to these tool names. Default: all tools."),
            ["instructions"] = Prop("string", "Extra instructions appended to the subagent's system prompt."),
            ["wait"] = Prop("boolean", "Block until the subagent finishes and return its report (yields your lane while waiting). Default false."),
            ["timeoutSeconds"] = Prop("integer", "With wait=true: maximum seconds to wait."),
        }, "task"),
    };

    protected override async Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var task = ToolArgs.Str(args, "task", "prompt", "description", "message");
        if (string.IsNullOrWhiteSpace(task)) return ToolResult.Error("Missing 'task': describe the subagent's task completely.");
        var info = await runtime.SpawnAsync(new SpawnRequest
        {
            Task = task,
            Name = ToolArgs.Str(args, "name"),
            Model = ToolArgs.Str(args, "model"),
            Tools = ToolArgs.List(args, "tools", "allowedTools"),
            Instructions = ToolArgs.Str(args, "instructions", "systemPrompt"),
            ParentAgentId = context.AgentId,
        }, ct).ConfigureAwait(false);

        if (ToolArgs.Bool(args, "wait") == true)
        {
            var timeout = ToolArgs.Num(args, "timeoutSeconds", "timeout") is { } t && t > 0 ? TimeSpan.FromSeconds(t) : (TimeSpan?)null;
            var results = await runtime.WaitAsync(context.AgentId, [info.Id], yieldLane: true, timeout, ct).ConfigureAwait(false);
            var r = results.FirstOrDefault() ?? runtime.Get(info.Id) ?? info;
            return new ToolResult { Content = Report(r), IsError = r.Status == AgentStatus.Failed, Details = Details(r) };
        }

        var sb = new StringBuilder();
        sb.Append("Spawned subagent \"").Append(info.Name).Append("\" (id ").Append(info.Id).Append(", session ").Append(info.SessionId).Append(')');
        var model = info.Model ?? Plugin.Models.DefaultModelRef;
        if (model is not null) sb.Append(" on ").Append(model);
        if (PoolOf(context, info) is { } pool)
        {
            sb.Append(" — lane pool ").Append(pool.Key).Append(": ").Append(pool.Busy).Append('/').Append(pool.Capacity).Append(" busy");
            if (pool.Queued > 0) sb.Append(", ").Append(pool.Queued).Append(" queued");
        }
        sb.Append(".\nIt works in the background; its final report is delivered to you automatically when it finishes. ")
          .Append("Call agent_wait to block until it is done (this yields your lane).");
        return ToolResult.Ok(sb.ToString(), Details(runtime.Get(info.Id) ?? info));
    }
}

// ------------------------------------------------------------------ agent_wait

internal sealed class AgentWaitTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_wait",
        Label = "Wait for agents",
        Description = "Wait for subagents to finish and return their final reports. While waiting your lane is released (yielded) so other agents, typically the ones you wait for, can use it; afterwards you resume with priority. Without ids it waits for all of your running subagents. A new user message interrupts the wait.",
        Category = "agents",
        PromptGuidelines = ["agent_wait yields your lane while waiting: prefer one agent_wait for all workers over several short waits."],
        Parameters = Schema(new JsonObject
        {
            ["ids"] = StringArray("Agent ids (or names) to wait for. Default: all of your running subagents."),
            ["id"] = Prop("string", "A single agent id (alternative to ids)."),
            ["timeoutSeconds"] = Prop("integer", "Maximum seconds to wait (default 3600). Agents still running are reported as such."),
        }),
    };

    protected override async Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var requested = ToolArgs.List(args, "ids", "agents", "agentIds") ?? ToolArgs.List(args, "id", "agentId", "agent");
        var ids = new List<string>();
        var unknown = new List<string>();
        if (requested is { Count: > 0 })
        {
            foreach (var r in requested)
            {
                if (Resolve(runtime, context.AgentId, r) is { } a) ids.Add(a.Id);
                else unknown.Add(r);
            }
        }
        else
        {
            ids = runtime.List(false).Where(a => a.ParentAgentId == context.AgentId).Select(a => a.Id).ToList();
            if (ids.Count == 0)
            {
                var finished = runtime.List(true).Count(a => a.ParentAgentId == context.AgentId);
                return ToolResult.Ok(finished > 0
                    ? "None of your subagents is running. Their reports were delivered already; use agent_result to read one again."
                    : "You have no subagents. Use agent_spawn to start one.");
            }
        }
        if (ids.Count == 0) return ToolResult.Error($"Unknown agent(s): {string.Join(", ", unknown)}. Use agent_list to see your subagents.");

        var seconds = ToolArgs.Num(args, "timeoutSeconds", "timeout") is { } t && t > 0 ? t : 3600;
        var results = await runtime.WaitAsync(context.AgentId, ids, yieldLane: true, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        var running = results.Count(a => IsBusy(a.Status));
        sb.Append(running == 0
            ? $"{results.Count} agent{(results.Count == 1 ? "" : "s")} finished."
            : $"{results.Count - running} of {results.Count} agents finished; {running} still running (the wait timed out or a new message arrived).");
        foreach (var a in results) sb.Append("\n\n").Append(Report(a));
        if (unknown.Count > 0) sb.Append("\n\nUnknown agent(s): ").Append(string.Join(", ", unknown));
        var arr = new JsonArray();
        foreach (var a in results) arr.Add(Details(a));
        return ToolResult.Ok(sb.ToString(), new JsonObject { ["agents"] = arr });
    }
}

// ------------------------------------------------------------------ agent_send

internal sealed class AgentSendTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_send",
        Label = "Message agent",
        Description = "Send a message to another agent: your parent (to=\"parent\"), one of your subagents (id or name), or any agent id. It arrives as a notice; an idle agent is woken up. mode \"steer\" (default) delivers it at the agent's next step, \"queue\" after its current run.",
        Category = "agents",
        SummaryArg = "to",
        Parameters = Schema(new JsonObject
        {
            ["to"] = Prop("string", "\"parent\", or an agent id or name."),
            ["message"] = Prop("string", "The message."),
            ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("steer", "queue"), ["description"] = "steer (default) or queue." },
        }, "to", "message"),
    };

    protected override async Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var to = ToolArgs.Str(args, "to", "agent", "agentId", "id");
        var message = ToolArgs.Str(args, "message", "text", "content");
        if (string.IsNullOrWhiteSpace(to)) return ToolResult.Error("Missing 'to': \"parent\" or an agent id.");
        if (string.IsNullOrWhiteSpace(message)) return ToolResult.Error("Missing 'message'.");

        AgentInfo? target;
        if (string.Equals(to.Trim(), "parent", StringComparison.OrdinalIgnoreCase))
        {
            var me = runtime.Get(context.AgentId);
            if (me?.ParentAgentId is not { } pid) return ToolResult.Error("You have no parent agent.");
            target = runtime.Get(pid);
            if (target is null) return ToolResult.Error("Your parent agent is no longer available.");
        }
        else
        {
            target = Resolve(runtime, context.AgentId, to);
            if (target is null) return ToolResult.Error($"Unknown agent '{to}'. Use agent_list to see agents.");
        }
        if (target.Id == context.AgentId) return ToolResult.Error("You cannot message yourself.");

        var mode = ToolArgs.Str(args, "mode")?.Trim().ToLowerInvariant() == "queue" ? DeliveryMode.Queue : DeliveryMode.Steer;
        var ok = await runtime.MessageAsync(context.AgentId, target.Id, message, mode, ct).ConfigureAwait(false);
        if (!ok) return ToolResult.Error($"Could not deliver the message to {target.Name} ({target.Id}).");
        var now = runtime.Get(target.Id) ?? target;
        return ToolResult.Ok($"Message delivered to {now.Name} ({now.Id}), status {Status(now.Status)}.", Details(now));
    }
}

// ------------------------------------------------------------------ agent_list

internal sealed class AgentListTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_list",
        Label = "Agents",
        Description = "List your subagents (or all agents with all=true) with status, activity, model, lane pool and usage.",
        ReadOnly = true,
        Category = "agents",
        Parameters = Schema(new JsonObject
        {
            ["all"] = Prop("boolean", "List every agent, not only your subagents."),
        }),
    };

    protected override Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var all = ToolArgs.Bool(args, "all") == true;
        var agents = runtime.List(true).Where(a => all || a.ParentAgentId == context.AgentId).Take(50).ToList();
        if (agents.Count == 0)
            return Task.FromResult(ToolResult.Ok(all ? "No agents." : "You have no subagents. Use agent_spawn to start one.", new JsonObject { ["agents"] = new JsonArray() }));
        var sb = new StringBuilder();
        var arr = new JsonArray();
        foreach (var a in agents)
        {
            sb.Append("- ").Append(a.Name).Append(" (").Append(a.Id).Append(") ").Append(Status(a.Status));
            if (!string.IsNullOrEmpty(a.Activity)) sb.Append(": ").Append(a.Activity);
            if (a.Id == context.AgentId) sb.Append(" [you]");
            sb.Append("\n  ");
            if (a.Model is not null) sb.Append("model ").Append(a.Model).Append(", ");
            if (a.Pool is not null) sb.Append("pool ").Append(a.Pool).Append(", ");
            sb.Append(Stats(a));
            if (a.QueuedMessages > 0) sb.Append(", ").Append(a.QueuedMessages).Append(" queued messages");
            if (!string.IsNullOrWhiteSpace(a.Task)) sb.Append("\n  task: ").Append(Truncate(a.Task.ReplaceLineEndings(" "), 160, "…"));
            sb.Append('\n');
            arr.Add(Details(a));
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject { ["agents"] = arr }));
    }
}

// ------------------------------------------------------------------ agent_result

internal sealed class AgentResultTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_result",
        Label = "Agent result",
        Description = "Get a subagent's status and final report (its last message) without waiting.",
        ReadOnly = true,
        Category = "agents",
        SummaryArg = "id",
        Parameters = Schema(new JsonObject { ["id"] = Prop("string", "Agent id or name.") }, "id"),
    };

    protected override Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var id = ToolArgs.Str(args, "id", "agentId", "agent", "name");
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(ToolResult.Error("Missing 'id'."));
        var a = Resolve(runtime, context.AgentId, id);
        if (a is null) return Task.FromResult(ToolResult.Error($"Unknown agent '{id}'. Use agent_list to see agents."));
        var text = Report(a);
        if (IsBusy(a.Status) && !string.IsNullOrWhiteSpace(a.Result))
            text += "\n\nLast report of a previous run:\n" + Truncate(a.Result.Trim(), ReportChars, $"See session {a.SessionId}.");
        return Task.FromResult(ToolResult.Ok(text, Details(a)));
    }
}

// ------------------------------------------------------------------ agent_cancel

internal sealed class AgentCancelTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_cancel",
        Label = "Cancel agent",
        Description = "Cancel a running subagent (its own subagents are cancelled too). Its partial work stays in its session.",
        Category = "agents",
        SummaryArg = "id",
        Parameters = Schema(new JsonObject { ["id"] = Prop("string", "Agent id or name.") }, "id"),
    };

    protected override async Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        var id = ToolArgs.Str(args, "id", "agentId", "agent", "name");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("Missing 'id'.");
        var a = Resolve(runtime, context.AgentId, id);
        if (a is null) return ToolResult.Error($"Unknown agent '{id}'. Use agent_list to see agents.");
        for (var cur = runtime.Get(context.AgentId); cur is not null; cur = cur.ParentAgentId is { } p ? runtime.Get(p) : null)
            if (cur.Id == a.Id) return ToolResult.Error("You cannot cancel yourself or one of your parent agents.");
        if (!IsBusy(a.Status)) return ToolResult.Ok($"{a.Name} ({a.Id}) is not running (status {Status(a.Status)}).", Details(a));
        var ok = await runtime.AbortAsync(a.Id).ConfigureAwait(false);
        // Consume the result: the caller asked for the cancellation, so it needs no agent-result notice about it.
        var final = await runtime.WaitAsync(context.AgentId, [a.Id], yieldLane: false, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var now = final.FirstOrDefault() ?? runtime.Get(a.Id) ?? a;
        return ok
            ? ToolResult.Ok($"Cancelled {now.Name} ({now.Id}); status {Status(now.Status)}.", Details(now))
            : ToolResult.Error($"{now.Name} ({now.Id}) could not be cancelled (status {Status(now.Status)}).", Details(now));
    }
}
