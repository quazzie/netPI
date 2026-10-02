using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Goal;

/// <summary>
/// A session's goal, kept in the session meta (<c>meta.goal</c>) so the UI gets it with <c>session.updated</c> and it
/// survives restarts. <see cref="ToldVersion"/>/<see cref="ToldStatus"/> record what the model was last told, so a change
/// is announced once and a goal compacted out of the context is announced again.
/// </summary>
internal sealed class Goal
{
    public const string MetaKey = "goal";
    public const string Active = "active", Paused = "paused", Blocked = "blocked", Complete = "complete", Cleared = "cleared";
    public const string ExecutionUnavailable = "execution-unavailable";

    public string Id { get; set; } = Ids.New("goal");
    public string Objective { get; set; } = "";
    public string Status { get; set; } = Active;
    /// <summary>Why it paused or blocked, or the completion summary.</summary>
    public string? Reason { get; set; }
    public long TokenBudget { get; set; }
    public long TokensUsed { get; set; }
    /// <summary>Runs started by the goal since it was set or resumed.</summary>
    public int Continuations { get; set; }
    /// <summary>Continuation runs in a row without a successful tool call.</summary>
    public int NoProgress { get; set; }
    public int Version { get; set; } = 1;
    public int ToldVersion { get; set; }
    public string? ToldStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Open => Status is Active or Paused or Blocked or ExecutionUnavailable;

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["objective"] = Objective,
        ["status"] = Status,
        ["reason"] = Reason,
        ["tokenBudget"] = TokenBudget,
        ["tokensUsed"] = TokensUsed,
        ["continuations"] = Continuations,
        ["noProgress"] = NoProgress,
        ["version"] = Version,
        ["toldVersion"] = ToldVersion,
        ["toldStatus"] = ToldStatus,
        ["createdAt"] = CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        ["updatedAt"] = UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    public static Goal? From(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o, "id") is not { Length: > 0 } id) return null;
        return new Goal
        {
            Id = id,
            Objective = Str(o, "objective") ?? "",
            Status = Str(o, "status") ?? Active,
            Reason = Str(o, "reason"),
            TokenBudget = Num(o, "tokenBudget"),
            TokensUsed = Num(o, "tokensUsed"),
            Continuations = (int)Num(o, "continuations"),
            NoProgress = (int)Num(o, "noProgress"),
            Version = Math.Max(1, (int)Num(o, "version")),
            ToldVersion = (int)Num(o, "toldVersion"),
            ToldStatus = Str(o, "toldStatus"),
            CreatedAt = Time(o, "createdAt"),
            UpdatedAt = Time(o, "updatedAt"),
        };
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Any number: a value built in memory wraps an int or long (TryGetValue&lt;long&gt; fails on an int), a parsed one a
    /// JsonElement; the JSON text reads the same for both.
    /// </summary>
    private static long Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return 0;
        var s = v.ToJsonString();
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    private static DateTimeOffset Time(JsonObject o, string key) =>
        DateTimeOffset.TryParse(Str(o, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.UtcNow;
}

internal sealed class GoalException(string message) : Exception(message);

/// <summary>
/// Goal state, notices and the continuation loop. When a run ends while the session's goal is active, the next run is
/// started with a "goal" notice once the agent is idle (a new run, so its slot is released in between and every
/// continuation gets the per-run limits). The runtime, not the model, pauses the goal: on stop, on a failed run, after
/// <c>goal.noProgressLimit</c> continuation runs without a successful tool call, at <c>goal.maxContinuations</c> and at
/// the goal's token budget.
/// </summary>
internal sealed class Goals(IPluginContext ctx)
{
    public const int MaxObjective = 4000;
    public const string NoticeKind = "goal";
    /// <summary>
    /// The goal's pause reason when a plugin reload stopped its run: the executor is coming back, so the goal pauses
    /// (resumable) instead of claiming "no executor capability", and <see cref="ExecutorChanged"/> resumes it when the
    /// executor returns (idea-ydszpo).
    /// </summary>
    public const string ReloadPausedReason = "The runtime reloaded.";

    private sealed class RunTrack
    {
        public string? GoalId;
        public bool Auto;
        public int Progress;
        public int Runs;
    }

    private readonly ConcurrentDictionary<string, RunTrack> _runs = new(); // session id → the running run
    private readonly ConcurrentDictionary<string, byte> _autoNext = new(); // sessions whose next run is a continuation
    private readonly ConcurrentDictionary<string, int> _started = new(); // session id → the last run that reached OnRunStart

    public IPluginContext Ctx => ctx;

    // ---------------------------------------------------------------- state

    public Goal? Get(string sessionId) => Goal.From(ctx.Sessions.GetSession(sessionId)?.Meta?[Goal.MetaKey]);

    /// <summary>Changes the stored goal in one session update; <paramref name="change"/> returns the new goal (or the same).</summary>
    public Goal? Update(string sessionId, Func<Goal?, Goal?> change)
    {
        Goal? result = null;
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            var next = change(Goal.From(s.Meta?[Goal.MetaKey]));
            s.Meta ??= new JsonObject();
            if (next is null) s.Meta.Remove(Goal.MetaKey);
            else
            {
                next.UpdatedAt = DateTimeOffset.UtcNow;
                s.Meta[Goal.MetaKey] = next.ToJson();
            }
            result = next;
        });
        return result;
    }

    private void CheckSession(string sessionId)
    {
        var s = ctx.Sessions.GetSession(sessionId) ?? throw new GoalException($"Session {sessionId} not found.");
        if (s.Kind == "subagent") throw new GoalException("Goals are for chat sessions, not subagents.");
    }

    private static string CheckObjective(string? objective)
    {
        objective = objective?.Trim() ?? "";
        if (objective.Length == 0) throw new GoalException("The goal is empty: describe what should be done.");
        if (objective.Length > MaxObjective)
            throw new GoalException($"The goal is {objective.Length} characters; keep it under {MaxObjective} (put details in a file and point to it).");
        return objective;
    }

    /// <summary>A new goal (replacing any other). Set by the user it starts a run when the agent is idle.</summary>
    public Goal Set(string sessionId, string? objective, long tokenBudget, bool byModel)
    {
        CheckSession(sessionId);
        var text = CheckObjective(objective);
        var goal = Update(sessionId, old =>
        {
            if (byModel && old is { Open: true }) throw new GoalException($"A goal is already {old.Status}: \"{Short(old.Objective)}\". The user can replace or clear it.");
            var budget = tokenBudget > 0 ? tokenBudget : Math.Max(0, ctx.Settings.Get("goal.tokenBudget", 0L));
            var g = new Goal { Objective = text, TokenBudget = budget };
            if (byModel) (g.ToldVersion, g.ToldStatus) = (g.Version, Goal.Active); // the tool result tells the model
            return g;
        })!;
        TitleFrom(sessionId, text);
        if (!byModel) Kick(sessionId, goal, "set");
        return goal;
    }

    /// <summary>
    /// A session started with /goal has no user message for the host's automatic title: name it after the objective
    /// (the host's rule: its first line, at most 60 characters).
    /// </summary>
    private void TitleFrom(string sessionId, string objective)
    {
        var s = ctx.Sessions.GetSession(sessionId);
        if (s is null || !(string.IsNullOrWhiteSpace(s.Title) || s.Title == "New session")) return;
        var line = objective.Split('\n')[0].Trim();
        if (line.Length > 60) line = line[..59].TrimEnd() + "…";
        if (line.Length > 0) ctx.Sessions.UpdateSession(sessionId, x => x.Title = line);
    }

    /// <summary>Changes the objective or budget of the current goal (same goal; the model hears about it on its next call).</summary>
    public Goal Edit(string sessionId, string? objective, long? tokenBudget)
    {
        CheckSession(sessionId);
        var text = objective is null ? null : CheckObjective(objective);
        return Update(sessionId, g =>
        {
            if (g is not { Open: true }) throw new GoalException("There is no goal to edit.");
            if (text is not null && text != g.Objective)
            {
                g.Objective = text;
                g.Version++;
            }
            if (tokenBudget is { } b) g.TokenBudget = Math.Max(0, b);
            return g;
        })!;
    }

    public Goal Pause(string sessionId, string? reason = null) => SetStatus(sessionId, Goal.Paused, reason ?? "Paused by the user.", null);

    /// <summary>The runtime pauses the goal it was running (no error when it changed meanwhile).</summary>
    private void PauseQuietly(string sessionId, string goalId, string reason) =>
        Update(sessionId, g =>
        {
            if (g is { Status: Goal.Active } && g.Id == goalId) (g.Status, g.Reason) = (Goal.Paused, reason);
            return g;
        });

    public Goal Resume(string sessionId)
    {
        CheckSession(sessionId);
        var busy = ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded };
        var goal = Update(sessionId, g =>
        {
            // an active goal with an idle agent (a user-started run that failed before its first turn): start it again
            if (g is { Status: Goal.Active } && !busy) return g;
            if (g is not { Status: Goal.Paused or Goal.Blocked or Goal.ExecutionUnavailable }) throw new GoalException(g is { Status: Goal.Active } ? "The goal is already active." : "There is no paused goal.");
            g.Status = Goal.Active;
            g.Reason = null;
            g.Continuations = 0;
            g.NoProgress = 0;
            return g;
        })!;
        Kick(sessionId, goal, "resumed");
        return goal;
    }

    public Goal Clear(string sessionId) => SetStatus(sessionId, Goal.Cleared, null, null);

    /// <summary>goal_update: the model completes, blocks or pauses the goal (the tool result is its notice).</summary>
    public Goal ReportByModel(string sessionId, string status, string summary)
    {
        return Update(sessionId, g =>
        {
            if (g is not { Status: Goal.Active }) throw new GoalException(g is { Open: true } ? $"The goal is {g.Status}, not active." : "There is no active goal.");
            g.Status = status;
            g.Reason = summary;
            (g.ToldVersion, g.ToldStatus) = (g.Version, status);
            return g;
        })!;
    }

    private Goal SetStatus(string sessionId, string status, string? reason, string? onlyId)
    {
        CheckSession(sessionId);
        return Update(sessionId, g =>
        {
            if (g is null || onlyId is not null && g.Id != onlyId) return g;
            if (status == Goal.Paused && g.Status != Goal.Active) throw new GoalException($"The goal is {g.Status}, not active.");
            if (status == Goal.Cleared && g.Status == Goal.Cleared) throw new GoalException("There is no goal.");
            g.Status = status;
            g.Reason = reason;
            return g;
        }) ?? throw new GoalException("There is no goal.");
    }

    // ---------------------------------------------------------------- notices

    /// <summary>The notice for a goal that is (still) active; <paramref name="what"/>: set | changed | resumed | continue | reminder.</summary>
    public static string ActiveNotice(Goal g, string what)
    {
        var sb = new StringBuilder();
        sb.Append(what switch
        {
            "changed" => "The user changed the goal. Work on it until it is fully done:",
            "resumed" => "The user resumed the goal. Continue until it is fully done:",
            "reloaded" => "The runtime reloaded while the goal was working. It is back: continue until it is fully done:",
            "continue" => $"The goal is not done yet (automatic continuation {g.Continuations}). Keep working until it is fully done:",
            "reminder" => "The goal you are working on (repeated because the earlier notices were compacted):",
            _ => "The user set a goal for this session. Work on it until it is fully done:",
        });
        sb.Append("\n<goal>\n").Append(g.Objective).Append("\n</goal>\n");
        sb.Append(
            "- Work from the actual state (files, command output, test results), not from memory, and don't narrow the goal to what is easy.\n" +
            "- Before you call it complete, check every part of it against evidence: run the tests, read the output.\n" +
            "- Done: call goal_update with status \"complete\" and a short summary of what was done and how it was checked.\n" +
            "- Only the user can unblock you (access, a decision that is theirs): goal_update with status \"blocked\" and what you need.\n" +
            "- Otherwise keep going: when you stop without calling goal_update, you are started again.");
        if (g.TokenBudget > 0) sb.Append($"\nTokens used for this goal: {Tokens(g.TokensUsed)} of {Tokens(g.TokenBudget)}.");
        return sb.ToString();
    }

    private static string InactiveNotice(Goal g) => g.Status switch
    {
        Goal.Cleared => "The user cleared the goal. Don't keep working on it unless the user asks.",
        Goal.Complete => "The goal was marked complete.",
        _ => $"The goal is {g.Status}{(string.IsNullOrWhiteSpace(g.Reason) ? "" : $" ({g.Reason.TrimEnd('.')})")}. It no longer restarts you; don't keep working on it unless the user asks.",
    };

    private static JsonObject NoticeMeta(Goal g) => new() { ["goalId"] = g.Id, ["version"] = g.Version, ["status"] = g.Status };

    internal static bool InContext(IReadOnlyList<ChatMessage> context, string goalId) => context.Any(m =>
        (m.Role == MessageRole.Notice && m.MetaString("kind") == NoticeKind && m.MetaString("goalId") == goalId)
        || m.Parts.Any(p => p is ToolCallPart { Name: "goal_update" or "goal_set" } or ToolResultPart { Name: "goal_update" or "goal_set" }));

    /// <summary>
    /// Before a model call: tells the model about a goal it has not seen (set while it was busy, compacted away), a change of
    /// objective, a resume, or that the user paused or cleared it (or the runtime paused it). Appended, never rewritten.
    /// </summary>
    public async Task SyncNoticeAsync(AgentTurnContext turn)
    {
        var sid = turn.Run.Session.Id;
        var goal = Get(sid);
        if (goal is null) return;
        string? text = null;
        if (goal.Status == Goal.Active)
        {
            if (!InContext(turn.Messages, goal.Id))
                text = ActiveNotice(goal, goal.ToldVersion == 0 ? "set"
                    : goal.ToldStatus != Goal.Active ? "resumed"
                    : goal.ToldVersion != goal.Version ? "changed"
                    : "reminder");
            else if (goal.ToldVersion != goal.Version) text = ActiveNotice(goal, "changed");
            else if (goal.ToldStatus != Goal.Active) text = ActiveNotice(goal, "resumed");
        }
        else if (goal.ToldStatus == Goal.Active && goal.ToldStatus != goal.Status && InContext(turn.Messages, goal.Id))
            text = InactiveNotice(goal);
        if (text is null) return;

        var m = ChatMessage.NoticeText(text, NoticeKind);
        foreach (var (k, v) in NoticeMeta(goal)) m.Meta![k] = v?.DeepClone();
        ctx.Sessions.AppendMessage(sid, m);
        Told(sid, goal);
        await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    private void Told(string sessionId, Goal goal) =>
        Update(sessionId, g =>
        {
            if (g is not null && g.Id == goal.Id) (g.ToldVersion, g.ToldStatus) = (goal.Version, goal.Status);
            return g;
        });

    // ---------------------------------------------------------------- runs

    /// <summary>Starts a run with the goal notice when the agent is idle; a busy agent gets the notice at its next model call.</summary>
    private void Kick(string sessionId, Goal goal, string what)
    {
        var rt = ctx.Services.Get<IAgentRuntime>();
        if (rt is null) { Unavailable(sessionId, goal); return; }
        if (rt.GetBySession(sessionId) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded }) return;
        _ = SendNoticeAsync(rt, sessionId, goal, ActiveNotice(goal, what), auto: false);
    }

    private void Unavailable(string sessionId, Goal goal)
    {
        goal.Status = Goal.ExecutionUnavailable;
        goal.Reason = "No executor capability is available. The goal is saved; resume explicitly after an executor returns.";
        Update(sessionId, g => g?.Id == goal.Id && g.Status == Goal.Active ? goal : g);
    }

    public void ExecutorChanged()
    {
        if (ctx.Services.Get<IAgentRuntime>() is not null)
        {
            // the executor is back: the goals a reload paused resume of their own; goals the user paused (or that never
            // ran because no executor was loaded) stay where they are, for an explicit goal.resume
            for (var offset = 0; ; offset += 100)
            {
                var sessions = ctx.Sessions.ListSessions(new SessionQuery { IncludeArchived = true, Offset = offset, Limit = 100 });
                foreach (var session in sessions)
                    if (Get(session.Id) is { Status: Goal.Paused, Reason: ReloadPausedReason } goal) ResumeReloaded(session.Id, goal);
                if (sessions.Count < 100) break;
            }
            return;
        }
        for (var offset = 0; ; offset += 100)
        {
            var sessions = ctx.Sessions.ListSessions(new SessionQuery { IncludeArchived = true, Offset = offset, Limit = 100 });
            foreach (var session in sessions)
                if (Get(session.Id) is { Status: Goal.Active } goal) Unavailable(session.Id, goal);
            if (sessions.Count < 100) break;
        }
    }

    /// <summary>The executor returned: a goal the reload paused resumes like a user resume (counters reset, a run starts when idle).</summary>
    private void ResumeReloaded(string sessionId, Goal goal)
    {
        var resumed = Update(sessionId, g =>
        {
            if (g is { Status: Goal.Paused, Reason: ReloadPausedReason } && g.Id == goal.Id)
            {
                g.Status = Goal.Active;
                g.Reason = null;
                g.Continuations = 0;
                g.NoProgress = 0;
            }
            return g;
        });
        if (resumed is { Status: Goal.Active }) Kick(sessionId, resumed, "reloaded");
    }

    private async Task SendNoticeAsync(IAgentRuntime rt, string sessionId, Goal goal, string text, bool auto)
    {
        try
        {
            if (auto) _autoNext[sessionId] = 0;
            Told(sessionId, goal);
            var info = await rt.SendAsync(sessionId, new UserInput
            {
                Text = text,
                AsNotice = true,
                NoticeKind = NoticeKind,
                Source = "system",
                Meta = NoticeMeta(goal),
            }, DeliveryMode.Auto).ConfigureAwait(false);
            _ = WatchStartAsync(rt, sessionId, goal.Id, info.Id, info.Runs);
        }
        catch (Exception ex)
        {
            _autoNext.TryRemove(sessionId, out _);
            ctx.Logger.LogWarning(ex, "Starting the goal run for session {Session} failed", sessionId);
            try { PauseQuietly(sessionId, goal.Id, $"Could not start the next run: {ex.Message}"); } catch (Exception) { }
        }
    }

    /// <summary>
    /// The run hooks only run once a run reaches its first turn: a run stopped while starting, or failing before its first
    /// model call (no model configured), never calls OnRunEnd. For the runs this plugin starts, pause the goal then.
    /// </summary>
    private async Task WatchStartAsync(IAgentRuntime rt, string sessionId, string goalId, string agentId, int run)
    {
        try
        {
            await rt.WaitAsync(null, [agentId], yieldSlot: false, ct: ctx.Stopping).ConfigureAwait(false);
            if (_started.TryGetValue(sessionId, out var started) && started >= run) return; // RunEnded decided
            var now = rt.GetBySession(sessionId);
            PauseQuietly(sessionId, goalId, string.IsNullOrEmpty(now?.Error) ? "Stopped." : $"The run failed: {now.Error}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Watching the goal run for session {Session} failed", sessionId); }
    }

    public void RunStarted(AgentRunContext run)
    {
        var sid = run.Session.Id;
        _started[sid] = run.Agent.Runs;
        var auto = _autoNext.TryRemove(sid, out _);
        _runs[sid] = new RunTrack { GoalId = Get(sid) is { Status: Goal.Active } g ? g.Id : null, Auto = auto, Runs = run.Agent.Runs };
    }

    /// <summary>Tokens the goal cost: input that was not read from the cache, plus output.</summary>
    public void CountUsage(AgentRunContext run, Usage? usage)
    {
        if (usage is null || !_runs.TryGetValue(run.Session.Id, out var track) || track.GoalId is null) return;
        var tokens = usage.InputTokens + usage.CacheWriteTokens + usage.OutputTokens;
        if (tokens <= 0) return;
        Update(run.Session.Id, g =>
        {
            if (g is not null && g.Id == track.GoalId) g.TokensUsed += tokens;
            return g;
        });
    }

    public void ToolDone(AgentRunContext run, ToolCallPart call, ToolResultPart result)
    {
        if (call.Name is "goal_update" or "goal_set" || result.IsError) return;
        if (_runs.TryGetValue(run.Session.Id, out var track)) Interlocked.Increment(ref track.Progress);
    }

    /// <summary>After a run: pause the goal (stopped, failed, limits) or start the next run once the agent is idle.</summary>
    public void RunEnded(AgentRunContext run)
    {
        var sid = run.Session.Id;
        _runs.TryRemove(sid, out var track);
        if (run.Agent.IsSubagent) return;
        var goal = Get(sid);
        if (goal is not { Status: Goal.Active }) return;

        if (run.CancelReason == "plugin reloaded")
        {
            // the reload stopped the run, not the capability: the executor comes back, so the goal pauses (and
            // ExecutorChanged resumes it) instead of claiming "no executor capability" (idea-ydszpo)
            PauseQuietly(sid, goal.Id, ReloadPausedReason);
            return;
        }

        if (ctx.Services.Get<IAgentRuntime>() is null)
        {
            Unavailable(sid, goal);
            return;
        }

        if (run.Outcome == "aborted")
        {
            PauseQuietly(sid, goal.Id, "Stopped.");
            return;
        }
        if (run.Outcome != "completed")
        {
            PauseQuietly(sid, goal.Id, $"The run failed: {run.Error?.Message ?? "unknown error"}");
            return;
        }

        var auto = track?.Auto == true;
        var noProgress = auto && track!.Progress == 0 ? goal.NoProgress + 1 : 0;
        var noProgressLimit = Math.Max(1, ctx.Settings.Get("goal.noProgressLimit", 3));
        var maxContinuations = Math.Max(1, ctx.Settings.Get("goal.maxContinuations", 100));
        string? stop = null;
        if (noProgress >= noProgressLimit)
            stop = $"No progress: {noProgress} automatic runs in a row without a successful tool call (goal.noProgressLimit).";
        else if (goal.Continuations >= maxContinuations)
            stop = $"Reached {maxContinuations} automatic runs (goal.maxContinuations). Resume to allow more.";
        else if (goal.TokenBudget > 0 && goal.TokensUsed >= goal.TokenBudget)
            stop = $"Token budget reached: {Tokens(goal.TokensUsed)} of {Tokens(goal.TokenBudget)}.";

        Update(sid, g =>
        {
            if (g is null || g.Id != goal.Id || g.Status != Goal.Active) return g;
            g.NoProgress = noProgress;
            if (stop is not null)
            {
                g.Status = Goal.Paused;
                g.Reason = stop;
            }
            return g;
        });
        if (stop is not null) return;

        var rt = ctx.Services.Get<IAgentRuntime>();
        if (rt is null) { Unavailable(sid, goal); return; }
        // queued input starts the next run by itself, and a running subagent's report does too: their ends decide again
        if (rt.GetQueue(sid).Count > 0) return;
        if (run.Agent.Children.Any(id => rt.Get(id) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded })) return;
        _ = ContinueAsync(rt, sid, goal.Id, track?.Runs ?? run.Agent.Runs);
    }

    /// <summary>Once the agent is idle (right after the run-end hooks), start the next run, unless something else started one.</summary>
    private async Task ContinueAsync(IAgentRuntime rt, string sessionId, string goalId, int runs)
    {
        try
        {
            for (var i = 0; ; i++)
            {
                await Task.Delay(20, ctx.Stopping).ConfigureAwait(false);
                var info = rt.GetBySession(sessionId);
                if (info is null || info.Runs > runs) return; // gone, or a new run started (user input)
                if (info.Status is not (AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded)) break;
                if (i >= 250) return; // not idle after 5 s: leave it
            }
            var goal = Update(sessionId, g =>
            {
                if (g is { Status: Goal.Active } && g.Id == goalId) g.Continuations++;
                return g;
            });
            if (goal is not { Status: Goal.Active } || goal.Id != goalId) return;
            await SendNoticeAsync(rt, sessionId, goal, ActiveNotice(goal, "continue"), auto: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Goal continuation for session {Session} failed", sessionId); }
    }

    // ---------------------------------------------------------------- helpers

    internal static string Short(string s) => s.Length <= 80 ? s : s[..80] + "…";

    internal static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString(CultureInfo.InvariantCulture);
}
