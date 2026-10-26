using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NetPI.Tidy;

/// <summary>Registers <see cref="TidyHook"/> and its settings (<c>tidy.*</c>, docs/SETTINGS.md).</summary>
[NetPiPlugin("netpi.tidy", Name = "Tidy", Description = "Reminds an agent that ends its turn with work left behind to clean up", Order = 75)]
public sealed class TidyPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "tidy", Title = "Tidy", Group = "Runs", Order = 35,
            Settings =
            [
                SettingInfo.Bool("tidy.enabled", "Remind agents to clean up", true,
                    "When an agent ends its turn and its own worktree has uncommitted changes or unmerged commits, its merged branch's worktree is still there, " +
                    "files it changed in a shared checkout are not committed, or scratch it created this run is still there, it gets one notice listing them. " +
                    "Not when it is waiting for something (an answer, a running process, CI, a report): it may then say so and stop."),
                SettingInfo.Bool("tidy.decide", "Ask the decision model whether the agent is waiting", true,
                    "Only when something was left: one short decision-lane question on the agent's final message (is it waiting, does it ask the user). " +
                    "Off, or without a decision model, a final message that ends with a question counts as waiting and everything else gets the notice."),
                SettingInfo.ModelRef("tidy.decideModel", "Decision model",
                    "The model for that question; empty = the Decide plugin's default."),
            ],
        });
        var settings = context.Settings;
        context.Services.Register<IAgentHook>(new TidyHook(() => settings, context.Services));
        return Task.CompletedTask;
    }
}

/// <summary>
/// At the end of a turn (a model call that answered without calling a tool), checks what the run left behind
/// (<see cref="TidyFacts"/>) and, when something is left and the agent is not waiting for anything, injects one notice
/// (kind <c>tidy</c>) that lists it: clean up, or say in one line why it stays.
/// <para>
/// It is a reminder, never a block: once per run, and not again in a later run of the chat for the same leftovers, so an
/// agent that answered "intentional, waiting for CI" is not asked again. Three things keep it quiet: nothing left (most
/// turns; costs a few git calls), a process still running in the checkout (the agent's own build or server), and an
/// agent that says it waits (the decision model, or a final question to the user without one).
/// </para>
/// <para>After the nudge (200), the commit notice (260) and the goal (535): a stalled or goal-driven turn is theirs; this one only sees a real end.</para>
/// </summary>
public sealed class TidyHook(Func<ISettings?> settings, IServiceRegistry services) : IAgentHook
{
    public const string NoticeKind = "tidy";
    public const string AskedKey = "netpi.tidy.asked";
    public const string WrittenKey = "netpi.tidy.written";
    public const string ScratchKey = "netpi.tidy.scratch-before";
    public static readonly TimeSpan FactsBudget = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DecisionBudget = TimeSpan.FromSeconds(15);

    /// <summary>The leftovers each chat was last told about, so the next run of that chat is not told the same again.</summary>
    private readonly ConcurrentDictionary<string, string> _told = new(StringComparer.Ordinal);

    public int Order => 600;

    public ValueTask OnRunStartAsync(AgentRunContext run)
    {
        if (!Enabled()) return ValueTask.CompletedTask;
        // plain JSON in the run context, never a plugin type (docs/PLUGINS.md: it would pin this assembly after a reload)
        run.Items[ScratchKey] = new JsonArray([.. TidyFacts.ScratchEntries(run.Cwd, TopOf(run)).Select(e => (JsonNode?)JsonValue.Create(e))]);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        if (call.Name is not ("write" or "edit") || result.IsError) return ValueTask.CompletedTask;
        var paths = WrittenPaths(call.Arguments, turn.Run.Cwd);
        if (paths.Count == 0) return ValueTask.CompletedTask;
        var list = turn.Run.Items.TryGetValue(WrittenKey, out var v) && v is JsonArray a ? a : new JsonArray();
        foreach (var p in paths)
            if (!list.Any(n => n?.GetValue<string>() is { } s && TidyFacts.Same(s, p))) list.Add(p);
        turn.Run.Items[WrittenKey] = list;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var run = turn.Run;
        if (!Enabled() || run.CancellationToken.IsCancellationRequested) return null;
        if (!IsFinalAnswer(assistant)) return null;
        if (run.Items.ContainsKey(AskedKey)) return null;
        run.Items[AskedKey] = true;                       // decided once per run, whatever the outcome

        if (BusyIn(run.Cwd)) return null;                 // its build, server or test run is still going: it is not done

        List<string> leftovers;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken))
        {
            budget.CancelAfter(FactsBudget);
            try
            {
                leftovers = await TidyFacts.GatherAsync(run.Cwd, Strings(run, WrittenKey), Strings(run, ScratchKey),
                    RetireRequested(run), budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!run.CancellationToken.IsCancellationRequested) { return null; }
        }
        if (leftovers.Count == 0) { _told.TryRemove(run.Session.Id, out _); return null; }

        var fingerprint = string.Join("\n", leftovers);
        if (_told.TryGetValue(run.Session.Id, out var last) && last == fingerprint) return null;
        if (await WaitingAsync(run, TaskOf(turn.Messages), assistant.Text, leftovers).ConfigureAwait(false)) return null;

        _told[run.Session.Id] = fingerprint;
        return TurnDecision.Inject(Text(leftovers), NoticeKind);
    }

    /// <summary>The notice. Short: the agent knows its task; it needs the list and the two ways out.</summary>
    public static string Text(IReadOnlyList<string> leftovers) =>
        "Before you finish: this task left work behind —\n" +
        string.Join("\n", leftovers.Select(l => "- " + l)) + "\n" +
        "If you are done, clean it up now (commit or discard your changes, merge your branch and retire its worktree, " +
        "delete the scratch you no longer need). If you are leaving it on purpose — you wait for something, or the user " +
        "should decide — say so in one line and end your turn.";

    /// <summary>A model call that ended the turn with an answer: no tool call, not cut off, not aborted, not empty (the nudge's cases).</summary>
    public static bool IsFinalAnswer(ChatMessage assistant) =>
        !assistant.ToolCalls.Any() && assistant.StopReason is not ("aborted" or "error" or "length") &&
        !string.IsNullOrWhiteSpace(assistant.Text);

    /// <summary>Without a decision model: a final message whose last line asks something waits for the user.</summary>
    public static bool EndsWithQuestion(string text)
    {
        var last = text.TrimEnd().Split('\n').LastOrDefault()?.TrimEnd('*', '_', '`', ' ', ')') ?? "";
        return last.EndsWith('?');
    }

    /// <summary>
    /// Is the agent waiting, so the leftovers are deliberate? The decision sees the slice of the agent's context that
    /// answers it — the task it was given (the turn's last user message), its final message and what is left — rather than
    /// the whole conversation: a full transcript re-prefilled on the decision lane (which runs ahead of the agents) costs
    /// tens of thousands of tokens unless it hits the agent's own cached prefix exactly, and a "leave it for review" or
    /// "I started the CI run" is in those three. It runs on the agent's own admission slot, like the guardrails' second
    /// opinion, so it neither waits behind nor takes a slot from the agents.
    /// </summary>
    private async Task<bool> WaitingAsync(AgentRunContext run, string? task, string text, IReadOnlyList<string> leftovers)
    {
        if (!settings().GetOr("tidy.decide", true) || !DecisionCapabilities.Available(services)) return EndsWithQuestion(text);
        var state = new JsonObject
        {
            ["context"] = "an AI coding agent just ended its turn (it now stops until someone writes to it) and left this behind in its checkout",
            ["left_behind"] = new JsonArray([.. leftovers.Select(l => (JsonNode?)JsonValue.Create(l))]),
        };
        if (task is { Length: > 0 }) state["task_given"] = Clip(task, 1500);
        state["final_message"] = Clip(text, 2000, tail: true);
        var body = new JsonObject
        {
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                ["waiting"] = Q("Does the agent say it is waiting for something before its task is finished: a running job, a build, CI, a review, a report, a later check, or another agent?"),
                ["asks_user"] = Q("Does the final message ask the user a question, or for a decision or an approval, before the agent goes on?"),
                ["keep_asked"] = Q("Did the task ask the agent to leave its changes, branch or files as they are (uncommitted, unmerged, for review or for later)?"),
            },
        };
        if (settings().GetOr("tidy.decideModel", "") is { Length: > 0 } model) body["model"] = model.Trim();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
        budget.CancelAfter(DecisionBudget);
        try
        {
            var answers = await DecisionCapabilities.InvokeAsync(services, body, budget.Token,
                held: run.AdmissionLease(), heldModel: run.Model.Ref, lane: true).ConfigureAwait(false);
            if (Yes(answers, "keep_asked")) return true;
            return Yes(answers, "waiting") || Yes(answers, "asks_user");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !run.CancellationToken.IsCancellationRequested)
        {
            return EndsWithQuestion(text);                // no answer: the plain rule, never a silent "not waiting"
        }
    }

    /// <summary>The task the turn answers: its last user message (injected notices have their own role, so they are not taken for it).</summary>
    public static string? TaskOf(IReadOnlyList<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text;

    private static string Clip(string s, int max, bool tail = false) =>
        s.Length <= max ? s : tail ? "…" + s[^max..] : s[..max] + "…";

    private static JsonObject Q(string question) => new() { ["type"] = "yes_no", ["question"] = question };

    private static bool Yes(JsonObject? answers, string key) =>
        answers?[key]?["noul"] is JsonValue v && v.TryGetValue<double>(out var p) && DecisionConfidence.Probability(p) && p >= 0.5;

    private bool Enabled() => settings().GetOr("tidy.enabled", true);

    private bool BusyIn(string cwd)
    {
        try { return services.Get<IWorkspaceProcesses>()?.IsBusyIn(cwd) == true; }
        catch { return false; }
    }

    private bool RetireRequested(AgentRunContext run)
    {
        try
        {
            if (services.Get<IWorkspaceResolver>()?.ResolveLenient(run.Session) is not { } binding) return false;
            return services.Get<IWorkspaceStore>()?.GetWorkspace(binding.WorkspaceId)?.Meta?["retireWhenDone"] is not null;
        }
        catch { return false; }
    }

    private string? TopOf(AgentRunContext run)
    {
        try { return services.Get<IWorkspaceResolver>()?.ResolveLenient(run.Session)?.Root ?? run.Project?.Path; }
        catch { return run.Project?.Path; }
    }

    private static List<string> Strings(AgentRunContext run, string key) =>
        run.Items.TryGetValue(key, out var v) && v is JsonArray a
            ? a.Select(n => n is JsonValue s && s.TryGetValue<string>(out var t) ? t : null).OfType<string>().ToList()
            : [];

    /// <summary>The files a write or edit call names (its path, or each path of the several-files form), made absolute against the run's folder.</summary>
    public static List<string> WrittenPaths(string? arguments, string cwd)
    {
        var paths = new List<string>();
        JsonObject? args;
        try { args = JsonNode.Parse(arguments ?? "") as JsonObject; }
        catch (System.Text.Json.JsonException) { return paths; }
        if (args is null) return paths;
        void Add(JsonObject o)
        {
            foreach (var name in ToolPathArgs.PathNames)
                if (o[name] is JsonValue v && v.TryGetValue<string>(out var p) && !string.IsNullOrWhiteSpace(p))
                {
                    paths.Add(TidyFacts.Norm(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p)));
                    return;
                }
        }
        Add(args);
        foreach (var name in ToolPathArgs.EditFilesNames)
            if (args[name] is JsonArray files)
                foreach (var f in files.OfType<JsonObject>()) Add(f);
        return paths;
    }
}
