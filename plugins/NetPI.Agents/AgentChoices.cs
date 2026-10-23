using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Agents;

/// <summary>
/// <c>agent_choices</c>: the agents the user set up, as an agent that delegates sees them. First the budget, then the
/// agents that can take work (free before paid, cheapest first: id, model, busy/instances, state, price, today's spend,
/// context window, the user's note on when to use it), then other model calls in progress. Switched-off agents and
/// agents whose model isn't loaded or reachable are hidden: they can't take work, so they are not a choice (their state
/// stays visible in Settings → Agents and the Work tab, and a spawn that tries anyway is refused with the reasons).
/// Registered by the agents plugin, so it disappears (with the agents section of the system prompt) when the plugin is off.
/// </summary>
internal sealed class AgentChoicesTool(AgentScheduler scheduler, Ledger ledger) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "agent_choices",
        Label = "Agents",
        Description =
            "List the agents the user set up that can take work: each is a model with a number of instances (runs at once), " +
            "its state (ready, busy), a note on when to use it, its price and today's spend; and the budget. Switched-off " +
            "agents and agents whose model isn't loaded or reachable are not listed. Pass an agent's id to agent_spawn, or " +
            "\"any\" to let the first agent with a free instance take the subagent. Also shows who runs on them.",
        ReadOnly = true,
        Category = "agents",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var pools = scheduler.Snapshot();
        var runtime = context.Services.Get<IAgentRuntime>();
        var names = runtime?.List(true).ToDictionary(a => a.Id, a => a.Name) ?? [];
        string Who(SlotHolder o) => (names.TryGetValue(o.AgentId, out var n) ? n : o.Label ?? "?") + (o.AgentId == context.AgentId ? " (you)" : "");

        var sb = new StringBuilder(ledger.BudgetLine()).Append("\n\n");
        if (runtime is null) sb.Append("Delegation unavailable: no executor capability is registered. Scheduling and budgets remain available.\n\n");
        sb.Append("Shared model capacity (several agent names share these totals):\n");
        foreach (var resource in scheduler.Resources())
            sb.Append("- ").Append(resource.Model ?? resource.Key).Append(": ").Append(resource.Capacity).Append(" total, ").Append(resource.Busy).Append(" running, ").Append(resource.Queued).Append(" queued\n");
        var agents = pools.Where(p => p.Configured)
            // switched off or the model isn't loaded/reachable: hidden, they can't take work (a spawn that tries is refused with the reasons)
            .Where(p => p.Available)
            .OrderBy(p => p.Free ? 0 : p.PriceInput is null ? 2 : 1)
            .ThenBy(p => (p.PriceInput ?? 0) + (p.PriceOutput ?? 0))
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var others = pools.Where(p => !p.Configured && (p.Busy > 0 || p.Queued > 0)).ToList();
        var allConfigured = pools.Count(p => p.Configured);
        if (agents.Count > 0)
        {
            sb.Append(runtime is null ? "Configured agents (execution unavailable):\n" : "Agents (pass the id to agent_spawn, or \"any\" for the first one with a free instance):\n");
            foreach (var p in agents) Line(sb, p, withModels: false, Who, context.AgentId);
            if (others.Count > 0)
            {
                sb.Append("\nOther model calls in progress (not agents):\n");
                foreach (var p in others) Line(sb, p, withModels: true, Who, context.AgentId);
            }
        }
        else if (allConfigured > 0)
        {
            // agents are set up but every one is hidden: say so without listing them; a spawn that tries gets the reasons
            sb.Append("Every agent is switched off or its model isn't loaded or reachable: no agent can take work right now.\n");
            if (others.Count > 0)
            {
                sb.Append("\nOther model calls in progress (not agents):\n");
                foreach (var p in others) Line(sb, p, withModels: true, Who, context.AgentId);
            }
        }
        else
        {
            sb.Append(runtime is null ? "No agents are set up.\n" : "No agents are set up: a subagent runs on your model unless you pass agent_spawn a model ref.\n");
            if (others.Count > 0) sb.Append("Model calls in progress (busy/slots):\n");
            foreach (var p in others) Line(sb, p, withModels: true, Who, context.AgentId);
        }
        if (scheduler.Unassigned() is { Count: > 0 } floating)
            sb.Append("\nWaiting for any free agent: ").Append(string.Join(", ", floating.Select(Who))).Append('\n');
        // the caller's own agent line stays even while that agent is hidden (it may have been switched off mid-run)
        if (runtime?.Get(context.AgentId)?.Agent is { } mine && pools.Any(p => p.Configured && p.Key == mine)) sb.Append("\nYou run on the agent ").Append(mine).Append('.');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject
        {
            ["agents"] = NetPiJson.ToNode(pools),
            ["resources"] = NetPiJson.ToNode(scheduler.Resources()),
            ["budget"] = ledger.BudgetStatus(),
        }));
    }

    private void Line(StringBuilder sb, AgentSlots p, bool withModels, Func<SlotHolder, string> who, string you)
    {
        sb.Append("- ").Append(p.Key);
        if (p.Configured && p.Model is not null) sb.Append(" · ").Append(p.Model);
        sb.Append(" · ").Append(p.Busy).Append('/').Append(p.Capacity).Append(" busy");
        // the caller's own instance is free for its subagents while it waits
        if (p.Owners.Any(o => o.AgentId == you)) sb.Append(" (one is you: free for your subagents while you wait)");
        if (p.Queued > 0) sb.Append(", ").Append(p.Queued).Append(" queued");
        var model = scheduler.ModelInfo(p.Model ?? (p.Models.Count == 1 ? p.Models[0] : null));
        if (model?.IsLocal == true) sb.Append(" · local");
        if (p.Free) sb.Append(" · free");
        else if (p.PriceInput is { } pi && p.PriceOutput is { } po) sb.Append(" · ").Append(Ledger.Usd(pi)).Append(" / ").Append(Ledger.Usd(po)).Append(" per Mtok in/out");
        else if (p.Configured || model is not null) sb.Append(" · price unknown");
        if (!p.Free && (p.SpentTodayUsd > 0 || p.DailyLimitUsd is not null))
        {
            sb.Append(" · ").Append(Ledger.Usd(p.SpentTodayUsd)).Append(" today");
            if (p.DailyLimitUsd is { } cap) sb.Append(" (cap ").Append(Ledger.Usd(cap)).Append(')');
        }
        if (model?.ContextWindow is { } ctx) sb.Append(" · ").Append(ctx >= 1000 ? (ctx / 1000).ToString(CultureInfo.InvariantCulture) + "k" : ctx.ToString(CultureInfo.InvariantCulture)).Append(" ctx");
        if (model?.SupportsImages == true) sb.Append(" · images");
        if (!string.IsNullOrWhiteSpace(p.Use)) sb.Append(" · \"").Append(p.Use.Trim()).Append('"');
        if (withModels && p.Models.Count > 0)
        {
            var models = p.Models.Count > 8 ? string.Join(", ", p.Models.Take(8)) + $", … (+{p.Models.Count - 8})" : string.Join(", ", p.Models);
            sb.Append("\n  models: ").Append(models);
        }
        if (p.Owners.Count > 0) sb.Append("\n  running: ").Append(string.Join(", ", p.Owners.Select(who)));
        if (p.Waiters.Count > 0) sb.Append("\n  waiting: ").Append(string.Join(", ", p.Waiters.Select(who)));
        sb.Append('\n');
    }
}

/// <summary>
/// How to choose an agent, for agents that can start subagents (order 300). Contributed only while the agents plugin
/// runs. What agents are and how instances work is in the agent_spawn and agent_choices definitions, and agent_choices
/// marks the caller's own instance, which is free for its subagents while it waits.
/// </summary>
internal sealed class AgentsPromptSection : IPromptSection
{
    public string Id => "agents";
    public int Order => 300;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        // a deferred agent_spawn is still one call away: the guidance holds for the call by name
        bool Has(string name) => c.Tools.Concat(c.DeferredTools).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!Has("agent_spawn")) return ValueTask.FromResult<string?>(null);
        return ValueTask.FromResult<string?>("# Agents\n- " +
            (Has("agent_choices") ? "Before you delegate, look at agent_choices and choose" : "Choose") +
            " an active agent by its note and cost: prefer free ones; a paid agent spends the user's money, so use it only " +
            "when the task needs what it is good at, and above the budget's warning level only when the user asked.\n" +
            "- With two local slots: everyday work is you plus one independent helper. For a batch, spawn two workers and wait so you yield your slot; you resume with priority. An explicitly chosen cloud coordinator may supervise both local workers. More agent names or instances do not add physical model capacity. Busy work queues by priority: a subagent starts at normal at most (your level when lower), so spawn work that can wait with priority low (or background, to give way to any other run), and pass a level up to your own only when the work truly outruns the rest. Active inference is not preempted; a critical run (an alert) may take a subagent's instance between its model calls, and that subagent then goes on when one frees. Never change model or spend money just because local slots are busy.\n" +
            "- Give each child a self-contained goal, relevant paths/evidence, constraints and file ownership. A writing child uses an isolated workspace; use bounded independent assignments, wait for reports, and integrate commits deliberately. Request a final report with results, changed paths, validation and blockers. Fresh context is the default; tools are chosen by the parent, profiles and budget overrides are not inherited. Background work is useful when you have independent work to do; otherwise wait and release capacity.");
    }
}
