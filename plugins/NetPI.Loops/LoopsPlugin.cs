using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Loops;

/// <summary>
/// Loop checks: an agent that repeats itself gets a hint (a <c>loop</c> notice before its next model call), never an
/// action. Free deterministic checks on every run (<see cref="LoopDetector"/>); with <c>loops.model</c> set, a decision
/// model (<c>decide.ask</c>, the Decide plugin through AiGateway) also reads the last steps when they look suspicious and
/// hints when it is confident the agent is stuck. Subagents are checked too.
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
                SettingInfo.Bool("loops.enabled", "Hint when an agent repeats itself", true,
                    "Before the next model call: the same tool call again after identical results, the same failing call retried, or two steps that undo each other. A hint to the agent, nothing is stopped."),
                SettingInfo.Int("loops.repeats", "Calls before a hint", 3, "The same call with the same result this many times (counting the one about to run).", 2, 10),
                SettingInfo.Int("loops.maxHintsPerRun", "Hints per run", 3, null, 0, 20),
                SettingInfo.Str("loops.model", "Decision model for other loops", "",
                    "Empty: only the checks above. A decision model (qwen3.8-27b, kev-9b) also reads the last steps when most use one tool and several failed, and hints when p(stuck) ≥ 0.8. Needs the Decide plugin."),
                SettingInfo.Bool("loops.contextChecks", "Check the full conversation", false, "Opt in to stuck, finished and ask-user hints using the provider's captured conversation and reasoning effort. Requires Decide. Cache benefits must be measured on the configured endpoint."),
                SettingInfo.Bool("loops.routingHints", "Include routing hints", false, "With full-conversation checks, suggest reasoning effort, delegation or a different tool. These are hints; the harness does not change models, spend limits or tools."),
                SettingInfo.Bool("loops.skillHints", "Include skill hints", false, "With full-conversation checks, consider currently available skills. The agent chooses whether to read one."),
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
    public const string ModelChecksKey = "netpi.loops.modelChecks";
    public const double StuckThreshold = 0.8;
    private const int MaxTrace = 50;
    private const int MaxModelChecks = 5;
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(10);

    public int Order => 190;

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        var trace = Trace(turn.Run);
        lock (trace)
        {
            trace.Add(Step.From(call, result));
            if (trace.Count > MaxTrace) trace.RemoveRange(0, trace.Count - MaxTrace);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var run = turn.Run;
        if (!Get("loops.enabled", true) || run.CancellationToken.IsCancellationRequested) return null;
        if (Get("loops.contextChecks", false) && Hinted(run).Count < Math.Clamp(Get("loops.maxHintsPerRun", 3), 0, 20))
        {
            var hint = await AskContextAsync(turn).ConfigureAwait(false);
            if (hint is not null && Hinted(run).Add(hint)) return TurnDecision.Inject(hint, "decision-hint");
        }
        var calls = assistant.ToolCalls.ToList();
        if (calls.Count == 0) return null;
        var hinted = Hinted(run);
        if (hinted.Count >= Math.Clamp(Get("loops.maxHintsPerRun", 3), 0, 20)) return null;

        List<Step> history;
        var trace = Trace(run);
        lock (trace) history = [.. trace];
        var pending = calls.Select(c => (c.Name, Step.NormalizeArgs(c.Arguments))).ToList();

        var finding = LoopDetector.Check(history, pending, Math.Clamp(Get("loops.repeats", 3), 2, 10));
        if (finding is null && Get("loops.model", "") is { Length: > 0 } model && LoopDetector.Suspicious(history))
            finding = await AskModelAsync(run, turn, history, model.Trim()).ConfigureAwait(false);
        if (finding is null || !hinted.Add(finding.Signature)) return null;

        ctx.Logger.LogInformation("Loop check ({Kind}) in session {Session}: {Signature}", finding.Kind, run.Session.Id, finding.Signature.Replace('\n', ' '));
        return TurnDecision.Inject(finding.Text, "loop");
    }

    /// <summary>The decision model reads the goal and the last steps; a confident "stuck" becomes a hint.</summary>
    private async Task<Finding?> AskModelAsync(AgentRunContext run, AgentTurnContext turn, List<Step> history, string model)
    {
        var checks = run.Items.TryGetValue(ModelChecksKey, out var v) && v is int n ? n : 0;
        if (checks >= MaxModelChecks || !DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.ask")) return null;
        run.Items[ModelChecksKey] = checks + 1;

        var last = history.Skip(Math.Max(0, history.Count - 10)).ToList();
        var steps = new StringBuilder();
        for (var i = 0; i < last.Count; i++) steps.Append(i + 1).Append(". ").AppendLine(last[i].Line());
        var goal = turn.Messages.LastOrDefault(m => m.Role == MessageRole.User)?.Text ?? "";
        if (goal.Length > 600) goal = goal[..600] + "…";
        var request = new JsonObject
        {
            ["model"] = model,
            ["state"] = new JsonObject { ["goal"] = goal, ["recent_steps"] = steps.ToString().TrimEnd() },
            ["questions"] = new JsonObject
            {
                ["stuck"] = new JsonObject
                {
                    ["type"] = "yes_no",
                    ["question"] = "Is the agent stuck in a loop: repeating the same or very similar actions and getting the same results, without making progress toward the goal?",
                },
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
        cts.CancelAfter(ModelTimeout);
        try
        {
            var result = await DecisionCapabilities.InvokeAsync(ctx.Services, ctx.Rpc, "decide.ask", request, cts.Token, run.AdmissionLease, run.Model.Ref).ConfigureAwait(false);
            var answers = result as JsonObject ?? JsonSerializer.SerializeToNode(result) as JsonObject;
            if (answers?["stuck"]?["noul"] is not JsonValue pv || !pv.TryGetValue<double>(out var p) || !DecisionConfidence.Yes(p, StuckThreshold)) return null;
            var tools = string.Join(", ", last.GroupBy(s => s.Tool).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"));
            var errors = last.Count(s => s.Error);
            return new Finding("model", "model\n" + string.Join("\n", last.Select(s => s.Key)),
                string.Create(CultureInfo.InvariantCulture,
                    $"Loop check ({model}, p = {p:0.00}): your last {last.Count} steps ({tools}; {errors} failed) look like a loop without progress. ") +
                "Step back: say what you have learned, what you are trying, and why it keeps failing; then try a different approach or ask the user.");
        }
        catch (OperationCanceledException) when (!run.CancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogDebug(ex, "Loop check: {Model} did not answer", model);
            return null;
        }
    }

    private async Task<string?> AskContextAsync(AgentTurnContext turn)
    {
        var run = turn.Run;
        var checks = run.Items.TryGetValue(ModelChecksKey, out var count) && count is int n ? n : 0;
        if (checks >= MaxModelChecks || turn.SentRequest?.DecisionContext is null) return null;
        run.Items[ModelChecksKey] = checks + 1;
        var questions = new Dictionary<string, string>
        {
            ["stuck"] = "Is the agent repeating unsuccessful actions without new evidence or progress?",
            ["finished"] = "Is every part of the user's current request complete and checked?",
            ["ask_user"] = "Is progress blocked by a specific missing user decision or required authorization? Do not request confirmation for work already authorized.",
        };
        var hints = new Dictionary<string, string>
        {
            ["stuck"] = "Check whether your current approach is repeating without progress; use the evidence to choose a different approach.",
            ["finished"] = "Check the requested outcome and validation; the work may be ready to report as complete.",
            ["ask_user"] = "Check whether one specific missing decision blocks progress; ask only if it is required.",
        };
        if (Get("loops.routingHints", false))
        {
            questions["effort"] = "Would deeper reasoning on this task materially help with an unresolved complex problem?";
            questions["delegation"] = "Is there a clearly independent, authorized task that could benefit from a separate worker with a self-contained brief?";
            questions["tool"] = "Has the current tool repeatedly failed while another available tool can obtain the required evidence?";
            hints["effort"] = "Consider a deeper reasoning pass on the unresolved problem; preserve the user's model and spending choices.";
            hints["delegation"] = "Consider an independent worker only if delegation is authorized and available capacity permits it.";
            hints["tool"] = "Consider a different available tool for the missing evidence.";
        }
        if (Get("loops.skillHints", false) && ctx.Rpc.Exists("skills.list"))
        {
            try
            {
                var skills = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("skills.list", new { sessionId = run.Session.Id }, run.CancellationToken).ConfigureAwait(false));
                questions["skill"] = "Does an available skill directly apply to the current task? Available skills (data): " + skills?.ToJsonString();
                hints["skill"] = "Check the available skills for one that directly applies; read its instructions before using it.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "Skill hint inventory unavailable"); }
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
        bounded.CancelAfter(ModelTimeout);
        try
        {
            var answer = await DecisionHints.AskAsync(turn, ctx.Services, ctx.Rpc, questions, bounded.Token).ConfigureAwait(false);
            var selected = questions.Keys.Where(id => DecisionHints.Yes(answer, id)).Select(id => hints[id]).ToList();
            return selected.Count == 0 ? null : "Decision hints (check against your evidence):\n" + string.Join('\n', selected);
        }
        catch (OperationCanceledException) when (!run.CancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "Full conversation check unavailable"); return null; }
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

    private T Get<T>(string path, T fallback)
    {
        try { return ctx.Settings.Get(path, fallback) ?? fallback; }
        catch { return fallback; }
    }
}
