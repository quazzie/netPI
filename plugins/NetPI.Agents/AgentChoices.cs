using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Agents;

/// <summary>
/// <c>agent_choices</c>: the agents the user set up, as an agent that delegates sees them. First the budget, then the
/// agents (active first, then cheapest: id, model, busy/instances, state, price, today's spend, context window, the user's
/// note on when to use it), then other model calls in progress. Registered by the agents plugin, so it disappears (with
/// the agents section of the system prompt) when the plugin is off.
/// </summary>
internal sealed class AgentChoicesTool(AgentScheduler scheduler, Ledger ledger) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "agent_choices",
        Label = "Agents",
        Description =
            "List the agents the user set up to run subagents on: each is a model with a number of instances (runs at once), " +
            "its state (active, busy, not loaded, switched off), a note on when to use it, its price and today's spend; and " +
            "the budget. Pass an agent's id to agent_spawn. Also shows who runs on them.",
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
        var agents = pools.Where(p => p.Configured)
            .OrderBy(p => p.Available ? 0 : 1)
            .ThenBy(p => p.Free ? 0 : p.PriceInput is null ? 2 : 1)
            .ThenBy(p => (p.PriceInput ?? 0) + (p.PriceOutput ?? 0))
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var others = pools.Where(p => !p.Configured && (p.Busy > 0 || p.Queued > 0)).ToList();
        if (agents.Count > 0)
        {
            sb.Append("Agents (pass the id to agent_spawn):\n");
            foreach (var p in agents) Line(sb, p, withModels: false, Who, context.AgentId);
            if (others.Count > 0)
            {
                sb.Append("\nOther model calls in progress (not agents):\n");
                foreach (var p in others) Line(sb, p, withModels: true, Who, context.AgentId);
            }
        }
        else
        {
            sb.Append("No agents are set up: a subagent runs on your model unless you pass agent_spawn a model ref.\n");
            if (others.Count > 0) sb.Append("Model calls in progress (busy/slots):\n");
            foreach (var p in others) Line(sb, p, withModels: true, Who, context.AgentId);
        }
        if (runtime?.Get(context.AgentId)?.Agent is { } mine && agents.Any(p => p.Key == mine)) sb.Append("\nYou run on the agent ").Append(mine).Append('.');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject
        {
            ["agents"] = NetPiJson.ToNode(pools),
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
        if (p.Configured && !p.Available) sb.Append(" · NOT ACTIVE: ").Append(p.Disabled ? "switched off by the user" : p.Unavailable);
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
        bool Has(string name) => c.Tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!Has("agent_spawn")) return ValueTask.FromResult<string?>(null);
        return ValueTask.FromResult<string?>("# Agents\n- " +
            (Has("agent_choices") ? "Before you delegate, look at agent_choices and choose" : "Choose") +
            " an active agent by its note and cost: prefer free ones; a paid agent spends the user's money, so use it only " +
            "when the task needs what it is good at, and above the budget's warning level only when the user asked.");
    }
}
