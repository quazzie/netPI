using NetPI.Abstractions;


namespace NetPI.Agent;

/// <summary>
/// Bridges the cross-ALC <see cref="IAgentRunner"/> contract (PLAN §10, §36)
/// to the in-process <see cref="AgentRuntime"/>. Builds the system prompt from
/// workspace context, assembles the transcript, and runs the loop in the
/// background, streaming results over the event bus.
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    private readonly AgentRuntime _runtime;
    private readonly IPluginContext _ctx;
    private readonly object _gate = new();
    /// <summary>astra-1 A (run cleanup): the owned background run — observed (not
    /// fire-and-forgotten) so plugin stop can cancel and await it.</summary>
    private Task? _runTask;
    /// <summary>astra-1 E: the run registry (RunId → owned run, active + finished).
    /// The runner — not the runtime — owns run identity (PLAN §10, Package E).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunRecord> _runs = new();
    /// <summary>astra-1 E: concurrent-run capacity (default 1; simultaneous runs only after isolation tests pass).</summary>
    private readonly int _maxConcurrentRuns;
    /// <summary>astra-1 D2 (slice 2): the run's safe-boundary side — applies a
    /// pending project change AFTER the run unwinds (its in-flight tool batch
    /// keeps its original workspace), serialized per session with the send
    /// path (shared gates).</summary>
    private readonly RunnerSafeBoundary _boundary;

    /// <summary>
    /// astra-2 §3/§4: a Pooled deployment is admitted through the lane scheduler
    /// (service <c>lanes</c>) and its model→pool policy comes from the deployment
    /// resolver (service <c>deployments</c>). Both are optional: absent services
    /// mean "no pool bound this model" → the legacy direct-execution path
    /// governed by the runner's own capacity. Resolved once at construction
    /// (the host service registry is global and load-order independent — a
    /// plugin resolves its cross-plugin peers lazily).
    /// </summary>
    private readonly NetPI.Abstractions.ILaneScheduler? _lanes;
    private readonly NetPI.Abstractions.IDeploymentPolicySource? _deployments;

    /// <summary>astra-2: accepted-but-QUEUED runs (persisted, awaiting capacity) — no live segment yet.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, QueuedRun> _queuedRuns = new();

    /// <summary>astra-2: a queued (not yet started) accepted run.</summary>
    private sealed record QueuedRun(AgentRunRequest Request, CancellationToken Cts);

    /// <summary>astra-2 §4: every execution task (runId → segment), not just the legacy _runTask.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _runTasks = new();

    public AgentRunner(AgentRuntime runtime, IPluginContext ctx, int maxConcurrentRuns = 1)
    {
        _runtime = runtime;
        _ctx = ctx;
        _maxConcurrentRuns = Math.Max(1, maxConcurrentRuns);
        _boundary = new RunnerSafeBoundary(ctx);
        _lanes = Resolve<NetPI.Abstractions.ILaneScheduler>("lanes");
        _deployments = Resolve<NetPI.Abstractions.IDeploymentPolicySource>("deployments");
        // astra-2 §3.1: when the scheduler supports the admission sink, subscribe
        // so a queued segment starts the moment its lane frees (FIFO).
        if (_lanes is NetPI.Abstractions.ILaneAdmissionSink sink)
            sink.OnAdmittedFromQueue(OnLaneAdmitted);
    }

    /// <summary>astra-1 D2 (slice 2): the per-session gate the send path
    /// (StartRunAsync) and the boundary apply serialize on — exposed for the
    /// Web surface's session.project command (an idle apply takes it too).</summary>
    public System.Threading.SemaphoreSlim SessionGate(string sessionId) => _boundary.Gate(sessionId);

    /// <summary>astra-1 D2 (slice 2): a project change requested on an IDLE
    /// session applies immediately (same gate the send path uses — a start
    /// that races in waits on it). Returns the applied operation id, or null
    /// (no pending row / stores unavailable / a run took the session first).</summary>
    public async ValueTask<string?> ApplyPendingProjectChangeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var applied = await _boundary.ApplyPendingAsync(sessionId, cancellationToken);
        return applied?.OperationId;
    }

    /// <summary>astra-1 A (run cleanup): the owned background run task (null when none).</summary>
    public Task? RunTask
    {
        get { lock (_gate) return _runTask; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _runs.Values.Any(r => r.Outcome == RunState.Running); }
    }

    /// <summary>astra-1 E: all owned runs (active + finished), newest first.</summary>
    public IReadOnlyList<RunInfo> ListRuns()
    {
        lock (_gate)
        {
            return _runs.Values
                .OrderByDescending(r => r.StartTime)
                .Select(r => r.Info)
                .ToList();
        }
    }

    /// <summary>astra-1 E: a specific run by id (null when unknown).</summary>
    public RunInfo? GetRun(string runId)
    {
        if (string.IsNullOrEmpty(runId)) return null;
        lock (_gate) return _runs.TryGetValue(runId, out var r) ? r.Info : null;
    }

    /// <summary>astra-1 E: the ACTIVE run for a session (null when none).</summary>
    public RunInfo? GetSessionRun(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            var rec = _runs.Values.FirstOrDefault(r =>
                r.SessionId == sessionId && r.Outcome == RunState.Running);
            return rec?.Info;
        }
    }

    /// <summary>astra-1 E: cancel one specific run. True when a live run was signalled.</summary>
    public bool CancelRun(string runId)
    {
        if (string.IsNullOrEmpty(runId)) return false;
        lock (_gate)
        {
            if (_runs.TryGetValue(runId, out var rec) && rec.Outcome == RunState.Running)
            {
                rec.Cts?.Cancel();
                return true;
            }
            return false;
        }
    }

    /// <summary>astra-1 E: one owned run — identity + cancellation + terminal outcome.
    /// The runner (not the runtime) is the owner of run state.</summary>
    private sealed class RunRecord(
        string runId, string? sessionId, string? modelId, DateTimeOffset startTime, CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        /// <summary>astra-1 E: when the run started (registry ordering — newest first).</summary>
        public DateTimeOffset StartTime => _start;
        /// <summary>astra-1 E: the run's id (registry key).</summary>
        public string RunId => _runId;
        /// <summary>astra-1 E: the session this run belongs to (null for ad-hoc runs).</summary>
        public string? SessionId => _sessionId;
        public string? ModelId => _modelId;
        private readonly string _runId = runId;
        private readonly string? _sessionId = sessionId;
        private readonly string? _modelId = modelId;
        private readonly DateTimeOffset _start = startTime;
        /// <summary>Mutable — flips to a terminal outcome exactly once.</summary>
        public RunState Outcome { get; set; } = RunState.Running;
        public DateTimeOffset? EndTime { get; set; }
        /// <summary>The runtime state a completed/cancelled/failed run reflects.</summary>
        public AgentState State { get; set; } = AgentState.Preparing;

        /// <summary>astra-2: the lane token this segment holds (Pooled deployments only). Released once on drain.</summary>
        public NetPI.Abstractions.LaneOwnershipToken? LaneToken { get; set; }

        /// <summary>astra-2: this run's pool + deployment binding (null for direct execution).</summary>
        public (string PoolId, string DeploymentId)? LaneBinding { get; set; }

        public RunInfo Info => new(_runId, _sessionId, _modelId, State, _start, EndTime, Outcome);
    }
    public async ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        // astra-1 A (run cleanup): no text → the send fails (never a silent
        // empty run), and the gate is never taken.
        if (string.IsNullOrWhiteSpace(request.Text))
            return new AgentRunStart(request.SessionId, "Empty message — nothing to send.");

        // astra-1 D2 (slice 2): per-session gate — the send's critical
        // section (accept + persist the user entry) is serialized with the
        // boundary apply, so a send that lands the instant a run unwinds
        // sees the post-switch state (the apply holds the gate until commit).
        var sessionGate = (System.Threading.SemaphoreSlim?)(!string.IsNullOrEmpty(request.SessionId) ? _boundary.Gate(request.SessionId) : null);
        if (sessionGate is not null && !sessionGate.Wait(System.TimeSpan.FromMilliseconds(2000)))
            return new AgentRunStart(request.SessionId, "This session is switching projects — try again in a moment.");

        RunRecord rec = null!;
        try
        {
            bool sessionBusy = false;
            bool capacityFull = false;
            lock (_gate)
            {
            if (!string.IsNullOrEmpty(request.SessionId) &&
                _runs.Values.Any(r => r.SessionId == request.SessionId && r.Outcome == RunState.Running))
                sessionBusy = true;
            var active = _runs.Values.Count(r => r.Outcome == RunState.Running);
            if (active >= _maxConcurrentRuns)
                capacityFull = true;
            var runId = Guid.NewGuid().ToString("n");
            rec = new RunRecord(runId, request.SessionId, request.ModelId, DateTimeOffset.UtcNow, new CancellationTokenSource());
            _runs[runId] = rec;
            }

            // astra-2 §4/§13: admission by deployment policy, decided OUTSIDE the
            // lock (a pooled model may await the lane scheduler; a direct model
            // is governed only by the runner's own capacity). A Pooled deployment
            // that cannot be admitted is ACCEPTED + QUEUED (full capacity is a
            // queue, never a rejection); a Direct deployment at runner capacity
            // is still refused (the pre-lane behavior).
            var policy = _deployments?.PolicyFor(request.ModelId ?? string.Empty);
            var disposition = RunDisposition.Admitted;
            string? queueReason = null;
            if (!sessionBusy && !capacityFull)
            {
                if (policy is { RequiresLane: true })
                {
                    if (_lanes is null)
                    { disposition = RunDisposition.Queued; queueReason = "no lane scheduler available"; }
                    else
                    {
                        var entry = new LaneQueueEntry(
                            rec.RunId, policy.PoolId!, policy.DeploymentId, 0,
                            null, rec.RunId, request.SessionId,
                            request.Text.Length <= 48 ? request.Text : request.Text[..48],
                            DateTimeOffset.UtcNow);
                        var result = await _lanes.AcquireAsync(entry);
                        if (result.Token is not null)
                        {
                            rec.LaneToken = result.Token;
                            rec.LaneBinding = (policy.PoolId!, policy.DeploymentId);
                        }
                        else
                        {
                            disposition = RunDisposition.Queued;
                            queueReason = result.BlockedReason;
                        }
                    }
                }
            }

        // Persist the initial user entry (the agent runtime does not persist
        // the initial input message itself). astra-1 A: a persistence failure
        // FAILS the send — a run whose first message was never stored would
        // execute tools and continue turns on a lost transcript.
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            try
            {
                var store = _ctx.Services.Resolve<ISessionStore>("sessions");
                var userMessageId = MessageIdentity.DeterministicId("user", request.Text);
                var user = new AgentMessage(userMessageId, MessageRole.User,
                    [new TextPart(request.Text)], DateTimeOffset.UtcNow);
                await store.AppendAsync(new SessionEntry(userMessageId,
                    request.SessionId, EntryKind.Message, user, null, DateTimeOffset.UtcNow), cancellationToken);
            }
            catch (Exception ex)
            {
                await ReleaseLaneAsync(rec);
                RemoveRun(rec);
                return new AgentRunStart(request.SessionId, $"Failed to persist the message: {ex.Message}");
            }
        }

        // astra-2 §3.1: the admission OUTCOME decides what happens next.
        if (sessionBusy)
        {
            await ReleaseLaneAsync(rec);
            RemoveRun(rec);
            return new AgentRunStart(request.SessionId, "This session already has an active run.");
        }
        if (disposition == RunDisposition.Queued)
        {
            // Accepted + QUEUED: stored as durable work (a record, not a live
            // task) that starts ONLY when its lane frees (astra-2 §3.1/§5.1).
            _queuedRuns[rec.RunId] = new QueuedRun(request, rec.Cts.Token);
            return new AgentRunStart(request.SessionId,
                string.IsNullOrEmpty(queueReason) ? "Queued: waiting for a free lane."
                                                 : $"Queued: {queueReason}.",
                rec.RunId, RunDisposition.Queued);
        }
        if (capacityFull)
        {
            // A Direct model at the runner's own capacity is still refused
            // (the pre-lane behavior — astra-2 §11: direct bypasses lanes).
            RemoveRun(rec);
            return new AgentRunStart(request.SessionId, "All concurrent runs are busy.");
        }

        // Admitted (direct, or a pooled model that got a lane token NOW): run it.
        _runTasks[rec.RunId] = Task.Run(() => ExecuteAsync(request, rec));
        _runTask = _runTasks[rec.RunId];
        return new AgentRunStart(request.SessionId, null, rec.RunId);
        }
        finally
        {
            sessionGate?.Release();
        }
    }

    /// <summary>astra-2: release a run's lane token (idempotent no-op when none) — the
    /// ownership authority returns to the scheduler so the queue advances.</summary>
    private async Task ReleaseLaneAsync(RunRecord rec)
    {
        if (rec.LaneToken is not { } token || _lanes is null) return;
        rec.LaneToken = null;
        try { await _lanes.ReleaseAsync(token); }
        catch { /* a failed release never strands a run; the scheduler fences it */ }
    }

    /// <summary>astra-2: drop an unstarted run record (and any queued placeholder).</summary>
    private void RemoveRun(RunRecord rec)
    {
        lock (_gate)
        {
            rec.Cts.Dispose();
            ((System.Collections.Generic.IDictionary<string, RunRecord>)_runs).Remove(rec.RunId);
            if (_runTask is not null && !ReferenceEquals(_runTask, Task.CompletedTask))
            { /* _runTask is only set for started runs */ }
        }
        _queuedRuns.TryRemove(rec.RunId, out _);
    }

    /// <summary>
    /// astra-2 §3.1: the ONLY trigger that starts a stored queued segment — the
    /// scheduler fires it (outside its own gate) the instant a queued lane is
    /// admitted. A queued record holds NO live task; this pop-then-starts it.
    /// </summary>
    private void OnLaneAdmitted(LaneOwnershipToken token)
    {
        if (token is null) return;
        if (!_queuedRuns.TryGetValue(token.RunId, out var queued)) return;
        if (!_queuedRuns.TryRemove(token.RunId, out _)) return;
        lock (_gate)
        {
            if (!_runs.TryGetValue(token.RunId, out var rec)) return;
            rec.LaneToken = token;
            var pol = _deployments?.PolicyFor(rec.ModelId ?? string.Empty);
            rec.LaneBinding = pol?.RequiresLane == true ? (pol.PoolId!, pol.DeploymentId) : rec.LaneBinding;
            _runTask = Task.Run(() => ExecuteAsync(queued.Request, rec));
            _runTasks[token.RunId] = _runTask;
        }
    }

    public ValueTask CancelRunAsync(CancellationToken cancellationToken = default)
    {
        var live = new List<CancellationTokenSource>();
        lock (_gate)
        {
            foreach (var r in _runs.Values)
                if (r.Outcome == RunState.Running)
                    live.Add(r.Cts);
        }
        foreach (var c in live) c.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// astra-1 A (run cleanup): cancel the owned run and AWAIT it (bounded) so a
    /// plugin stop never releases a still-executing run. The wait is bounded by
    /// design — a run wedged inside a non-cooperative await cannot be force-
    /// stopped in-process; it is reported, not waited on forever.
    /// </summary>
    public async ValueTask<bool> WaitForRunAsync(TimeSpan bound)
    {
        await CancelRunAsync(default);
        Task? task;
        lock (_gate) task = _runTask;
        if (task is null) return true;
        var done = await Task.WhenAny(task, Task.Delay(bound));
        if (done != task)
        {
            _ctx.Log.Warning("agent run did not quiesce within the stop bound; releasing it as-is");
            return false;
        }
        try { await task; } catch { /* the terminal event already reported it */ }
        return true;
    }

    private async Task ExecuteAsync(AgentRunRequest request, RunRecord run)
    {
        var cts = run.Cts;
        string? sessionId = request.SessionId;
        bool cancelled = false;
        bool failed = false;
        try
        {
            var workspace = string.IsNullOrEmpty(request.WorkspacePath)
                ? Environment.CurrentDirectory : request.WorkspacePath;
            var systemText = await BuildSystemPromptAsync(workspace);

        // astra-1 A (message identity): the run's user message reuses the EXACT
        // message StartRunAsync persisted — same ID and timestamp — instead of
        // reconstructing a fresh one with a different ID (identities participate
        // in Responses fingerprints). Text-based tail de-duplication is gone:
        // identical consecutive user messages are legitimate.
        var userMessageId = MessageIdentity.DeterministicId("user", request.Text);
        var transcript = await BuildTranscriptAsync(request, systemText, userMessageId, cts.Token);


            var result = await _runtime.RunAsync(new AgentRunOptions
            {
                SessionId = sessionId,
                RunId = run.RunId,
                ModelId = request.ModelId,
                Messages = transcript,
                ReasoningLevel = request.ReasoningLevel,
                Temperature = request.Temperature,
                Workspace = workspace,
            }, cts.Token);
            // astra-1 A: the RUNTIME result carries the terminal outcome —
            // cancellation comes back as a result (note "cancelled"), not a
            // throw. Exactly one of completed/cancelled/failed is recorded.
            if (!result.Ok)
            {
                if (string.Equals(result.Note, "cancelled", StringComparison.Ordinal))
                    cancelled = true;
                else
                    failed = true;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failed = true;
            _ctx.Log.Error("Agent run failed", ex);
        }
        finally
        {
            // astra-1 A (run cleanup): clear the running state BEFORE the
            // terminal notification — IsRunning must not report a finished
            // run, and a new send must not be refused by a dead one.
            lock (_gate)
            {
                // astra-1 E: record the terminal outcome on the owned run EXACTLY ONCE.
                // IsRunning / GetSessionRun flip off the instant this lands, so a new
                // send is never refused by a dead run.
                run.Outcome = cancelled ? RunState.Cancelled : failed ? RunState.Failed : RunState.Completed;
                run.EndTime = DateTimeOffset.UtcNow;
                run.State = AgentState.Idle;
                cts.Dispose();
                _runTask = null;
            }

            // astra-2 §5.1: the segment has drained (model unwound / tool batch
            // finished or cancelled) — return the lane to the scheduler so the
            // FIFO queue can admit the next assignment. Release OUTSIDE the lock
            // (it awaits the scheduler's own gate). Idempotent: a double release
            // or a stale epoch is a no-op, never an error.
            if (run.LaneToken is not null)
                await ReleaseLaneAsync(run);

            // astra-2 §4: the execution task is done; drop it from the registry.
            _runTasks.TryRemove(run.RunId, out _);

            // astra-1 A: exactly one terminal outcome per accepted run —
            // completed, cancelled or FAILED (a crashed loop must not be
            // reported as a completion).
            try
            {
                var type = cancelled ? AgentEventType.AgentCancelled
                    : failed ? AgentEventType.AgentFailed
                    : AgentEventType.AgentCompleted;
                var evt = new AgentEvent(Guid.NewGuid().ToString("n"), type, DateTimeOffset.UtcNow, sessionId, null, run.RunId);
                await _ctx.Events.PublishAsync(evt, CancellationToken.None);
            }
            catch { /* bus may already be gone */ }

            // astra-1 D2 (slice 2): the SAFE BOUNDARY — the run has unwound
            // (its in-flight tool batch keeps its original workspace, so a
            // pending project change can only be applied NOW: after unwind,
            // before the session is usable for the next send). The resulting
            // ProjectApplied event is bridged by the Web surface to
            // session.project.applied + session.updated.
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    var applied = await _boundary.ApplyPendingAsync(sessionId, CancellationToken.None);
                    if (applied is not null)
                    {
                        var appliedEvt = new AgentEvent(
                            Guid.NewGuid().ToString("n"), AgentEventType.ProjectApplied,
                            DateTimeOffset.UtcNow, sessionId,
                            System.Text.Json.JsonSerializer.SerializeToElement(new
                            {
                                operationId = applied.OperationId,
                                projectId = applied.ProjectId,
                                projectName = applied.ProjectName,
                            }),
                            run.RunId);
                        await _ctx.Events.PublishAsync(appliedEvt, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _ctx.Log.Warning($"boundary apply of pending project change failed: {ex.Message}");
                }
            }
        }
    }



    private async Task<string> BuildSystemPromptAsync(string workspace)
    {
        try
        {
            var builder = Resolve<IWorkspaceContextBuilder>("workspace-context");
            var provider = Resolve<ISystemPromptProvider>("system-prompt");
            if (builder is null || provider is null) return string.Empty;
            var inputs = await builder.BuildAsync(workspace, CancellationToken.None);
            var tools = Resolve<IToolRegistry>("tools");
            var allTools = tools?.All() ?? [];
            var toolDefs = allTools.Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList();
            // PLAN §15: per-tool behavioral guidance (IAgentTool.Guidelines) joins
            // the prompt as a "Tool Guidelines" section.
            var guidelines = allTools.SelectMany(t => t.Guidelines ?? []).Distinct().ToList();
            inputs = inputs with { Tools = toolDefs, ToolGuidelines = guidelines };
            return await provider.BuildAsync(inputs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warning($"System prompt build failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Build the transcript for a run (PLAN §31/§33): system prompt first, then
    /// the active model context (compaction summary + retained tail when a
    /// compaction has occurred, otherwise recent history). The current run's
    /// user message was already persisted by StartRunAsync and is the newest
    /// stored message.
    /// </summary>
    private async Task<List<AgentMessage>> BuildTranscriptAsync(
        AgentRunRequest request, string systemText, string userMessageId, CancellationToken ct)
    {
        var transcript = new List<AgentMessage>();

        if (!string.IsNullOrWhiteSpace(systemText))
            transcript.Add(new AgentMessage(
                MessageIdentity.DeterministicId("system", systemText), MessageRole.System,
                [new TextPart(systemText)], DateTimeOffset.UtcNow));

        // Active context: via the AutoCompact plugin (handles compaction
        // reconstruction), else straight from the store.
        try
        {
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                var compaction = Resolve<ICompaction>("compaction");
                var context = new List<AgentMessage>();
                if (compaction is not null)
                    context.AddRange(await compaction.BuildActiveContextAsync(request.SessionId, ct));

                if (context.Count == 0)
                {
                    // astra-1 A: bounded RECENT tail (newest first in the store,
                    // returned oldest first) — never an oldest-first window, which
                    // silently substituted old history for current history on a
                    // session longer than the window.
                    var store = Resolve<ISessionStore>("sessions");
                    if (store is not null)
                        context = (await store.ReadRecentAsync(request.SessionId, 200, ct))
                            .Where(e => e.Kind == EntryKind.Message && e.Message is not null)
                            .Select(e => e.Message!)
                            .ToList();
                }
                // The run's user message is re-added below with its PERSISTED
                // identity (userMessageId): if the active context already
                // contains it (retained tail after StartRunAsync persisted it),
                // drop it by ID — no text-based de-duplication (identical
                // consecutive user messages are legitimate).
                for (var i = context.Count - 1; i >= 0; i--)
                    if (context[i].Id == userMessageId) context.RemoveAt(i);
                // PLAN §46: a run that died mid-batch (crash/restart) leaves tool
                // calls without results — the provider rejects the transcript
                // ("function_call_output must contain a non-empty call_id"). Repair
                // the history with synthetic interrupted results before it is sent.
                context = TranscriptSanitizer.Sanitize(context).ToList();
                transcript.AddRange(context);
                // astra-1 D: the ACTIVE project snapshot is a ProjectContext entry,
                // which the compaction tail never carries (it filters
                // EntryKind.Message only) — reconstruct it EXACTLY from its
                // persisted snapshot and place it after the history but before
                // this run's user message. Fresh session → between the system
                // prompt and the first user message; existing session → after
                // the whole conversation. Deterministic id (derived from the
                // entry id) keeps provider fingerprints stable and never stores
                // a duplicate message.
                await AppendActiveProjectContextAsync(transcript, request.SessionId, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // astra-1 A (run cleanup): a failed context build is NOT silently
            // replaced with a user-only prompt — the run fails (the runner
            // reports it as AgentFailed), which is louder and recoverable than
            // quietly sending a degraded transcript to the model.
            _ctx.Log.Error($"context build failed: {ex.Message}");
            throw;
        }

        transcript.Add(new AgentMessage(userMessageId, MessageRole.User,
            [new TextPart(request.Text)], DateTimeOffset.UtcNow));
        return transcript;
    }


    /// <summary>
    /// astra-1 D: append the session's ACTIVE project snapshot to the transcript
    /// (reconstructed EXACTLY from its persisted <see cref="EntryKind.ProjectContext"/>
    /// entry — it survives compaction, reconnect and restart because it is read
    /// from the store, not carried in the compaction tail). No-op when the
    /// session has no attached project. The projected message id is derived
    /// from the entry id, so re-building the transcript across runs is stable
    /// and never produces a duplicate.
    /// </summary>
    private async Task AppendActiveProjectContextAsync(
        List<AgentMessage> transcript, string sessionId, CancellationToken ct)
    {
        var store = Resolve<ISessionStore>("sessions");
        if (store is null) return;
        var active = await store.ActiveProjectContextAsync(sessionId, ct);
        if (active is null) return;
        var message = ProjectContextProjection.Project(active);
        if (message is not null)
            transcript.Add(message);
    }

    private T? Resolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }
    }

    private static string TextOf(AgentMessage m)
        => string.Join("\n", m.Parts.OfType<TextPart>().Select(p => p.Text));


}
