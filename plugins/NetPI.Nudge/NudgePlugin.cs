using System.Text.RegularExpressions;

namespace NetPI.Nudge;

/// <summary>
/// Registers <see cref="NudgeHook"/>. Settings: <c>nudge.enabled</c> (true), <c>nudge.maxPerRun</c> (3, consecutive
/// nudges per stall episode).
/// </summary>
[NetPiPlugin("netpi.nudge", Name = "Nudge", Description = "Auto-nudges a stalled agent", Order = 70)]
public sealed class NudgePlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "nudge", Title = "Nudge", Group = "Agents", Order = 30,
            Settings =
            [
                SettingInfo.Bool("nudge.enabled", "Nudge stalled turns", true, "Says \"continue\" when a turn ends empty, is cut off, or announces an action without doing it."),
                SettingInfo.Int("nudge.maxPerRun", "Nudges per stall episode", 3, "How many nudges in a row one stall may get. An acceptable response (a tool call, a final answer, an aborted or errored turn) resets it, so a long run that recovers can still be nudged when it stalls again.", 0, 20),
            ],
        });
        var settings = context.Settings;
        context.Services.Register<IAgentHook>(new NudgeHook(() => settings));
        return Task.CompletedTask;
    }
}

/// <summary>Why a response needs a nudge.</summary>
public enum NudgeReason { CutOff, Empty, EmptyAfterThinking, TextToolCall, AnnouncedAction }

/// <summary>
/// After a model call that produced no tool calls, detect a stalled agent and inject a notice (kind <c>nudge</c>)
/// that makes it continue. At most <c>nudge.maxPerRun</c> <i>consecutive</i> nudges: the counter is cleared by any
/// acceptable response, so the cap bounds one stall episode rather than the whole run (otherwise a run nudged three
/// times early would never be nudged again, however hard it stalled later).
/// </summary>
public sealed partial class NudgeHook(Func<ISettings?> settings) : IAgentHook
{
    public const string CountKey = "netpi.nudge.count";

    public int Order => 200;

    public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var run = turn.Run;
        var s = settings();
        if (!s.GetOr("nudge.enabled", true) || run.CancellationToken.IsCancellationRequested)
            return ValueTask.FromResult<TurnDecision?>(null);

        var reason = Classify(assistant, run.ToolCallCount);
        if (reason is null)
        {
            // The stall is over: a real tool call, a clean final answer, or an aborted/errored turn ends the episode,
            // so the next one gets its own budget. The read path already treats a missing key as 0.
            run.Items.Remove(CountKey);
            return ValueTask.FromResult<TurnDecision?>(null);
        }

        var max = s.GetOr("nudge.maxPerRun", 3);
        var count = run.Items.TryGetValue(CountKey, out var v) && v is int n ? n : 0;
        if (count >= max) return ValueTask.FromResult<TurnDecision?>(null);
        run.Items[CountKey] = count + 1;
        return ValueTask.FromResult<TurnDecision?>(TurnDecision.Inject(TextFor(reason.Value), "nudge"));
    }

    /// <summary>Decide whether (and why) an assistant message needs a nudge. Null = it does not.</summary>
    public static NudgeReason? Classify(ChatMessage assistant, int runToolCallCount)
    {
        if (assistant.ToolCalls.Any()) return null;
        if (assistant.StopReason is "aborted" or "error") return null;

        var text = assistant.Text;
        if (assistant.StopReason == "length") return NudgeReason.CutOff;
        if (string.IsNullOrWhiteSpace(text))
            return assistant.Parts.OfType<ThinkingPart>().Any(t => !string.IsNullOrWhiteSpace(t.Text) || t.Redacted is not null)
                ? NudgeReason.EmptyAfterThinking
                : NudgeReason.Empty;
        if (HasToolMarkup(text)) return NudgeReason.TextToolCall;
        if (runToolCallCount > 0 && AnnouncesAction(text)) return NudgeReason.AnnouncedAction;
        return null;
    }

    public static string TextFor(NudgeReason reason) => reason switch
    {
        NudgeReason.CutOff =>
            "Your previous response was cut off (it hit the output token limit). Continue exactly where you left off; " +
            "if you were about to call a tool, call it now.",
        NudgeReason.EmptyAfterThinking =>
            "Your previous response ended while you were still thinking, without an answer or a tool call. " +
            "Continue exactly where you left off; if you were about to call a tool, call it now. If the task is complete, give your final answer.",
        NudgeReason.Empty =>
            "Your previous response was empty. Continue the task: call the next tool now, or give your final answer if you are done.",
        NudgeReason.TextToolCall =>
            "Your previous response contained a tool call written as text (<tool_call> / <function=…> markup). It was NOT executed. " +
            "Call the tool again using the native tool-calling interface, with a valid tool name and arguments.",
        _ =>
            "You announced a next step but ended your turn without calling a tool, so nothing happened. " +
            "Continue: make the tool call now. If you are actually finished, say so and give your final answer.",
    };

    public static bool HasToolMarkup(string text) =>
        text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase) || FunctionMarkup().IsMatch(text);

    /// <summary>The text ends by announcing an action ("Let me check the tests:") instead of doing it.</summary>
    public static bool AnnouncesAction(string text)
    {
        var tail = text.TrimEnd();
        if (tail.Length == 0) return false;
        if (tail.Length > 300) tail = tail[^300..];
        // Ignore trailing markdown emphasis/quotes around the last sentence.
        var trimmed = tail.TrimEnd('*', '_', '`', '"', '\'', ')', ' ');
        if (trimmed.EndsWith(':')) return true;
        return AnnounceRegex().IsMatch(trimmed);
    }

    [GeneratedRegex(@"<function\s*=|<function\s+name\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionMarkup();

    [GeneratedRegex(@"\b(let me(?!\s+know)|now let me|i['’]ll|i will|next,? i['’]ll|let['’]s|now i['’]ll|i['’]m going to|i am going to)\b[^.!?\n]{0,160}(?::|…|\.\.\.)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnounceRegex();

}
