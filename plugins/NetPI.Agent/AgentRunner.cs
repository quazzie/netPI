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
    public AgentRunner(AgentRuntime runtime, IPluginContext ctx, int maxConcurrentRuns = 1)
    {
        _runtime = runtime;
        _ctx = ctx;
        _maxConcurrentRuns = Math.Max(1, maxConcurrentRuns);
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
        private readonly string _runId = runId;
        private readonly string? _sessionId = sessionId;
        private readonly string? _modelId = modelId;
        private readonly DateTimeOffset _start = startTime;
        /// <summary>Mutable — flips to a terminal outcome exactly once.</summary>
        public RunState Outcome { get; set; } = RunState.Running;
        public DateTimeOffset? EndTime { get; set; }
        /// <summary>The runtime state a completed/cancelled/failed run reflects.</summary>
        public AgentState State { get; set; } = AgentState.Preparing;

        public RunInfo Info => new(_runId, _sessionId, _modelId, State, _start, EndTime, Outcome);
    }
    public async ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        // astra-1 A (run cleanup): no text → the send fails (never a silent
        // empty run), and the gate is never taken.
        if (string.IsNullOrWhiteSpace(request.Text))
            return new AgentRunStart(request.SessionId, "Empty message — nothing to send.");

        RunRecord rec = null!;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(request.SessionId) &&
                _runs.Values.Any(r => r.SessionId == request.SessionId && r.Outcome == RunState.Running))
                return new AgentRunStart(request.SessionId, "This session already has an active run.");
            var active = _runs.Values.Count(r => r.Outcome == RunState.Running);
            if (active >= _maxConcurrentRuns)
                return new AgentRunStart(request.SessionId, "All concurrent runs are busy.");
            var runId = Guid.NewGuid().ToString("n");
            rec = new RunRecord(runId, request.SessionId, request.ModelId, DateTimeOffset.UtcNow, new CancellationTokenSource());
            _runs[runId] = rec;
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
                lock (_gate)
                {
                    rec.Outcome = RunState.Failed;
                    rec.EndTime = DateTimeOffset.UtcNow;
                    rec.Cts.Dispose();
                    ((System.Collections.Generic.IDictionary<string, RunRecord>)_runs).Remove(rec.RunId);
                }
                return new AgentRunStart(request.SessionId, $"Failed to persist the message: {ex.Message}");
            }
        }
        _runTask = Task.Run(() => ExecuteAsync(request, rec));

        return new AgentRunStart(request.SessionId, null, rec.RunId);

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


    private T? Resolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }
    }

    private static string TextOf(AgentMessage m)
        => string.Join("\n", m.Parts.OfType<TextPart>().Select(p => p.Text));


}
