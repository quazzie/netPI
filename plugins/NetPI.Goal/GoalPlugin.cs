using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Goal;

/// <summary>
/// Goals: the user sets one for a session (<c>/goal</c>, <c>goal.set</c>) and the agent is started again after every run
/// until it calls <c>goal_update</c> with status complete (or blocked); the runtime pauses it on stop, failure, no
/// progress and limits. State in <c>meta.goal</c>; the model hears about it through appended "goal" notices.
/// </summary>
[NetPiPlugin("netpi.goal", Name = "Goal", Description = "Keeps the agent working on a goal until it marks it complete (/goal, goal_update)", Order = 66)]
public sealed class GoalPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "goals", Title = "Goals", Group = "Agents", Order = 20,
            Settings =
            [
                SettingInfo.Int("goal.maxContinuations", "Automatic runs before a goal pauses", 100, "Resume allows as many again.", 1, 10000),
                SettingInfo.Int("goal.noProgressLimit", "Runs without progress before a goal pauses", 3, "Automatic runs in a row without a successful tool call.", 1, 50),
                SettingInfo.Int("goal.tokenBudget", "Default token budget of a goal", 0, "For goals set without one; 0 = none.", 0, null, "tokens"),
            ],
        });
        var goals = new Goals(context);
        context.Tools.Register(new GoalUpdateTool(goals));
        context.Tools.Register(new GoalSetTool(goals));
        context.Services.Register<IAgentHook>(new GoalHook(goals));
        RegisterRpc(context, goals);
        return Task.CompletedTask;
    }

    private static void RegisterRpc(IPluginContext ctx, Goals goals)
    {
        static Task<object?> Result(Goal? g) => Task.FromResult<object?>(g is { Status: not Goal.Cleared } ? g.ToJson() : null);

        static Task<object?> Run(Func<Goal?> action)
        {
            try { return Result(action()); }
            catch (GoalException ex) { throw new RpcException("bad_request", ex.Message); }
        }

        long? Budget(RpcRequest r) => r.Prop("tokenBudget") is { ValueKind: JsonValueKind.Number } b && b.TryGetInt64(out var n) ? n : null;

        ctx.Rpc.Register("goal.get", (r, _) => Result(goals.Get(r.Required("sessionId"))),
            "The session's goal: { sessionId } → { id, objective, status, reason, tokenBudget, tokensUsed, continuations, … } | null");
        ctx.Rpc.Register("goal.set", (r, _) => Run(() => goals.Set(r.Required("sessionId"), r.Str("objective"), Budget(r) ?? 0, byModel: false)),
            "Set a new goal (replaces the current one) and start working on it: { sessionId, objective, tokenBudget? }");
        ctx.Rpc.Register("goal.edit", (r, _) => Run(() => goals.Edit(r.Required("sessionId"), r.Str("objective"), Budget(r))),
            "Change the objective or token budget of the current goal: { sessionId, objective?, tokenBudget? }");
        ctx.Rpc.Register("goal.pause", (r, _) => Run(() => goals.Pause(r.Required("sessionId"))),
            "Pause the goal (the current run finishes; no new run starts): { sessionId }");
        ctx.Rpc.Register("goal.resume", (r, _) => Run(() => goals.Resume(r.Required("sessionId"))),
            "Resume a paused or blocked goal (counters reset; starts a run when idle): { sessionId }");
        ctx.Rpc.Register("goal.clear", (r, _) => Run(() => goals.Clear(r.Required("sessionId"))),
            "Remove the goal: { sessionId }");
    }
}

/// <summary>Order 535: after compaction (-100) and the context notices (500–530).</summary>
internal sealed class GoalHook(Goals goals) : IAgentHook
{
    public int Order => 535;

    public ValueTask OnRunStartAsync(AgentRunContext run)
    {
        goals.RunStarted(run);
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn) => await goals.SyncNoticeAsync(turn).ConfigureAwait(false);

    public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        goals.CountUsage(turn.Run, assistant.Usage);
        return ValueTask.FromResult<TurnDecision?>(null);
    }

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        goals.ToolDone(turn.Run, call, result);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnRunEndAsync(AgentRunContext run)
    {
        goals.RunEnded(run);
        return ValueTask.CompletedTask;
    }
}

internal sealed class GoalUpdateTool(Goals goals) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "goal_update",
        Label = "Goal",
        Category = "goal",
        SummaryArg = "status",
        Description =
            "Report on the session's goal (the user set it; you are started again after every answer until you call this). " +
            "status \"complete\": everything the goal asks for is done and checked; the summary says what was done and how it " +
            "was verified. status \"blocked\": you cannot go on without the user (access, a decision that is theirs); the " +
            "summary says what you need. status \"paused\": only when the user asks you to pause the goal.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("complete", "blocked", "paused") },
                ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "What was done and how it was checked, or what you need." },
            },
            ["required"] = new JsonArray("status", "summary"),
        },
        PromptGuidelines =
        [
            "While a goal is active you are started again after every answer: call goal_update with status complete only when every part is done and verified, blocked only when you need the user.",
        ],
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        args = Args.Unwrap(args);
        var status = Args.Str(args, "status", "state")?.Trim().ToLowerInvariant() switch
        {
            "complete" or "completed" or "done" or "achieved" => Goal.Complete,
            "blocked" or "stuck" => Goal.Blocked,
            "paused" or "pause" => Goal.Paused,
            _ => null,
        };
        if (status is null) return Task.FromResult(ToolResult.Error("goal_update needs status: complete, blocked or paused."));
        var summary = Args.Str(args, "summary", "reason", "message", "note")?.Trim();
        if (string.IsNullOrEmpty(summary))
            return Task.FromResult(ToolResult.Error(status == Goal.Complete
                ? "goal_update needs a summary: what was done and how it was checked."
                : "goal_update needs a summary: what you need from the user."));
        try
        {
            var g = goals.ReportByModel(context.SessionId, status, summary);
            var text = status switch
            {
                Goal.Complete => "Goal marked complete. It no longer restarts you; tell the user what was done.",
                Goal.Blocked => "Goal marked blocked. It no longer restarts you; tell the user what you need.",
                _ => "Goal paused. It no longer restarts you.",
            };
            return Task.FromResult(ToolResult.Ok(text, new { goal = g.ToJson() }));
        }
        catch (GoalException ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Task.FromResult(ToolResult.Error($"Session {context.SessionId} not found."));
        }
    }
}

internal sealed class GoalSetTool(Goals goals) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "goal_set",
        Label = "Set goal",
        Category = "goal",
        SummaryArg = "objective",
        Description =
            "Set a goal for this session, only when the user explicitly asks for one (\"make this your goal\", \"keep going " +
            "until …\"). You are then started again after every answer until you call goal_update with status complete. Not " +
            "for ordinary requests. Fails while another goal is open.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["objective"] = new JsonObject { ["type"] = "string", ["description"] = "What must be true when the goal is done, as the user put it." },
            },
            ["required"] = new JsonArray("objective"),
        },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        args = Args.Unwrap(args);
        var objective = Args.Str(args, "objective", "goal", "text", "description");
        try
        {
            var g = goals.Set(context.SessionId, objective, 0, byModel: true);
            return Task.FromResult(ToolResult.Ok(
                "Goal set. Work on it now; when you stop without calling goal_update you are started again:\n<goal>\n" + g.Objective + "\n</goal>",
                new { goal = g.ToJson() }));
        }
        catch (GoalException ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Task.FromResult(ToolResult.Error($"Session {context.SessionId} not found."));
        }
    }
}

/// <summary>Lenient argument access: names match ignoring case, '_' and '-'; arguments sent as a JSON string are unwrapped.</summary>
internal static class Args
{
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return e;
        try
        {
            using var doc = JsonDocument.Parse(e.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return e; }
    }

    public static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        return null;
    }
}
