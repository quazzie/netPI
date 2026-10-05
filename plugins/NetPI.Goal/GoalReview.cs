using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Goal;

/// <summary>
/// An independent check before a goal closes. <c>goal_update complete</c> used to be accepted on the model's word: a
/// MarioV2 orchestrator closed its goal seconds after it made a failing test pass by pointing it at another input, with
/// a RAM check still printing "OVER BUDGET", and the user found out hours later. Now the completion is judged by one
/// model call that did not do the work: the objective, the agent's summary and the evidence (the run's tool calls with
/// what they returned). It answers PASS, or GAPS with what is missing; gaps refuse the completion and the agent goes on.
/// A second refusal in a row blocks the goal for the user, so the two can never loop. A review that cannot run (no
/// model, an error) never holds a completion back. <c>goal.review</c>: <c>check</c> (default) or <c>off</c>.
/// </summary>
internal static class GoalReview
{
    /// <summary>How much of the run's evidence the reviewer reads (characters, newest kept).</summary>
    public const int MaxEvidenceChars = 24_000;
    /// <summary>The tool calls the evidence covers at most, newest last.</summary>
    public const int MaxCalls = 60;
    public const int MaxOutputTokens = 2_000;
    /// <summary>Refusals in a row before the goal is blocked for the user.</summary>
    public const int MaxRefusals = 2;

    public const string SystemPrompt =
        "You check, independently and skeptically, whether an agent has really finished the goal a user gave it. You see " +
        "the goal, the agent's own summary, and the evidence: the tool calls of its work, newest last, with what they " +
        "returned. You cannot run anything; judge from the evidence only.\n\n" +
        "Answer PASS only if every part of the goal is done and the evidence shows it was checked. Otherwise answer GAPS " +
        "and list each concrete gap, for example: a part of the goal with no evidence; a failing, skipped or never-run " +
        "check; a test or check that was changed, weakened or pointed at other input so that it would pass; a warning or " +
        "limit in the output (such as OVER BUDGET, a failure count, an error) that the summary does not deal with; a claim " +
        "in the summary that the evidence does not show. A goal that cannot be finished by its nature (\"never stop\", " +
        "\"keep improving\") is not complete: say so.\n\n" +
        "Format: the first line is exactly PASS or GAPS. After GAPS, at most 8 short bullets, each naming what is missing " +
        "and what would show it. Do not suggest new features or extra work beyond the goal.";

    /// <summary>The run's evidence: its tool calls and their results, newest last, within <see cref="MaxEvidenceChars"/>.</summary>
    public static string Evidence(IReadOnlyList<ChatMessage> context)
    {
        var results = new Dictionary<string, ToolResultPart>(StringComparer.Ordinal);
        foreach (var m in context)
            foreach (var r in m.Parts.OfType<ToolResultPart>())
                results[r.CallId] = r;
        var lines = new List<string>();
        foreach (var m in context)
        {
            if (m.Role == MessageRole.Summary && m.Text is { Length: > 0 } summary)
                lines.Add("(earlier work, as summarized when the context was compacted)\n" + Cut(summary, 4000));
            foreach (var call in m.Parts.OfType<ToolCallPart>())
            {
                if (call.Name is "goal_update" or "goal_set") continue;
                results.TryGetValue(call.Id, out var result);
                lines.Add(Line(call, result));
            }
        }
        var picked = new List<string>();
        var total = 0;
        var calls = 0;
        for (var i = lines.Count - 1; i >= 0 && calls < MaxCalls; i--, calls++)
        {
            if (total + lines[i].Length > MaxEvidenceChars) break;
            picked.Add(lines[i]);
            total += lines[i].Length + 1;
        }
        picked.Reverse();
        if (picked.Count < lines.Count) picked.Insert(0, $"({lines.Count - picked.Count} earlier entries not shown)");
        return picked.Count == 0 ? "(no tool calls)" : string.Join('\n', picked);
    }

    private static string Line(ToolCallPart call, ToolResultPart? result)
    {
        var args = ToolArgs.Parse(call.Arguments);
        var what = call.Name switch
        {
            "bash" or "pwsh" => "$ " + OneLine(args.Str("command", "cmd", "script") ?? call.Arguments, 300),
            "edit" or "write" or "read" => $"{call.Name} {args.Str("path", "file_path", "filePath") ?? OneLine(call.Arguments, 200)}",
            _ => $"{call.Name} {OneLine(call.Arguments, 200)}",
        };
        if (result is null) return $"- {what}\n  (no result)";
        var flag = result.IsError ? " [error]" : "";
        // the end of a result says how it ended (exit code, summary line, failure count)
        var text = result.Content.Trim();
        var shown = call.Name is "read" ? $"({text.Length} chars read)" : Tail(text, call.Name is "bash" or "pwsh" or "ssh" ? 500 : 240);
        return $"- {what}{flag}\n  → {shown.Replace("\n", "\n    ")}";
    }

    public static string Prompt(Goal goal, string summary, string evidence) =>
        $"<goal>\n{goal.Objective}\n</goal>\n\n<agent-summary>\n{summary}\n</agent-summary>\n\n<evidence>\n{evidence}\n</evidence>\n\n" +
        "Is the goal complete? First line PASS or GAPS.";

    /// <summary>The verdict: null for PASS (or an answer without a clear verdict), else the gaps.</summary>
    public static string? Gaps(string answer)
    {
        var text = answer.Trim();
        var first = text.Split('\n', 2)[0].Trim().TrimStart('*', '#', ' ').ToUpperInvariant();
        if (!first.StartsWith("GAPS", StringComparison.Ordinal)) return null;
        var rest = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..].Trim() : "";
        return rest.Length == 0 ? "The review found gaps but did not name them." : Cut(rest, 2000);
    }

    private static string OneLine(string s, int max) => Cut(s.Replace("\r", "").Replace('\n', ' ').Trim(), max);
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}

internal sealed class GoalsReview(IPluginContext ctx, Goals goals)
{
    /// <summary>
    /// Whether a completion may stand: null when it may (passed, review off or not possible), else the refusal for the
    /// tool result. <paramref name="note"/> is a line for the accepted result (what the review said or why it did not run).
    /// </summary>
    public async Task<(ToolResult? Refusal, string? Note)> CheckAsync(ToolContext tc, Goal goal, string summary, CancellationToken ct)
    {
        if (string.Equals(ctx.Settings.Get("goal.review", "check"), "off", StringComparison.OrdinalIgnoreCase)) return (null, null);

        // Cheap, and no model needed: work handed to subagents is not done while they are still doing it.
        var rt = ctx.Services.Get<IAgentRuntime>();
        var me = rt?.Get(tc.AgentId);
        var working = me?.Children.Select(rt!.Get)
            .Where(a => a is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded })
            .Select(a => string.IsNullOrWhiteSpace(a!.Name) ? a.Id : a.Name).ToList() ?? [];
        if (working.Count > 0)
            return (ToolResult.Error($"Not complete yet: {working.Count} of your subagents {(working.Count == 1 ? "is" : "are")} still working " +
                $"({string.Join(", ", working)}). Wait for their reports (agent with action wait), check what they did, then complete the goal."), null);

        if (tc.Model is not { } model) return (null, "(not reviewed: no model)");
        string answer;
        try
        {
            var effort = model.Reasoning is { Supported: true } r
                ? r.Efforts.FirstOrDefault(e => e.Equals("low", StringComparison.OrdinalIgnoreCase))
                : null;
            var evidence = GoalReview.Evidence(ctx.Sessions.GetContextMessages(tc.SessionId));
            var response = await ctx.Models.CompleteAsync(new ModelRequest
            {
                Model = model,
                SystemPrompt = GoalReview.SystemPrompt,
                Messages = [ChatMessage.UserText(GoalReview.Prompt(goal, summary, evidence))],
                ReasoningEffort = effort,
                MaxOutputTokens = GoalReview.MaxOutputTokens,
                SessionId = tc.SessionId,
                AgentId = tc.AgentId,
                Purpose = "goal-review",
            }, ct).ConfigureAwait(false);
            answer = response.Text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Goal review for session {Session} could not run", tc.SessionId);
            return (null, $"(not reviewed: {ex.Message})");
        }

        var gaps = GoalReview.Gaps(answer);
        if (gaps is null)
        {
            goals.Update(tc.SessionId, g => { if (g is not null && g.Id == goal.Id) g.Refusals = 0; return g; });
            return (null, "An independent review of the evidence passed it.");
        }

        var refusals = goal.Refusals + 1;
        if (refusals >= GoalReview.MaxRefusals)
        {
            goals.Update(tc.SessionId, g =>
            {
                if (g is not { Status: Goal.Active } || g.Id != goal.Id) return g;
                (g.Status, g.Reason, g.Refusals) = (Goal.Blocked, $"An independent review refused the completion {refusals} times. Its gaps:\n{gaps}", refusals);
                (g.ToldVersion, g.ToldStatus) = (g.Version, Goal.Blocked);
                return g;
            });
            return (ToolResult.Error(
                $"Not accepted: an independent review of the evidence found gaps again, so the goal is now blocked for the user to decide:\n{gaps}\n\n" +
                "It no longer restarts you. Tell the user what was done, what the review found, and what you would do about it."), null);
        }
        goals.Update(tc.SessionId, g => { if (g is not null && g.Id == goal.Id) g.Refusals = refusals; return g; });
        return (ToolResult.Error(
            $"Not accepted yet: an independent review of the evidence found gaps:\n{gaps}\n\n" +
            "Deal with them (or show the evidence that they are not gaps), then call goal_update complete again. " +
            $"A second refusal blocks the goal for the user."), null);
    }
}
