using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Lanes;

/// <summary>
/// <c>lanes_list</c>: the lanes as an agent sees them. First the budget, then the lanes the user set up (cheapest first:
/// id, model, busy/capacity, price, today's spend, context window, the user's note on when to use it), then other models
/// in use. Registered by the lanes plugin, so it disappears (with the lanes section of the system prompt) when lanes are
/// disabled.
/// </summary>
internal sealed class LanesListTool(LaneScheduler scheduler, Ledger ledger) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "lanes_list",
        Label = "Lanes",
        Description =
            "List the lanes: the models the user set up for agents, each with its parallel slots (busy/capacity), a note on " +
            "when to use it, its price and today's spend, cheapest first; and the budget. Pass a lane's id to agent_spawn. " +
            "Also shows who holds the lanes.",
        ReadOnly = true,
        Category = "agents",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var pools = scheduler.Snapshot();
        var runtime = context.Services.Get<IAgentRuntime>();
        var names = runtime?.List(true).ToDictionary(a => a.Id, a => a.Name) ?? [];
        string Who(LaneOwnerInfo o) => (names.TryGetValue(o.AgentId, out var n) ? n : o.Label ?? "?") + (o.AgentId == context.AgentId ? " (you)" : "");

        var sb = new StringBuilder(ledger.BudgetLine()).Append("\n\n");
        var configured = pools.Where(p => p.Configured)
            .OrderBy(p => p.Free ? 0 : p.PriceInput is null ? 2 : 1)
            .ThenBy(p => (p.PriceInput ?? 0) + (p.PriceOutput ?? 0))
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configured.Count > 0)
        {
            sb.Append("Lanes (pass the id to agent_spawn):\n");
            foreach (var p in configured) Line(sb, p, withModels: false, Who);
            var others = pools.Where(p => !p.Configured && (p.Busy > 0 || p.Queued > 0)).ToList();
            if (others.Count > 0)
            {
                sb.Append("\nOther models in use (automatic lanes, not for subagents):\n");
                foreach (var p in others) Line(sb, p, withModels: true, Who);
            }
        }
        else
        {
            sb.Append("No lanes are set up for agents: a subagent runs on your model unless you pass agent_spawn a model ref.\n");
            if (pools.Count > 0) sb.Append("Automatic lanes (busy/capacity):\n");
            foreach (var p in pools) Line(sb, p, withModels: true, Who);
        }
        if (runtime?.Get(context.AgentId)?.Pool is { } myPool) sb.Append("\nYou run on lane ").Append(myPool).Append('.');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject
        {
            ["pools"] = NetPiJson.ToNode(pools),
            ["budget"] = ledger.BudgetStatus(),
        }));
    }

    private void Line(StringBuilder sb, LanePoolInfo p, bool withModels, Func<LaneOwnerInfo, string> who)
    {
        sb.Append("- ").Append(p.Key);
        if (p.Configured && p.Model is not null) sb.Append(" · ").Append(p.Model);
        sb.Append(" · ").Append(p.Busy).Append('/').Append(p.Capacity).Append(" busy");
        if (p.Queued > 0) sb.Append(", ").Append(p.Queued).Append(" queued");
        if (p.Status is "offline" or "stopped") sb.Append(" [").Append(p.Status).Append(']');
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

/// <summary>How lanes work, for agents that can start subagents (order 300). Contributed only while the lanes plugin runs.</summary>
internal sealed class LanesPromptSection : IPromptSection
{
    public string Id => "lanes";
    public int Order => 300;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        bool Has(string name) => c.Tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!Has("agent_spawn")) return ValueTask.FromResult<string?>(null);
        var sb = new StringBuilder("# Lanes\n");
        sb.Append("- A lane is a model with a fixed number of parallel slots; an agent holds a slot for its whole run and queues when none is free. The user sets up lanes for agents, each with a note on when to use it, and a budget for paid models.");
        if (Has("lanes_list"))
            sb.Append("\n- Before you delegate, look at lanes_list: the lanes, their notes, price and today's spend, and the budget. Choose by the note and the cost: prefer free lanes; a paid lane spends the user's money, so use it only when the task needs what it is good at, and above the budget's warning level only when the user asked. Pass the lane's id to agent_spawn.");
        if (Has("agent_wait")) sb.Append("\n- While you wait in agent_wait, your lane goes to your subagents and you get it back with priority.");
        return ValueTask.FromResult<string?>(sb.ToString());
    }
}
