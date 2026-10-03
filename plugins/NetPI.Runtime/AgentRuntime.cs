using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>A run ended with an error that was already reported to the user (notice written).</summary>
internal sealed class RunFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <see cref="IAgentRuntime"/>: agent registry, input delivery (steer/queue), subagents, waiting that yields the slot,
/// parent notification and agent-to-agent messaging. The model/tool loop lives in <see cref="AgentRunner"/>.
/// </summary>
internal sealed class AgentRuntime : IAgentRuntime
{
    public const int MaxFinishedInMemory = 100;
    public const int ResultNoticeChars = 8000;
    public const int YieldPriority = 100;

    private readonly AgentStore _store;
    private readonly SubagentPlanner _planner;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentState> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bySession = new(StringComparer.Ordinal);
    private volatile bool _stopping;
    private int _spawnCounter;
    internal string Generation { get; } = Guid.NewGuid().ToString("N");

    public AgentRuntime(IPluginContext ctx)
    {
        Ctx = ctx;
        _store = new AgentStore(ctx);
        _planner = new SubagentPlanner(ctx);
    }

    internal IPluginContext Ctx { get; }
    internal bool Stopping => _stopping;

    /// <summary>Throttle interval for activity-only <c>agent.status</c> updates.</summary>
    internal int StatusThrottleMs { get; set; } = 250;

    private static readonly TimeSpan KeepToolResults = TimeSpan.FromDays(7);

    /// <summary>The folder of a session's full tool results (under the host's temp folder).</summary>
    internal string ToolResultsDir(string sessionId) => Path.Combine(Ctx.Paths.TempDir, "tool-results", Safe(sessionId));

    /// <summary>Save a whole tool result; returns the file, or null when it could not be written.</summary>
    internal string? SaveToolResult(string sessionId, ToolCallPart call, string content)
    {
        try
        {
            var dir = ToolResultsDir(sessionId);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{Safe(call.Name)}-{Safe(call.Id)}.txt");
            File.WriteAllText(file, content);
            return file;
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Saving the full result of {Tool} failed", call.Name);
            return null;
        }
    }

    private static string Safe(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "x" : new string(chars, 0, Math.Min(chars.Length, 80));
    }

    /// <summary>Remove saved tool results older than a week, and a deleted session's.</summary>
    private void CleanToolResults(string? sessionId = null)
    {
        try
        {
            var root = Path.Combine(Ctx.Paths.TempDir, "tool-results");
            if (!Directory.Exists(root)) return;
            if (sessionId is not null)
            {
                var dir = ToolResultsDir(sessionId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepToolResults) File.Delete(f);
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Cleaning saved tool results failed"); }
    }

    public void Initialize()
    {
        _ = Task.Run(() => CleanToolResults());
        Ctx.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if (e.As<JsonObject>()?["id"]?.GetValue<string>() is { Length: > 0 } id) CleanToolResults(id);
        });
        _store.Initialize();
        var interrupted = _store.MarkInterrupted();
        if (interrupted > 0) Ctx.Logger.LogInformation("Marked {Count} interrupted agent(s) as failed", interrupted);
        var records = _store.LoadRecent(MaxFinishedInMemory);
        lock (_gate)
        {
            // oldest first, so the newest agent of a session wins the session mapping
            foreach (var rec in records.OrderBy(r => r.Info.CreatedAt))
            {
                var s = FromRecord(rec);
                _agents[s.Info.Id] = s;
                _bySession[s.Info.SessionId] = s.Info.Id;
            }
        }
    }

    private static AgentState FromRecord(AgentRecord rec)
    {
        var info = rec.Info;
        if (info.Status.IsBusy())
        {
            info.Status = AgentStatus.Failed;
            info.Error ??= "interrupted";
        }
        info.Activity = null;
        var state = new AgentState(info) { Instructions = rec.Instructions, NotifyParent = rec.NotifyParent };
        // The queue is the only copy of input that never reached a run: hand it back on reload so a
        // plugin swap does not lose it (idea-6oulx0). A fresh SteerSignal is fine — nothing was steered to.
        lock (state.Gate)
        {
            foreach (var q in rec.Queue)
            {
                if (string.Equals(q.Mode, "steer", StringComparison.Ordinal)) state.Steering.Add(q.Input);
                else state.FollowUps.Add(q.Input);
            }
            info.QueuedMessages = state.Steering.Count + state.FollowUps.Count;
        }
        return state;
    }

    // ---------------------------------------------------------------- settings

    internal int IntSetting(string path, int fallback)
    {
        try
        {
            if (Ctx.Settings.GetNode(path) is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d)) return (int)d;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
            }
        }
        catch { }
        return fallback;
    }

    internal bool BoolSetting(string path, bool fallback)
    {
        try
        {
            if (Ctx.Settings.GetNode(path) is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
            }
        }
        catch { }
        return fallback;
    }

    internal IReadOnlyList<IAgentHook> Hooks()
    {
        try { return Ctx.Services.GetAll<IAgentHook>().OrderBy(h => h.Order).ToList(); }
        catch { return []; }
    }

    /// <summary>Observers see every model call; unlike the hooks, nothing they come after can cut the chain short.</summary>
    internal IReadOnlyList<IAgentCallObserver> CallObservers()
    {
        try { return Ctx.Services.GetAll<IAgentCallObserver>().OrderBy(o => o.Order).ToList(); }
        catch { return []; }
    }

    /// <summary>
    /// The tools an agent is sent, sorted by name (tool definitions are part of the request prefix, and registry order
    /// changes when a plugin reloads): the registry's tools, a subagent's allowlist, no orchestration tools at the maximum
    /// depth, and not the tools switched off for the session (<see cref="SessionTools"/>) unless <paramref name="includeOff"/>.
    /// </summary>
    internal List<IAgentTool> ToolsFor(AgentInfo? agent, SessionInfo? session, bool includeOff = false)
    {
        return ToolSelection.Eligible(Ctx.Tools, agent, session, ToolSelection.MaxDepth(Ctx.Settings), includeOff);
    }

    // ---------------------------------------------------------------- registry

    internal AgentState? FindState(string? idOrSession)
    {
        if (string.IsNullOrEmpty(idOrSession)) return null;
        lock (_gate)
        {
            if (_agents.TryGetValue(idOrSession, out var s)) return s;
            if (_bySession.TryGetValue(idOrSession, out var id) && _agents.TryGetValue(id, out s)) return s;
        }
        return null;
    }

    internal AgentInfo Snapshot(AgentState s)
    {
        lock (s.Gate) return s.Info.Clone();
    }

    public IReadOnlyList<AgentInfo> List(bool includeFinished = true)
    {
        List<AgentState> states;
        lock (_gate) states = [.. _agents.Values];
        return states.Select(Snapshot)
            .Where(a => includeFinished || a.Status.IsBusy())
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public AgentInfo? Get(string agentId)
    {
        AgentState? s;
        lock (_gate) _agents.TryGetValue(agentId, out s);
        return s is null ? null : Snapshot(s);
    }

    public AgentInfo? GetBySession(string sessionId)
    {
        AgentState? s = null;
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out var id)) _agents.TryGetValue(id, out s);
        }
        return s is null ? null : Snapshot(s);
    }

    /// <summary>The session's agent, created lazily (reusing the last persisted record of that session).</summary>
    internal AgentState GetOrCreateForSession(string sessionId)
    {
        if (FindStateBySession(sessionId) is { } existing) return existing;
        var session = Ctx.Sessions.GetSession(sessionId) ?? throw new KeyNotFoundException($"No session {sessionId}");

        AgentState state;
        if (_store.LoadBySession(sessionId) is { } rec)
        {
            state = FromRecord(rec);
        }
        else
        {
            var isSub = session.Kind == "subagent";
            string? parentAgentId = null;
            var depth = 0;
            if (isSub && session.ParentSessionId is { } ps && FindStateBySession(ps) is { } parent)
            {
                parentAgentId = parent.Info.Id;
                depth = parent.Info.Depth + 1;
            }
            else if (isSub) depth = 1;
            state = new AgentState(new AgentInfo
            {
                Id = Ids.New("agt"),
                SessionId = sessionId,
                Name = isSub ? (string.IsNullOrWhiteSpace(session.Title) ? "subagent" : session.Title) : "main",
                IsSubagent = isSub,
                ParentAgentId = parentAgentId,
                ParentSessionId = session.ParentSessionId,
                Depth = depth,
                Status = AgentStatus.Idle,
                Model = session.Model,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            if (isSub && session.Meta?["agentInstructions"] is JsonValue iv && iv.TryGetValue<string>(out var instr)) state.Instructions = instr;
        }

        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out var id) && _agents.TryGetValue(id, out var raced)) return raced;
            _agents[state.Info.Id] = state;
            _bySession[sessionId] = state.Info.Id;
        }
        Save(state);
        return state;
    }

    private AgentState? FindStateBySession(string sessionId)
    {
        lock (_gate)
            return _bySession.TryGetValue(sessionId, out var id) && _agents.TryGetValue(id, out var s) ? s : null;
    }

    private void Register(AgentState s)
    {
        lock (_gate)
        {
            _agents[s.Info.Id] = s;
            _bySession[s.Info.SessionId] = s.Info.Id;
        }
    }

    /// <summary>Keep active agents and the most recent <see cref="MaxFinishedInMemory"/> inactive ones.</summary>
    private void Prune()
    {
        lock (_gate)
        {
            var inactive = new List<(AgentState S, DateTimeOffset At)>();
            foreach (var s in _agents.Values)
            {
                lock (s.Gate)
                {
                    if (s.Run is not null || s.Steering.Count > 0 || s.FollowUps.Count > 0 || s.ResultWaiters > 0) continue;
                    inactive.Add((s, s.Info.FinishedAt ?? s.Info.CreatedAt));
                }
            }
            if (inactive.Count <= MaxFinishedInMemory) return;
            foreach (var (s, _) in inactive.OrderByDescending(x => x.At).Skip(MaxFinishedInMemory))
            {
                // keep parents of running children (their results must find them)
                if (s.Info.Children.Any(c => _agents.TryGetValue(c, out var child) && child.Run is not null)) continue;
                _agents.Remove(s.Info.Id);
                if (_bySession.TryGetValue(s.Info.SessionId, out var id) && id == s.Info.Id) _bySession.Remove(s.Info.SessionId);
            }
        }
    }

    internal void Save(AgentState s)
    {
        AgentInfo snap;
        string? instructions;
        bool notify;
        List<QueuedRecord> queue;
        lock (s.Gate)
        {
            snap = s.Info.Clone();
            instructions = s.Instructions;
            notify = s.NotifyParent;
            queue = s.QueueRecords();
        }
        _store.Save(snap, instructions, notify, queue);
    }

    // ---------------------------------------------------------------- events

    internal void Emit(string type, JsonObject data, string? sessionId = null) =>
        Ctx.Events.Publish(new BusEvent { Type = type, Data = data, SessionId = sessionId });

    /// <summary>Publish <c>agent.status</c>. Activity-only updates are throttled to ~4/s.</summary>
    internal void PublishStatus(AgentState s, bool throttled = false)
    {
        if (throttled)
        {
            lock (s.Gate)
            {
                var elapsed = Environment.TickCount64 - s.LastStatusTick;
                if (elapsed < StatusThrottleMs)
                {
                    if (!s.StatusTimerArmed)
                    {
                        s.StatusTimerArmed = true;
                        _ = Task.Delay((int)(StatusThrottleMs - elapsed)).ContinueWith(_ =>
                        {
                            lock (s.Gate) s.StatusTimerArmed = false;
                            PublishStatus(s);
                        }, TaskScheduler.Default);
                    }
                    return;
                }
            }
        }
        lock (s.PublishGate)
        {
            AgentInfo snap;
            lock (s.Gate)
            {
                s.LastStatusTick = Environment.TickCount64;
                snap = s.Info.Clone();
            }
            try
            {
                Ctx.Events.Publish(new BusEvent { Type = EventTypes.AgentStatus, Data = new JsonObject { ["agent"] = NetPiJson.ToNode(snap) } });
            }
            catch (Exception ex)
            {
                Ctx.Logger.LogDebug(ex, "agent.status publish failed");
            }
        }
    }

    internal void PublishQueue(AgentState s)
    {
        List<QueuedInput> items;
        lock (s.Gate) items = s.QueueSnapshot();
        Emit(EventTypes.AgentQueue, new JsonObject
        {
            ["sessionId"] = s.Info.SessionId,
            ["items"] = NetPiJson.ToNode(items),
        }, s.Info.SessionId);
        // The queue is the only copy of input that never reached a run: persist it on every change, so a
        // reload hands it back instead of losing it (idea-6oulx0).
        Save(s);
    }

    /// <summary>Set status/activity; persists and publishes when the status changed.</summary>
    internal void SetStatus(AgentState s, AgentStatus status, string? activity, bool keepActivity = false)
    {
        bool changed, activityChanged;
        lock (s.Gate)
        {
            changed = s.Info.Status != status;
            s.Info.Status = status;
            activityChanged = !keepActivity && s.Info.Activity != activity;
            if (!keepActivity) s.Info.Activity = activity;
        }
        if (changed) Save(s);
        // Nothing to say when neither the status nor the activity moved. Every turn asks for a slot, and an unchanged
        // agent.status is still a bus event the UI serializes and the diagnostics recorder summarizes; a client that
        // missed one gets the whole agent over RPC anyway.
        if (changed || activityChanged) PublishStatus(s);
    }

    /// <summary>Set the activity text (throttled publish when it changed).</summary>
    internal void SetActivity(AgentState s, string? activity)
    {
        bool changed;
        lock (s.Gate)
        {
            changed = s.Info.Activity != activity;
            s.Info.Activity = activity;
        }
        if (changed) PublishStatus(s, throttled: true);
    }

    internal void Update(AgentState s, Action<AgentInfo> mutate)
    {
        lock (s.Gate) mutate(s.Info);
    }

    // ---------------------------------------------------------------- persistence of inputs

    internal ChatMessage PersistInput(AgentState s, UserInput input, string? delivery)
    {
        ChatMessage m;
        if (input.AsNotice)
        {
            m = ChatMessage.NoticeText(input.Text, string.IsNullOrEmpty(input.NoticeKind) ? "notice" : input.NoticeKind);
            if (!string.Equals(input.Source, "user", StringComparison.Ordinal)) m.Meta!["source"] = input.Source;
        }
        else
        {
            var parts = new List<MessagePart>();
            if (!string.IsNullOrEmpty(input.Text)) parts.Add(new TextPart { Text = input.Text });
            if (input.Images is { Count: > 0 } images) parts.AddRange(images);
            m = new ChatMessage { Role = MessageRole.User, Parts = parts };
            var meta = new JsonObject();
            if (!string.Equals(input.Source, "user", StringComparison.Ordinal)) meta["source"] = input.Source;
            if (delivery is not null)
            {
                meta["delivery"] = delivery;
                meta["kind"] = delivery == "queue" ? "queued" : delivery;
            }
            if (meta.Count > 0) m.Meta = meta;
        }
        if (input.Meta is { Count: > 0 } extra)
        {
            m.Meta ??= new JsonObject();
            foreach (var (k, v) in extra) m.Meta[k] = v?.DeepClone();
        }
        m.SessionId = s.Info.SessionId;
        m.CreatedAt = DateTimeOffset.UtcNow;
        return Ctx.Sessions.AppendMessage(s.Info.SessionId, m);
    }

    internal void AppendNotice(AgentState s, string text, string kind, JsonObject? meta = null)
    {
        try
        {
            var m = ChatMessage.NoticeText(text, kind);
            m.SessionId = s.Info.SessionId;
            m.Meta!["agentId"] = s.Info.Id;
            if (meta is not null)
                foreach (var (k, v) in meta) m.Meta[k] = v?.DeepClone();
            Ctx.Sessions.AppendMessage(s.Info.SessionId, m);
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Failed to append notice to {Session}", s.Info.SessionId);
        }
    }

    // ---------------------------------------------------------------- delivery / runs

    public async Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default)
    {
        var state = GetOrCreateForSession(sessionId);
        await DeliverAsync(state, input, mode).ConfigureAwait(false);
        return Snapshot(state);
    }

    /// <summary>Idle → persist the input and start a run. Running → steer (Auto/Steer) or queue a follow-up.</summary>
    internal Task DeliverAsync(AgentState s, UserInput input, DeliveryMode mode)
    {
        if (_stopping) throw new InvalidOperationException("The agent runtime is stopping.");
        RunState? run = null;
        CancellationTokenSource? sig = null;
        lock (s.Gate)
        {
            if (s.Run is not null)
            {
                if (mode == DeliveryMode.Queue) s.FollowUps.Add(input);
                else
                {
                    s.Steering.Add(input);
                    // The signal this steer belongs to, taken with the add: a drain that replaces it before the Cancel below
                    // has already taken the steer, and the late Cancel must not land on the fresh signal.
                    if (string.Equals(input.Source, "user", StringComparison.Ordinal)) sig = s.SteerSignal;
                }
                s.Info.QueuedMessages = s.Steering.Count + s.FollowUps.Count;
            }
            else
            {
                run = BeginRunLocked(s);
            }
        }

        if (run is null)
        {
            if (sig is not null)
            {
                try { sig.Cancel(); } catch (ObjectDisposedException) { }
            }
            PublishQueue(s);
            PublishStatus(s);
            return Task.CompletedTask;
        }

        try
        {
            PersistInput(s, input, null);
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Failed to persist input for {Session}", s.Info.SessionId);
        }
        LaunchRun(s, run);
        return Task.CompletedTask;
    }

    /// <summary>Create and register a run. Caller holds <c>s.Gate</c> and has checked <c>s.Run == null</c>.</summary>
    private static RunState BeginRunLocked(AgentState s)
    {
        var run = new RunState();
        s.Run = run;
        s.RunDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ResultConsumed = false;
        s.CancelledByParent = false;
        s.PendingNotificationId = null;
        if (s.SteerSignal.IsCancellationRequested) s.SteerSignal = new CancellationTokenSource();
        // A run started for leftover follow-ups delivers the first one right away.
        if (s.Steering.Count == 0 && s.FollowUps.Count > 0)
        {
            s.Steering.Add(s.FollowUps[0]);
            s.FollowUps.RemoveAt(0);
        }
        var info = s.Info;
        info.Status = AgentStatus.Running;
        info.Activity = "starting";
        info.StartedAt = DateTimeOffset.UtcNow;
        info.FinishedAt = null;
        info.Error = null;
        info.Runs++;
        info.QueuedMessages = s.Steering.Count + s.FollowUps.Count;
        return run;
    }

    private void LaunchRun(AgentState s, RunState run)
    {
        Save(s);
        PublishStatus(s);
        var runner = new AgentRunner(this, s, run);
        run.Task = Task.Run(runner.RunAsync);
    }

    /// <summary>Called by the runner when a run ends (always, from its finally block).</summary>
    internal async Task EndRunAsync(AgentState s, RunState run, AgentRunContext? rc, string outcome, Exception? error)
    {
        try { run.Lease?.Dispose(); } catch (Exception ex) { Ctx.Logger.LogDebug(ex, "Lease release failed"); }
        run.Lease = null;

        if (rc is not null)
        {
            rc.Outcome = outcome;
            rc.CancelReason = run.CancelReason;
            rc.Error = error;
            foreach (var hook in Hooks())
            {
                try { await hook.OnRunEndAsync(rc).ConfigureAwait(false); }
                catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Agent hook OnRunEnd failed"); }
            }
        }

        AgentInfo final;
        bool notify;
        UserInput? notice = null;
        RunState? next = null;
        List<QueuedRecord> queue = [];
        TaskCompletionSource done;
        lock (s.Gate)
        {
            var info = s.Info;
            info.FinishedAt = DateTimeOffset.UtcNow;
            info.Activity = null;
            if (run.LastAssistantText is not null) info.Result = run.LastAssistantText;
            switch (outcome)
            {
                case "completed":
                    info.Status = info.IsSubagent ? AgentStatus.Completed : AgentStatus.Idle;
                    info.Error = null;
                    break;
                case "aborted":
                    info.Status = info.IsSubagent ? AgentStatus.Cancelled : AgentStatus.Idle;
                    info.Error = info.IsSubagent ? run.CancelReason ?? "cancelled" : null;
                    break;
                default:
                    info.Status = info.IsSubagent ? AgentStatus.Failed : AgentStatus.Idle;
                    info.Error = error?.Message ?? "failed";
                    break;
            }
            s.ResultConsumed = s.ResultWaiters > 0;
            notify = info.IsSubagent && s.NotifyParent && !s.ResultConsumed && !run.Stopping && !_stopping
                     && !s.CancelledByParent && info.ParentAgentId is not null;
            final = info.Clone();
            s.Run = null;
            done = s.RunDone;
            // The agent-result notice the parent gets (and the id a wait needs to drop it from the parent's
            // queue) is decided here, with the run's clearing: a WaitAsync that sees Run gone reads under this
            // same lock, so it must find the id there, or it removes nothing and the parent gets the report
            // twice (with the wait's result and as the notice). The other half of the rule is at the queueing:
            // a result a wait consumed before this runs is not queued at all (NotifyParentAsync).
            if (notify) notice = ParentNotice(final);
            if (notice is not null) s.PendingNotificationId = notice.Id;
            // Input that is still queued starts the next run, unless this run failed without taking any: the run steps that
            // can fail (the model is gone, the budget is spent, the agent is unavailable) come before the queue is drained, so
            // another run would fail the same way, append the same error, and start again for as long as the cause lasts. The
            // queue stays as it is (the chips and "send" are there for it) until the user acts or a run gets further.
            if (!run.Aborted && !run.Stopping && !_stopping && (s.Steering.Count > 0 || s.FollowUps.Count > 0)
                && (outcome == "completed" || run.Delivered))
                next = BeginRunLocked(s);
            queue = s.QueueRecords();
        }

        done.TrySetResult();
        _store.Save(final, s.Instructions, s.NotifyParent, queue);
        try
        {
            Ctx.Events.Publish(new BusEvent { Type = EventTypes.AgentStatus, Data = new JsonObject { ["agent"] = NetPiJson.ToNode(final) } });
        }
        catch { }

        if (notice is not null) await NotifyParentAsync(s, final, notice).ConfigureAwait(false);
        if (next is not null) LaunchRun(s, next);
        Prune();
    }

    // ---------------------------------------------------------------- parent notification

    internal static string Escape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    internal static string StatusName(AgentStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    /// <summary>The agent-result notice a finished subagent's parent gets (built where its id is recorded).</summary>
    private static UserInput ParentNotice(AgentInfo final)
    {
        var sb = new StringBuilder();
        sb.Append("<agent-result id=\"").Append(final.Id).Append("\" name=\"").Append(Escape(final.Name))
          .Append("\" status=\"").Append(StatusName(final.Status)).Append("\">\n");
        if (final.Status == AgentStatus.Failed && !string.IsNullOrEmpty(final.Error)) sb.Append("Error: ").Append(final.Error).Append("\n\n");
        var report = string.IsNullOrWhiteSpace(final.Result) ? "(no final report)" : final.Result.Trim();
        sb.Append(TextLimit.Head(report, ResultNoticeChars, $"Use agent with action result and id {final.Id} for the full report."));
        sb.Append("\n</agent-result>");
        return new UserInput
        {
            Text = sb.ToString(),
            AsNotice = true,
            NoticeKind = "agent-result",
            Source = "agent:" + final.Id,
            Meta = new JsonObject
            {
                ["agentId"] = final.Id, ["agentName"] = final.Name, ["sessionId"] = final.SessionId,
                ["status"] = StatusName(final.Status),
            },
        };
    }

    private async Task NotifyParentAsync(AgentState child, AgentInfo final, UserInput notice)
    {
        var parent = FindState(final.ParentAgentId);
        if (parent is null)
        {
            lock (child.Gate) child.PendingNotificationId = null;
            return;
        }
        try
        {
            // Whether the parent gets the report at all is settled under the child's gate, the gate a wait takes to record
            // that it took the result instead (<see cref="WaitAsync"/>): the notice is queued here or not at all, because a
            // queued one cannot be taken back once the parent's next turn has drained it into the conversation — and a wait
            // that consumed the result a moment earlier already looked, found nothing to drop, and moved on.
            Task queued;
            lock (child.Gate)
            {
                if (child.ResultConsumed)
                {
                    child.PendingNotificationId = null;   // nothing is queued, so there is nothing for a wait to drop
                    return;
                }
                queued = DeliverAsync(parent, notice, DeliveryMode.Auto);
            }
            await queued.ConfigureAwait(false);
            // A wait may have consumed the result while this delivery was in flight: its removal of the queued
            // input then ran before the input was queued and found nothing to drop.
            lock (child.Gate)
                if (child.ResultConsumed) RemoveQueuedInput(parent, notice.Id);
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Failed to notify parent {Parent} about {Child}", final.ParentAgentId, final.Id);
        }
    }

    // ---------------------------------------------------------------- slots

    /// <summary>The agent the chat last ran on: the scheduler's tie-break between agents that are equally free. None for a chat on "any" that has not run.</summary>
    private string? PreferredAgent(string sessionId) => SessionAgent.Running(Ctx.Sessions.GetSession(sessionId));

    /// <summary>
    /// What a granted slot says about where the chat runs: the agent it holds is where the chat last ran (the picker shows it,
    /// the scheduler prefers it next time, its calls are billed to it) — not a reservation, the next run goes to whichever
    /// agent is free first. A chat on "any" keeps that choice and only records the agent of the run; the model of the agent
    /// it got becomes the chat's model (<paramref name="model"/>).
    /// </summary>
    private void NoteAgent(AgentState s, string agent, string? model = null)
    {
        Update(s, i => i.Agent = agent);
        var session = Ctx.Sessions.GetSession(s.Info.SessionId);
        if (session is null) return;
        var running = session.Meta?[SessionAgent.RunKey] is JsonValue r && r.TryGetValue<string>(out var current) ? current : null;
        var wanted = SessionAgent.IsAny(session) ? null : SessionAgent.Of(session);
        if (string.Equals(running, agent, StringComparison.Ordinal) && (SessionAgent.IsAny(session) || string.Equals(wanted, agent, StringComparison.Ordinal))
            && (model is null || string.Equals(session.Model, model, StringComparison.OrdinalIgnoreCase)))
            return;
        Ctx.Sessions.UpdateSession(s.Info.SessionId, x =>
        {
            x.Meta ??= new JsonObject();
            x.Meta[SessionAgent.RunKey] = agent;
            if (!SessionAgent.IsAny(x)) x.Meta[SessionAgent.MetaKey] = agent;
            if (model is not null) x.Model = model;
        });
    }

    /// <summary>
    /// Acquire a slot for <paramref name="model"/> on the session's agent (status Queued while waiting). Returns null without
    /// a scheduler. Survives a scheduler reload (its waiters are cancelled → re-resolve and retry). Throws
    /// <see cref="CallRefusedException"/> when a limit stops it or the agent can't take work.
    /// </summary>
    internal async Task<IAgentSlot?> AcquireSlotAsync(AgentState s, RunState run, ModelInfo model, int priority, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var scheduler = Ctx.Services.Get<IAgentScheduler>();
            if (scheduler is null)
            {
                if (model.IsLocal && Ctx.Services.Get<IResourceLeases>() is { } physical)
                {
                    var capacity = ModelCapacity.Local(Ctx.Settings, model);
                    if (!physical.TryAcquire("local:" + model.Ref, capacity, new AgentSlotRequest
                        { Key = model.Ref, AgentId = s.Info.Id, SessionId = s.Info.SessionId, Label = s.Info.Name, ExecutorGeneration = Generation, Priority = priority, Provider = model.Provider }, capacity, out var held))
                    {
                        SetStatus(s, AgentStatus.Queued, "waiting for model capacity");
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue; // Also re-resolve a scheduler that returned during the gap.
                    }
                    run.Model = model;
                    SetStatus(s, AgentStatus.Running, null, keepActivity: true);
                    return new PhysicalSlot(held!, model.Ref, s.Info.Id);
                }
                Update(s, i => i.Agent = null);
                run.Model = model;
                SetStatus(s, AgentStatus.Running, null, keepActivity: true);
                return null;
            }
            // Every agent on the model may take the run: it waits for the first with a free instance, not in one agent's queue.
            var agents = scheduler.Candidates(model, PreferredAgent(s.Info.SessionId));
            var pool = agents.Count == 0 ? scheduler.Resolve(model) : scheduler.Resolve(model, agents[0]);
            var several = agents.Count > 1;
            Update(s, i => i.Agent = several ? null : pool);
            var request = new AgentSlotRequest
            {
                Key = pool,
                Candidates = several ? agents : null,
                AgentId = s.Info.Id,
                SessionId = s.Info.SessionId,
                Label = s.Info.Name,
                ExecutorGeneration = Generation,
                Priority = priority,
                Provider = model.Provider,
            };
            if (scheduler.TryAcquire(request, out var lease) && lease is not null)
            {
                run.Model = model;
                if (agents.Count > 0) NoteAgent(s, lease.Key);
                else Update(s, i => i.Agent = lease.Key);
                SetStatus(s, AgentStatus.Running, null, keepActivity: true);
                return lease;
            }
            SetStatus(s, AgentStatus.Queued, several ? "waiting for a free agent" : agents.Count == 0 ? $"waiting for a slot on {pool}" : $"waiting for agent {pool}");
            try
            {
                lease = await scheduler.AcquireAsync(request, ct).ConfigureAwait(false);
                run.Model = model;
                if (agents.Count > 0) NoteAgent(s, lease.Key);
                else Update(s, i => i.Agent = lease.Key);
                SetStatus(s, AgentStatus.Running, "starting");
                return lease;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the scheduler was stopped (plugin reload): resolve the new one and retry
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A run of a chat on "any available agent": the first agent that can take work and has a free instance takes it, and
    /// the chat's model becomes that agent's model. The slot comes first because the models differ per agent. Survives a
    /// scheduler reload like <see cref="AcquireSlotAsync"/>.
    /// </summary>
    internal async Task<(IAgentSlot Slot, string ModelRef)> AcquireAnySlotAsync(AgentState s, int priority, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var scheduler = Ctx.Services.Get<IAgentScheduler>()
                ?? throw new CallRefusedException("\"Any available agent\" needs the agents plugin.") { Kind = "unavailable" };
            var agents = scheduler.Candidates(null, PreferredAgent(s.Info.SessionId));
            if (agents.Count == 0) throw new CallRefusedException("No agent is set up. Set one up in Settings → Agents.") { Kind = "unavailable" };
            var several = agents.Count > 1;
            Update(s, i => i.Agent = several ? null : agents[0]);
            var request = new AgentSlotRequest
            {
                Key = agents[0],
                Candidates = several ? agents : null,
                AgentId = s.Info.Id,
                SessionId = s.Info.SessionId,
                Label = s.Info.Name,
                ExecutorGeneration = Generation,
                Priority = priority,
            };
            if (!(scheduler.TryAcquire(request, out var lease) && lease is not null))
            {
                SetStatus(s, AgentStatus.Queued, several ? "waiting for a free agent" : $"waiting for agent {agents[0]}");
                try { lease = await scheduler.AcquireAsync(request, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // the scheduler was stopped (plugin reload): resolve the new one and retry
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }
            }
            var modelRef = scheduler.ModelOf(lease.Key);
            if (modelRef is null)
            {
                lease.Dispose();
                throw new CallRefusedException($"The agent \"{lease.Key}\" has no model.") { Kind = "unavailable" };
            }
            NoteAgent(s, lease.Key, modelRef);
            SetStatus(s, AgentStatus.Running, "starting");
            return (lease, modelRef);
        }
    }

    // ---------------------------------------------------------------- spawn / wait / message / abort
    private sealed class PhysicalSlot(IDisposable held, string key, string agentId) : IAgentSlot
    {
        public string? LeaseId => (held as IResourceLease)?.Id;
        private int _released;
        public string Key => key;
        public string AgentId => agentId;
        public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) held.Dispose(); }
    }

    public async Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default)
    {
        if (_stopping) throw new InvalidOperationException("The agent runtime is stopping.");
        if (string.IsNullOrWhiteSpace(request.Task)) throw new ArgumentException("A subagent needs a task.");

        AgentState? parent = null;
        if (request.ParentAgentId is { } pid)
            parent = FindState(pid) ?? throw new ArgumentException($"Unknown parent agent '{pid}'.");
        var parentInfo = parent is null ? null : Snapshot(parent);
        var depth = (parentInfo?.Depth ?? 0) + 1;
        var maxDepth = ToolSelection.MaxDepth(Ctx.Settings);
        if (depth > maxDepth)
            throw new InvalidOperationException($"Maximum subagent depth ({maxDepth}) reached: this agent cannot spawn subagents (setting agents.maxDepth).");

        var parentSession = parentInfo is null ? null : Ctx.Sessions.GetSession(parentInfo.SessionId);
        var id = Ids.New("agt");
        var n = Interlocked.Increment(ref _spawnCounter);
        var name = string.IsNullOrWhiteSpace(request.Name) ? $"agent-{(parentInfo?.Children.Count ?? n - 1) + 1}" : request.Name.Trim();

        // Everything the child needs is decided before its session exists (SubagentPlanner): a refused model, workspace or
        // tool list must not leave a runnable child behind, and the plan itself needs no running agent.
        var plan = await _planner.PlanAsync(request, parentInfo, parentSession, id, name, ct).ConfigureAwait(false);
        var session = Ctx.Sessions.CreateSession(new SessionInfo
        {
            Title = name,
            Kind = "subagent",
            ParentSessionId = parentSession?.Id ?? parentInfo?.SessionId,
            ProjectId = request.ProjectId ?? parentSession?.ProjectId,
            Model = plan.ModelRef,
            Reasoning = plan.Reasoning,
            Meta = plan.Meta,
        });

        var state = new AgentState(new AgentInfo
        {
            Id = id,
            SessionId = session.Id,
            Name = name,
            ParentAgentId = parentInfo?.Id,
            ParentSessionId = session.ParentSessionId,
            IsSubagent = true,
            Depth = depth,
            Status = AgentStatus.Idle,
            Model = plan.ModelRef,
            CreatedAt = DateTimeOffset.UtcNow,
            Task = request.Task,
            ToolAllowlist = plan.ToolAllowlist,
        })
        {
            Instructions = plan.Instructions,
            NotifyParent = request.NotifyParent,
        };
        // Ownership is the worker's, and the worker is this session (its own, new) — not the agent, and not the slot it
        // runs on. A workspace provisioned for a batch before it started has no owner yet.
        if (plan.Workspace is { } workspace && string.IsNullOrEmpty(workspace.OwnerSessionId))
        {
            Ctx.Services.Get<IWorkspaceStore>()?.UpdateWorkspace(workspace.WorkspaceId, w =>
            {
                w.OwnerSessionId = session.Id;
                w.OwnerAgentId = id;
            });
        }

        Register(state);

        if (parent is not null)
        {
            lock (parent.Gate) parent.Info.Children.Add(id);
            Save(parent);
            PublishStatus(parent);
        }

        await DeliverAsync(state, new UserInput
        {
            Text = request.Task,
            Source = parentInfo is null ? "system" : "agent:" + parentInfo.Id,
        }, DeliveryMode.Auto).ConfigureAwait(false);
        return Snapshot(state);
    }

    public async Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var caller = FindState(callerAgentId);
        // never wait on yourself or an ancestor (that would deadlock)
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        for (var a = caller; a is not null && excluded.Add(a.Info.Id); a = FindState(a.Info.ParentAgentId)) { }

        var targets = new List<AgentState>();
        foreach (var id in agentIds)
            if (FindState(id) is { } t && !excluded.Contains(t.Info.Id) && !targets.Contains(t)) targets.Add(t);

        var registered = new List<AgentState>();
        var tasks = new List<Task>();
        foreach (var t in targets)
        {
            lock (t.Gate)
            {
                if (t.Run is null) continue;
                t.ResultWaiters++;
                registered.Add(t);
                tasks.Add(t.RunDone.Task);
            }
        }

        try
        {
            if (tasks.Count > 0) await WaitCoreAsync(caller, tasks, yieldSlot, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var t in registered)
                lock (t.Gate) t.ResultWaiters--;
        }

        var results = new List<AgentInfo>(targets.Count);
        foreach (var t in targets)
        {
            AgentInfo snap;
            lock (t.Gate)
            {
                if (t.Run is null)
                {
                    // The result is returned here, so the agent-result notice queued at the caller must not reach it as
                    // well. Recording it as consumed and dropping the notice are one step under the agent's gate — the
                    // gate the completion path takes to queue it (NotifyParentAsync) — so a notice queued after this
                    // cannot slip past: the completion sees the consumed result and sends nothing.
                    t.ResultConsumed = true;
                    if (t.PendingNotificationId is { } pending)
                    {
                        t.PendingNotificationId = null;
                        if (caller is not null) RemoveQueuedInput(caller, pending);
                    }
                }
                snap = t.Info.Clone();
            }
            results.Add(snap);
        }
        return results;
    }

    public async Task<bool> WaitYieldedAsync(string? callerAgentId, Task until, string activity, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await WaitCoreAsync(FindState(callerAgentId), [until], yieldSlot: true, timeout, ct, activity).ConfigureAwait(false);
        return until.IsCompleted;
    }

    /// <summary>
    /// Waits for <paramref name="tasks"/> with the caller's slot given back (when it holds one), until they end, the timeout
    /// passes or the user steers the caller. <paramref name="activity"/> replaces the "waiting for N agents" countdown.
    /// </summary>
    private async Task WaitCoreAsync(AgentState? caller, List<Task> tasks, bool yieldSlot, TimeSpan? timeout, CancellationToken ct,
        string? activity = null)
    {
        RunState? run = null;
        CancellationToken steer = default;
        if (caller is not null)
        {
            lock (caller.Gate)
            {
                run = caller.Run;
                steer = caller.SteerSignal.Token;
            }
        }

        IAgentSlot? yielded = null;
        var model = run?.Model;
        static string WaitingFor(int n) => $"waiting for {n} agent{(n == 1 ? "" : "s")}";
        var countdown = activity is null;
        activity ??= WaitingFor(tasks.Count);
        if (caller is not null && yieldSlot && run?.Lease is { IsReleased: false } lease && model is not null)
        {
            run.Lease = null;
            yielded = lease;
            // Record the yield before releasing the slot: a child that takes the slot in that window must see the
            // parent already Yielded, not still Running (the spawn+wait race, idea-6bxcsr).
            SetStatus(caller, AgentStatus.Yielded, activity);
            lease.Dispose();
        }
        else if (caller is not null)
            SetActivity(caller, activity);
        // keep the caller's activity current ("waiting for 2 agents" → "waiting for 1 agent") as workers finish
        var remaining = tasks.Count;
        var waiting = 1;
        if (caller is not null && countdown && tasks.Count > 1)
        {
            foreach (var task in tasks)
                _ = task.ContinueWith(_ =>
                {
                    var n = Interlocked.Decrement(ref remaining);
                    if (n > 0 && Volatile.Read(ref waiting) == 1) SetActivity(caller, WaitingFor(n));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, steer);
        if (timeout is { } to && to > TimeSpan.Zero && to < TimeSpan.FromDays(1)) linked.CancelAfter(to);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timeout, or the user steered the caller: return the current state
        }
        finally
        {
            Volatile.Write(ref waiting, 0);
            if (caller is not null && !ct.IsCancellationRequested)
            {
                if (yielded is not null && run is not null && model is not null)
                {
                    try
                    {
                        run.Lease = await AcquireSlotAsync(caller, run, model, YieldPriority, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // the re-acquire is bookkeeping, not the result of the wait: a model that went offline, or a
                        // budget spent while the user was typing, must not throw away the answer (idea-633b6n). The
                        // next turn's slot check reports the failure with its own message.
                        Ctx.Logger.LogWarning(ex, "Re-acquiring the slot of {Agent} after a wait failed", caller.Info.Id);
                        SetStatus(caller, AgentStatus.Queued, ex.Message);
                    }
                }
                else if (yielded is null)
                    SetActivity(caller, null);
            }
        }
    }

    public async Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default)
    {
        var target = FindState(toAgentId);
        if (target is null && Ctx.Sessions.GetSession(toAgentId) is not null) target = GetOrCreateForSession(toAgentId);
        if (target is null) return false;
        var sender = FindState(fromAgentId);
        var name = sender?.Info.Name ?? fromAgentId;
        var input = new UserInput
        {
            Text = $"<agent-message from=\"{Escape(name)} ({fromAgentId})\">\n{text.Trim()}\n</agent-message>",
            AsNotice = true,
            NoticeKind = "agent-message",
            Source = "agent:" + fromAgentId,
            Meta = new JsonObject
            {
                ["agentId"] = fromAgentId, ["agentName"] = name,
                ["sessionId"] = sender?.Info.SessionId,
            },
        };
        await DeliverAsync(target, input, mode).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AbortAsync(string agentOrSessionId)
    {
        var s = FindState(agentOrSessionId);
        if (s is null) return false;
        var run = AbortCore(s, "aborted by the user", byParent: false);
        if (run is null) return false;
        if (run.Task is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        return true;
    }

    /// <summary>Cancel the agent's run and (recursively) the runs of its children.</summary>
    internal RunState? AbortCore(AgentState s, string reason, bool byParent)
    {
        RunState? run;
        List<string> children;
        lock (s.Gate)
        {
            run = s.Run;
            children = [.. s.Info.Children];
            if (run is not null)
            {
                run.Aborted = true;
                run.CancelReason ??= reason;
                if (byParent) s.CancelledByParent = true;
            }
        }
        if (run is not null)
        {
            try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        foreach (var c in children)
            if (FindState(c) is { } child) AbortCore(child, "cancelled: parent agent was aborted", byParent: true);
        return run;
    }

    // ---------------------------------------------------------------- queue

    public IReadOnlyList<QueuedInput> GetQueue(string sessionId)
    {
        var s = FindState(sessionId);
        if (s is null) return [];
        lock (s.Gate) return s.QueueSnapshot();
    }

    /// <summary>
    /// Remove one of the person's queued inputs (agent.dequeue). Internal ones (a subagent's report, a harness notice) are
    /// refused: dropping them would lose what the agent waits for.
    /// </summary>
    public bool RemoveQueued(string sessionId, string inputId)
    {
        var s = FindState(sessionId);
        if (s is null) return false;
        string? source;
        lock (s.Gate) source = s.Steering.Concat(s.FollowUps).FirstOrDefault(i => i.Id == inputId)?.Source;
        if (source is not null && source != "user")
            throw new RpcException("forbidden", $"That queued input comes from {source}, not from you: it is for the agent and can't be removed.");
        return RemoveQueuedInput(s, inputId);
    }

    private bool RemoveQueuedInput(AgentState s, string inputId)
    {
        bool removed;
        lock (s.Gate)
        {
            removed = s.Steering.RemoveAll(i => i.Id == inputId) + s.FollowUps.RemoveAll(i => i.Id == inputId) > 0;
            if (removed)
            {
                s.Info.QueuedMessages = s.Steering.Count + s.FollowUps.Count;
                // The steer that cancelled the signal is gone: with nothing left to steer to, a guard approval, ask_user or
                // agent wait must not read the old cancel as "the user wrote a new message" and return at once.
                if (s.Steering.Count == 0 && s.SteerSignal.IsCancellationRequested) s.SteerSignal = new CancellationTokenSource();
            }
        }
        if (removed)
        {
            PublishQueue(s);
            PublishStatus(s);
        }
        return removed;
    }

    // ---------------------------------------------------------------- stop

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        List<AgentState> states;
        lock (_gate) states = [.. _agents.Values];
        var runs = new List<RunState>();
        foreach (var s in states)
        {
            lock (s.Gate)
            {
                if (s.Run is not { } r) continue;
                r.Stopping = true;
                r.Aborted = true;
                r.CancelReason = "plugin reloaded";
                runs.Add(r);
            }
        }
        foreach (var r in runs)
        {
            if (r.Lease?.LeaseId is { } leaseId) Ctx.Services.Get<IResourceLeases>()?.Update(leaseId, new ResourceLeaseUpdate { Retiring = true, CancellationRequested = true });
            try { r.Cts.Cancel(); } catch { }
        }
        try
        {
            await Task.WhenAll(runs.Select(r => r.Task ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Ctx.Logger.LogWarning(ex, "Some agent runs did not stop in time");
        }
        // EndRunAsync releases admission after the provider has actually returned. A shutdown
        // timeout cannot make a still-running call disappear from the host's physical count.
    }
}
