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
        if (runtime is null) return ToolResult.Error("The agent runtime is not available (the netpi.runtime plugin is not loaded).");
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

    protected AgentSlots? PoolOf(ToolContext context, AgentInfo a)
    {
        var pools = context.Services.Get<IAgentScheduler>()?.Snapshot();
        if (pools is null) return null;
        if (a.Agent is not null && pools.FirstOrDefault(p => p.Key == a.Agent) is { } byKey) return byKey;
        var model = a.Model ?? Plugin.Models.DefaultModelRef;
        return model is null ? null : pools.FirstOrDefault(p => p.Models.Contains(model, StringComparer.OrdinalIgnoreCase));
    }

    // When to delegate lives with the tools (only agents that have them see it); how the tools work is in their
    // definitions, and the agents plugin says how to choose an agent.
    protected static readonly string[] SpawnGuidelines =
    [
        "Delegate independent, well-scoped work (research, exploring code, separate modules) to subagents with agent_spawn; don't delegate what you can do in a couple of tool calls.",
        "Start subagents that should run at the same time in one agent_spawn call: separate calls run one after the other. Pass background: true only when you have other work to do meanwhile.",
    ];
}

// ------------------------------------------------------------------ agent_spawn

internal sealed class AgentSpawnTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    private static JsonObject ItemProperties() => new()
    {
        ["task"] = Prop("string", "Complete, self-contained task description (the subagent does not see your conversation): goal, relevant paths and context, constraints, and what to put in the final report."),
        ["name"] = Prop("string", "Short name for the subagent, e.g. \"tests\" or \"api-research\"."),
        ["agent"] = Prop("string", "The agent to run on: an id from agent_choices (required when the user has set up agents). On a busy agent the subagent waits for a free instance."),
        ["model"] = Prop("string", "Only when no agents are set up: a model ref \"provider/model\". Default: your model."),
        ["tools"] = StringArray("The subagent's tools, by name; they may include tools you do not have yourself (e.g. give a remote-work agent the ssh_* tools). Default: the tools you have."),
        ["instructions"] = Prop("string", "Extra instructions appended to the subagent's system prompt."),
    };

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_spawn",
        Label = "Spawn agent",
        Description = "Start subagents on tasks, each on one of the agents the user set up (a model with instances; see agent_choices). Each gets its own session and ends with a final report. " +
                      "One subagent: pass task (and agent, name, …). Several at once: pass them in subagents; they all start together. " +
                      "Waits until all of them finish and returns every report; your own instance is free for them meanwhile. background: true returns at once instead (the reports arrive later).",
        Category = "agents",
        SummaryArg = "name",
        PromptGuidelines = SpawnGuidelines,
        Parameters = Schema(new JsonObject(ItemProperties().Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
        {
            ["subagents"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Several subagents to start together, each { task, name?, agent?, model?, tools?, instructions? } (instead of the single task above).",
                ["items"] = Schema(ItemProperties(), "task"),
            },
            ["background"] = Prop("boolean", "true: return at once; each report arrives later on its own (or collect them with agent_wait). Default false: wait for all of them."),
            ["timeoutSeconds"] = Prop("integer", "When waiting: maximum seconds to wait (the ones still running then report later on their own)."),
        }),
    };

    protected override async Task<ToolResult> RunAsync(IAgentRuntime runtime, ToolContext context, JsonElement args, CancellationToken ct)
    {
        // one subagent (the arguments themselves) or several (subagents); all are checked before any starts
        var batch = ToolArgs.Get(args, "subagents", "agents", "tasks") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(ToolArgs.Unwrap).ToList()
            : null;
        if (batch is { Count: 0 }) return ToolResult.Error("subagents is empty: give each subagent a task.");
        var items = batch ?? [args];
        var agents = (context.Services.Get<IAgentScheduler>()?.Snapshot() ?? []).Where(p => p.Configured).ToList();
        var requests = new List<SpawnRequest>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var (request, error) = Prepare(items[i], agents, context);
            if (error is not null)
                return ToolResult.Error(batch is null ? error : $"subagents[{i}]{(ToolArgs.Str(items[i], "name") is { } n ? $" ({n})" : "")}: {error}\nNone of them was started.");
            requests.Add(request!);
        }

        var started = new List<AgentInfo>(requests.Count);
        foreach (var request in requests) started.Add(await runtime.SpawnAsync(request, ct).ConfigureAwait(false));

        // waiting is the default; background is the explicit choice (an older "wait": false means background too)
        var background = ToolArgs.Bool(args, "background") ?? (ToolArgs.Bool(args, "wait") is { } w ? !w : false);
        if (!background)
        {
            var timeout = ToolArgs.Num(args, "timeoutSeconds", "timeout") is { } t && t > 0 ? TimeSpan.FromSeconds(t) : (TimeSpan?)null;
            var results = await runtime.WaitAsync(context.AgentId, [.. started.Select(a => a.Id)], yieldSlot: true, timeout, ct).ConfigureAwait(false);
            if (batch is null)
            {
                var r = results.FirstOrDefault() ?? runtime.Get(started[0].Id) ?? started[0];
                return new ToolResult { Content = Report(r), IsError = r.Status == AgentStatus.Failed, Details = Details(r) };
            }
            var running = results.Count(a => IsBusy(a.Status));
            var report = new StringBuilder(running == 0
                ? $"{results.Count} subagents finished."
                : $"{results.Count - running} of {results.Count} subagents finished; {running} still running (the wait timed out or a new message arrived).");
            foreach (var a in results) report.Append("\n\n").Append(Report(a));
            return ToolResult.Ok(report.ToString(), new JsonObject { ["agents"] = new JsonArray([.. results.Select(a => (JsonNode)Details(a))]) });
        }

        var sb = new StringBuilder();
        foreach (var a in started)
        {
            var info = runtime.Get(a.Id) ?? a;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Spawned subagent \"").Append(info.Name).Append("\" (id ").Append(info.Id).Append(", session ").Append(info.SessionId).Append(')');
            var model = info.Model ?? Plugin.Models.DefaultModelRef;
            if (model is not null) sb.Append(" on ").Append(model);
            if (PoolOf(context, info) is { } pool)
            {
                sb.Append(pool.Configured ? " — agent " : " — ").Append(pool.Key).Append(": ").Append(pool.Busy).Append('/').Append(pool.Capacity).Append(" busy");
                if (pool.Queued > 0) sb.Append(", ").Append(pool.Queued).Append(" queued");
            }
            sb.Append('.');
        }
        sb.Append(started.Count == 1
            ? "\nIt works in the background; its final report arrives on its own when it finishes. "
            : "\nThey work in the background; each final report arrives on its own when it finishes. ")
          .Append("To wait for them instead, call agent_wait (your instance is free for them meanwhile).");
        return started.Count == 1
            ? ToolResult.Ok(sb.ToString(), Details(runtime.Get(started[0].Id) ?? started[0]))
            : ToolResult.Ok(sb.ToString(), new JsonObject { ["agents"] = new JsonArray([.. started.Select(a => (JsonNode)Details(runtime.Get(a.Id) ?? a))]) });
    }

    /// <summary>One subagent's request, or why it can't start.</summary>
    private (SpawnRequest? Request, string? Error) Prepare(JsonElement item, List<AgentSlots> agents, ToolContext context)
    {
        var task = ToolArgs.Str(item, "task", "prompt", "description", "message");
        if (string.IsNullOrWhiteSpace(task)) return (null, "Missing 'task': describe the subagent's task completely.");

        // the agents the user set up are the menu: one of them is required (an active one); without any, a model ref or your model
        var agentArg = ToolArgs.Str(item, "agent");
        var modelArg = ToolArgs.Str(item, "model");
        string? spawnAgent = null, spawnModel;
        if (agents.Count > 0)
        {
            var wanted = agentArg ?? modelArg;
            // by id; a model ref takes an agent on that model (an active, free one first)
            var agent = wanted is null ? null
                : agents.FirstOrDefault(p => string.Equals(p.Key, wanted, StringComparison.OrdinalIgnoreCase))
                  ?? agents.Where(p => string.Equals(p.Model, wanted, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(p => p.Available ? 0 : 1).ThenBy(p => p.Busy + p.Queued).FirstOrDefault();
            if (agent is null)
                return (null, (wanted is null ? "agent_spawn needs an agent." : $"There is no agent \"{wanted}\".") +
                              " The agents:\n" + AgentMenu(agents) + "\nPass one of these ids as agent (agent_choices also shows the budget).");
            if (!agent.Available)
                return (null, $"The agent \"{agent.Key}\" can't take work now: {(agent.Disabled ? "the user switched it off" : agent.Unavailable)}. " +
                              "The agents:\n" + AgentMenu(agents) + "\nChoose an active one.");
            spawnAgent = agent.Key;
            spawnModel = agent.Model;
        }
        else spawnModel = agentArg ?? modelArg;

        // the caller may not see the tools it hands out (a limited orchestrator): unknown names get the list
        var tools = ToolArgs.List(item, "tools", "allowedTools");
        if (tools is { Count: > 0 })
        {
            var all = Plugin.Tools.All;
            var known = new HashSet<string>(all.Select(t => t.Definition.Name), StringComparer.OrdinalIgnoreCase);
            var unknown = tools.Where(n => !known.Contains(n.Trim())).ToList();
            if (unknown.Count > 0)
                return (null, $"Unknown tool{(unknown.Count > 1 ? "s" : "")}: {string.Join(", ", unknown)}. The tools:\n" +
                    string.Join("\n", all.GroupBy(t => t.Definition.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
                        .Select(g => $"- {g.Key}: {string.Join(", ", g.Select(t => t.Definition.Name).Distinct().Order(StringComparer.Ordinal))}")));
        }

        return (new SpawnRequest
        {
            Task = task,
            Name = ToolArgs.Str(item, "name"),
            Agent = spawnAgent,
            Model = spawnModel,
            Tools = tools,
            Instructions = ToolArgs.Str(item, "instructions", "systemPrompt"),
            ParentAgentId = context.AgentId,
        }, null);
    }

    private static string AgentMenu(IEnumerable<AgentSlots> agents) => string.Join("\n", agents.OrderBy(p => p.Available ? 0 : 1).Select(p =>
        $"- {p.Key} · {p.Model} · " +
        (p.Available ? $"{p.Busy}/{p.Capacity} busy · " : $"not active ({(p.Disabled ? "switched off" : p.Unavailable)}) · ") +
        (p.Free ? "free"
            : p.PriceInput is { } pi && p.PriceOutput is { } po
                ? $"${pi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} / ${po.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} per Mtok"
                : "price unknown") +
        (string.IsNullOrWhiteSpace(p.Use) ? "" : $" · \"{p.Use.Trim()}\"")));
}

// ------------------------------------------------------------------ agent_wait

internal sealed class AgentWaitTool(IPluginContext plugin) : AgentToolBase(plugin)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "agent_wait",
        Label = "Wait for agents",
        Description = "Wait for subagents to finish and return their final reports. While waiting your instance is released (yielded) so other runs, typically the ones you wait for, can use it; afterwards you resume with priority. Without ids it waits for all of your running subagents and also returns the reports that arrived while you were busy and you have not seen yet. A new user message interrupts the wait.",
        Category = "agents",
        Parameters = Schema(new JsonObject
        {
            ["ids"] = StringArray("Agent ids (or names) to wait for. Default: all of your running subagents, plus finished ones whose report you have not seen."),
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
            // the running ones, and the finished ones whose report waits in your queue unseen (it comes back here instead)
            var unseen = runtime.GetQueue(context.SessionId)
                .Where(q => q.Source.StartsWith("agent:", StringComparison.Ordinal) && q.Text.StartsWith("<agent-result", StringComparison.Ordinal))
                .Select(q => q.Source["agent:".Length..])
                .ToHashSet(StringComparer.Ordinal);
            ids = runtime.List(true)
                .Where(a => a.ParentAgentId == context.AgentId && (IsBusy(a.Status) || unseen.Contains(a.Id)))
                .Select(a => a.Id)
                .ToList();
            if (ids.Count == 0)
            {
                var finished = runtime.List(true).Count(a => a.ParentAgentId == context.AgentId);
                return ToolResult.Ok(finished > 0
                    ? "None of your subagents is running, and you have seen all their reports; use agent_result to read one again."
                    : "You have no subagents. Use agent_spawn to start one.");
            }
        }
        if (ids.Count == 0) return ToolResult.Error($"Unknown agent(s): {string.Join(", ", unknown)}. Use agent_list to see your subagents.");

        var seconds = ToolArgs.Num(args, "timeoutSeconds", "timeout") is { } t && t > 0 ? t : 3600;
        var results = await runtime.WaitAsync(context.AgentId, ids, yieldSlot: true, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);

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
        Description = "List your subagents (or all running agents with all=true) with status, activity, model, the agent they run on and usage.",
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
            if (a.Agent is not null) sb.Append("agent ").Append(a.Agent).Append(", ");
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
        var final = await runtime.WaitAsync(context.AgentId, [a.Id], yieldSlot: false, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var now = final.FirstOrDefault() ?? runtime.Get(a.Id) ?? a;
        return ok
            ? ToolResult.Ok($"Cancelled {now.Name} ({now.Id}); status {Status(now.Status)}.", Details(now))
            : ToolResult.Error($"{now.Name} ({now.Id}) could not be cancelled (status {Status(now.Status)}).", Details(now));
    }
}
