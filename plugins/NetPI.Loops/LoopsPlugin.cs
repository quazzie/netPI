using Microsoft.Extensions.Logging;

namespace NetPI.Loops;

/// <summary>
/// Loop checks: an agent that repeats itself gets a hint (a <c>loop</c> notice before its next model call), never an
/// action. Free deterministic checks on every run (<see cref="LoopDetector"/>). Subagents are checked too.
/// </summary>
[NetPiPlugin("netpi.loops", Name = "Loops", Description = "Hints when an agent repeats itself: the same call with the same result, the same failure retried, or back-and-forth edits", Order = 72)]
public sealed class LoopsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "loops", Title = "Loops", Group = "Agents", Order = 32,
            Settings =
            [
                SettingInfo.Int("loops.repeats", "Calls before a hint", 3, "The same call with the same result this many times (counting the one about to run).", 2, 10),
                SettingInfo.Int("loops.maxHintsPerRun", "Hints per run", 3, null, 0, 20),
            ],
        });
        context.Services.Register<IAgentHook>(new LoopHook(context));
        return Task.CompletedTask;
    }
}

internal sealed class LoopHook(IPluginContext ctx) : IAgentHook
{
    public const string TraceKey = "netpi.loops.trace";
    public const string HintedKey = "netpi.loops.hinted";
    private const int MaxTrace = 50;

    public int Order => 190;

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        // a call the provider ran inside its own loop (the Claude CLI): a nudge after the fact cannot steer that loop
        if (call.ProviderExecuted) return ValueTask.CompletedTask;
        var trace = Trace(turn.Run);
        lock (trace)
        {
            trace.Add(Step.From(call, result));
            if (trace.Count > MaxTrace) trace.RemoveRange(0, trace.Count - MaxTrace);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var run = turn.Run;
        if (run.CancellationToken.IsCancellationRequested) return ValueTask.FromResult<TurnDecision?>(null);
        var calls = assistant.ToolCalls.ToList();
        if (calls.Count == 0) return ValueTask.FromResult<TurnDecision?>(null);
        var hinted = Hinted(run);
        if (hinted.Count >= Math.Clamp(ctx.Settings.GetOr("loops.maxHintsPerRun", 3), 0, 20)) return ValueTask.FromResult<TurnDecision?>(null);

        List<Step> history;
        var trace = Trace(run);
        lock (trace) history = [.. trace];
        var pending = calls.Select(c => (c.Name, Step.NormalizeArgs(c.Arguments))).ToList();

        var finding = LoopDetector.Check(history, pending, Math.Clamp(ctx.Settings.GetOr("loops.repeats", 3), 2, 10));
        if (finding is null || !hinted.Add(finding.Signature)) return ValueTask.FromResult<TurnDecision?>(null);

        ctx.Logger.LogInformation("Loop check ({Kind}) in session {Session}: {Signature}", finding.Kind, run.Session.Id, finding.Signature.Replace('\n', ' '));
        return ValueTask.FromResult<TurnDecision?>(TurnDecision.Inject(finding.Text, "loop"));
    }

    private static List<Step> Trace(AgentRunContext run)
    {
        lock (run.Items)
        {
            if (run.Items.TryGetValue(TraceKey, out var v) && v is List<Step> t) return t;
            var trace = new List<Step>();
            run.Items[TraceKey] = trace;
            return trace;
        }
    }

    private static HashSet<string> Hinted(AgentRunContext run)
    {
        lock (run.Items)
        {
            if (run.Items.TryGetValue(HintedKey, out var v) && v is HashSet<string> h) return h;
            var hinted = new HashSet<string>(StringComparer.Ordinal);
            run.Items[HintedKey] = hinted;
            return hinted;
        }
    }
}
