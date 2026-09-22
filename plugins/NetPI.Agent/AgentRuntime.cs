using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Agent;

/// <summary>Parameters for one agent run (PLAN §10).</summary>
public sealed record AgentRunOptions
{
    public string? SessionId { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
    public string? ReasoningLevel { get; init; }
    /// <summary>astra-1 E: the run's id — stamped onto this run's events.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// astra-2 §15.D: the logical assignment this run belongs to (null for
    /// ad-hoc runs with no orchestration record). Used to write/clear the
    /// durable in-flight tool-batch checkpoint around a tool batch's result
    /// persistence, so a crash in that window is recoverable (quarantined,
    /// never auto-replayed).
    /// </summary>
    public string? AssignmentId { get; init; }

    /// <summary>
    /// astra-2 §9: the logical agent identity this run belongs to (null for
    /// ad-hoc runs with no orchestration record). Used to drain the durable
    /// mailbox at model-turn boundaries.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// astra-2: the deployment/route binding this run was pinned to (trusted
    /// config, never the model). Stamped onto the ModelRequest so the provider
    /// can key chain/ownership identity on it; informational to the provider.
    /// </summary>
    public string? DeploymentId { get; init; }

    /// <summary>
    /// astra-2 §8: the run's workspace MODE as recorded on its assignment
    /// ("shared-read" / "isolated-worktree" / "shared-write"). The runtime
    /// resolves it from the orchestration store ONCE per run from the run id
    /// (null for ad-hoc runs with no orchestration record — they carry no
    /// mode and run unrestricted). "shared-read" enforces a read-only tool
    /// policy: mutating tools are withheld from the model's tool list and
    /// rejected at preflight if called.
    /// </summary>
    public string? WorkspaceMode { get; init; }

    /// <summary>
    /// astra-2 §15.B/§3.3: the lane ownership permit for a pooled run — the
    /// runner's authoritative <see cref="LaneOwnershipToken"/> for this segment.
    /// Present ONLY for pooled (lane-holding) runs. The runtime re-validates it
    /// against the lane scheduler before EVERY model call (in-run compaction
    /// included) and fails closed on a stale/missing permit; direct and legacy
    /// runs carry no token (a different authorized policy, not a bypass).
    /// </summary>
    public NetPI.Abstractions.LaneOwnershipToken? LanePermit { get; init; }
    /// <summary>Session workspace (PLAN §18/§25): tool paths + shell cwd.</summary>
    public string? Workspace { get; init; }
    public float? Temperature { get; init; }
}

/// <summary>Outcome of an agent run.</summary>
public sealed record AgentRunResult(bool Ok, AgentMessage? FinalAssistant, int Turns, string? Note);

/// <summary>
/// The agent runtime (PLAN §10-§12). Runs the model loop: request → stream →
/// tool execution → feed results, until the model stops issuing tool calls or a
/// tool execution → feed results, until the model stops issuing tool calls.
/// events as <see cref="AgentEventType.ModelStreamEvent"/> payloads. Resolves
/// its services at run time so plugin reloads defer until the run drains.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime, ISteeringQueue
{
    private readonly IPluginContext _ctx;
    private readonly IEventBus _bus;
    private readonly IServiceRegistry _services;
    /// <summary>
    /// astra-1 E (slice 2): the CURRENT run's scope, carried per task context.
    /// RunAsync sets it once at entry; every await in the run loop (and every
    /// helper it calls) sees that run's scope, and a concurrent run on another
    /// task sees ITS OWN — no shared instance state, so two sessions can run
    /// simultaneously without clobbering each other. The flow scope ends with
    /// the run task, so no manual reset is needed.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<RunScope> _runScope = new();

    /// <summary>
    /// PLAN §12: per-session steering queues. Each active session owns its own
    /// Channel&lt;QueuedUserMessage&gt;; a steer never cancels the running batch —
    /// it is drained at the turn boundary, after tool results are appended.
    /// </summary>
    private readonly ConcurrentDictionary<string, Channel<QueuedUserMessage>> _steer = new();
    /// <summary>The calling task's active run scope (null outside a run loop).</summary>
    private RunScope Current => _runScope.Value!;

    /// <summary>astra-1 E (slice 2): in-flight run scopes — the aggregate IAgentState
    /// (StateView) reads this to report "some run is active" and to pick a
    /// representative state/session. A run adds its scope at start and removes
    /// it in its finally; each scope's self-lease is disposed by the run's own
    /// finally, and the host's lease drain observes the disposal directly.</summary>
    private readonly List<RunScope> _activeScopes = new();
    private readonly object _activeLock = new();

    /// <summary>
    /// astra-1 E (slice 2): per-run mutable state — a run's AgentState, session id,
    /// usage counters and self-lease all live here so concurrent runs (different
    /// sessions) cannot clobber each other. Before this each of those was an
    /// instance field; two simultaneous runs would share one session id and one
    /// usage-counter pair. Only the per-session steering map and the in-flight
    /// scope registry remain instance-wide.
    /// </summary>
    private sealed class RunScope(string? sessionId)
    {
        /// <summary>The session id this run belongs to. Assigned at scope creation —
        /// primary-constructor parameters are NOT auto-assigned to members, so this
        /// initializer is the only binding.</summary>
        public readonly string? SessionId = sessionId;
        public AgentState State = AgentState.Idle;
        public IValueLease<object>? SelfLease;
        /// <summary>PLAN §32: prompt tokens reported by the provider on this run's LAST
        /// model request — the authoritative base for the context estimate.</summary>
        public int LastPromptTokens;
        /// <summary>PLAN §32: transcript length (message count) of this run's LAST model
        /// request — messages after this index were "added since" the provider usage.</summary>
        public int LastUsageMessageCount;
    }

    public AgentRuntime(IPluginContext context)
    {
        _ctx = context;
        _bus = context.Events;
        _services = context.Services;
    }
    private RunScope? ActiveBusiest()
    {
        lock (_activeLock)
            return _activeScopes.Count == 0 ? null : _activeScopes.MaxBy(s => (int)s.State);
    }

    private RunScope? ActiveFirst()
    {
        lock (_activeLock)
            return _activeScopes.Count == 0 ? null : _activeScopes[0];
    }

    /// <summary>Aggregate: some run is in flight right now.</summary>
    private bool AnyActive()
    {
        lock (_activeLock) return _activeScopes.Count > 0;
    }

    // ---- IAgentState -------------------------------------------------------
    public IAgentState State => new StateView(this);

    /// <summary>Aggregate view over in-flight runs (slice 2): State is the busiest
    /// active scope's state (Idle when none), IsRunning is "any run active",
    /// ActiveSessionId is a representative active session (the first, for stability).
    /// RawRunning/RawSession/RawIdle are removed — nothing outside the run loop
    /// consumed them; IsIdle is the last run's outcome and no run is active now.</summary>
    // ---- ISteeringQueue (PLAN §12, per-session) -----------------------------
    public int PendingCount(string? sessionId = null)
    {
        var ch = ChannelFor(sessionId);
        return ch?.Reader.Count ?? 0;
    }

    public ValueTask EnqueueAsync(string text, string? sessionId = null, CancellationToken ct = default)
    {
        ChannelFor(sessionId).Writer.TryWrite(new QueuedUserMessage(text));
        return ValueTask.CompletedTask;
    }

    /// <summary>Lazily create the session's steering channel (key: session id or "_global").</summary>
    private Channel<QueuedUserMessage> ChannelFor(string? sessionId)
        => _steer.GetOrAdd(sessionId ?? "_global", _ => Channel.CreateUnbounded<QueuedUserMessage>());

    /// <summary>
    /// Non-blocking poll of the run's session steering queue (null when empty).
    /// Must not wait: a steer message is injected only when one is already
    /// queued at the turn boundary, and the turn loop must keep moving.
    /// </summary>
    private ValueTask<QueuedUserMessage?> TryDequeueAsync(CancellationToken ct)
    {
        var ch = _steer.TryGetValue(Current.SessionId ?? "_global", out var c) ? c : null;
        if (ch is null || !ch.Reader.TryRead(out var msg))
            return ValueTask.FromResult<QueuedUserMessage?>(null);
        return ValueTask.FromResult<QueuedUserMessage?>(msg);
    }

    // ---- run ---------------------------------------------------------------
    public async ValueTask<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct)
    {
        // astra-1 E (slice 2): no shared running guard — capacity is enforced by
        // the runner (per-session + max-concurrent checks in its registry). Each
        // RunAsync call now owns a RunScope and never touches another run's state.
        var run = new RunScope(options.SessionId);
        var scope = run;
        _runScope.Value = run;
        lock (_activeLock) _activeScopes.Add(scope);
        // PLAN §44: hold the agent's own plugin lease for the whole run — a reload
        // cannot unload the agent plugin mid-run; the host's lease drain blocks
        // until this is released in the finally below.
        run.SelfLease = _ctx.LeaseSelf();
        run.State = ct.IsCancellationRequested ? AgentState.Cancelling : AgentState.Preparing;
        IValueLease<IModelProvider>? providerLease = null;
        IValueLease<IToolRegistry>? toolsLease = null;
        IValueLease<IModelCatalog>? catalogLease = null;
        IValueLease<ISessionStore>? storeLease = null;
        IModelProvider? provider = null;
        IToolRegistry? tools = null;
        IModelCatalog? catalog = null;
        ISessionStore? store = null;
        // astra-2 §15.B: the lane scheduler, resolved ONLY for a pooled run (one
        // that carries a LanePermit) — used to re-validate the execution permit
        // before every model call. Direct/legacy runs (no permit) never resolve it.
        NetPI.Abstractions.ILaneScheduler? lanes = null;
        int turns = 0;
        string? note = null;

        try
        {
            // astra-1 A (run cleanup): startup publication and service acquisition
            // live INSIDE the cleanup scope — a failure there releases the leases
            // and clears running state in the finally, instead of leaving the
            // runtime stuck in a "running" state with a leaked self-lease.
            await PublishAsync(AgentEventType.AgentStarting, options, null, ct);

            // astra-1 P3: operation-scoped leases for every cross-plugin service
            // this run depends on — a reload of any of those plugins cannot
            // unload it mid-run (the host's lease drain blocks until the finally
            // releases them).
            providerLease = AcquireLease<IModelProvider>("provider")
                ?? throw new ServiceUnavailableException("provider", "Model provider is not loaded.");
            toolsLease = AcquireLease<IToolRegistry>("tools");
            catalogLease = AcquireLease<IModelCatalog>("catalog");
            storeLease = AcquireLease<ISessionStore>("sessions");
            provider = providerLease.Value;
            tools = toolsLease?.Value;
            catalog = catalogLease?.Value;
            store = storeLease?.Value;
            lanes = options.LanePermit is not null ? Acquire<NetPI.Abstractions.ILaneScheduler>("lanes") : null;
            if (catalog is not null && string.IsNullOrEmpty(options.ModelId))
            {
                var models = await catalog.RefreshAsync(ct);
                if (models.Count > 0) options = options with { ModelId = models[0].ModelId };
            }
            if (string.IsNullOrEmpty(options.ModelId))
                throw new InvalidOperationException("No model selected and catalog empty.");

            var transcript = options.Messages.ToList();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                turns++;

                // ---- steering check (between turns, after tool batch) ----------
                // PLAN §12: a steering message never cancels the running tool
                // batch; it is drained here, after results are appended, and
                // injected as a User message seen by the next model call.
                var steer = await TryDequeueAsync(ct);
                if (steer is not null)
                {
                    var userMsg = new AgentMessage(NewId(), MessageRole.User, [new TextPart(steer.Text)], DateTimeOffset.UtcNow);
                    transcript.Add(userMsg);
                    if (!await AppendAsync(store, userMsg, ct))
                    {
                        // astra-1 §11a (A/B): the accepted input was not persisted —
                        // the run would proceed on a transcript the store lacks. Fail it.
                        Current.State = AgentState.Idle;
                        return new AgentRunResult(false, null, turns, "failed to persist the session entry");
                    }
                    await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);
                }

                // ---- mailbox drain (astra-2 §9) ------------------------------
                // Model-turn boundary after a complete tool batch: deliver the
                // recipient's peer messages as provenance-carrying User entries
                // (agent communication, not human instructions), bounded per
                // turn. Remaining messages stay queued for the next boundary;
                // a drain never interrupts an in-flight tool batch.
                if (await DrainMailboxAsync(options, transcript, store, ct) is DrainOutcome.PersistenceFailed)
                {
                    Current.State = AgentState.Idle;
                    return new AgentRunResult(false, null, turns, "failed to persist a mailbox message");
                }

                // ---- build + run the model ----------------------------------
                Current.State = AgentState.CallingModel;
                await PublishAsync(AgentEventType.BeforeModelRequest, options, null, ct);

                // PLAN §32: remember this request's transcript size so the
                // compaction estimate covers only the messages added since.
                Current.LastUsageMessageCount = transcript.Count;
                Current.LastPromptTokens = 0; // reset; set from the usage event below
                var request = new ModelRequest
                {
                    ModelId = options.ModelId,
                    SessionId = options.SessionId,
                    Messages = transcript,
                    Tools = options.WorkspaceMode != "shared-read" ? BuildToolDefs(tools) : BuildSharedReadToolDefs(tools),
                    Temperature = options.Temperature,
                    ReasoningLevel = options.ReasoningLevel,
                    RunId = options.RunId,
                    DeploymentId = options.DeploymentId,
                };

                AgentMessage? assistant = null;
                string? modelError = null;
                string? failedModelId = null;
                int attempt = 1;
                var retryPolicy = TryResolveRetryPolicy();
                while (true)
                {
                    assistant = null;
                    modelError = null;
                    failedModelId = null;
                    bool failed = false;
                    try
                    {
                        // PLAN §45: the hot path must not be one bus event per
                        // provider chunk. Text/thinking deltas are coalesced in a
                        // per-lane buffer and published at most every
                        // DeltaCoalesceMs (≈25 updates/s) or when the lane/boundary
                        // changes — still progressive for the UI, but the bus
                        // traffic drops from "per chunk" to a bounded rate.
                        const int DeltaCoalesceMs = 40;
                        string? deltaLane = null;
                        var deltaBuf = new System.Text.StringBuilder();
                        var lastDeltaFlush = DateTime.UtcNow;
                        // astra-2 §3.3/§15.B: re-validate the execution permit against
                        // the lane scheduler BEFORE every model call (the runner's
                        // admission is the initial grant; a stale/foreign permit —
                        // from a reloaded generation or a released lane — fails
                        // closed here, before any network I/O). In-run compaction
                        // goes through this same model call, so it is covered too.
                        if (options.LanePermit is { } permit)
                        {
                            if (lanes?.TryValidatePermit(permit, permit.PoolId, permit.AssignmentId) != true)
                            {
                                modelError = $"lane permit invalid for pool '{permit.PoolId}' (stale or released); refusing to infer";
                                _ctx.Log.Warning($"lanes: refusing inference for run {options.RunId} — permit not owned by this generation");
                                await PublishAsync(AgentEventType.ModelRequestFailed, options,
                                    new ModelEventWire { Kind = "model-failed", ModelId = request.ModelId, Error = modelError }, ct);
                                return new AgentRunResult(false, null, turns,
                                    modelError ?? "lane permit invalid");
                            }
                        }
                        await foreach (var ev in provider.RunAsync(request, ct))
                        {
                            if (ev is TextDelta td)
                            {
                                if (deltaLane is not "text")
                                {
                                    await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                                    deltaLane = "text";
                                    lastDeltaFlush = DateTime.UtcNow;
                                }
                                deltaBuf.Append(td.Text);
                                if ((DateTime.UtcNow - lastDeltaFlush).TotalMilliseconds >= DeltaCoalesceMs)
                                {
                                    await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                                    lastDeltaFlush = DateTime.UtcNow;
                                }
                                continue;
                            }
                            if (ev is ThinkingDelta th)
                            {
                                if (deltaLane is not "thinking")
                                {
                                    await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                                    deltaLane = "thinking";
                                    lastDeltaFlush = DateTime.UtcNow;
                                }
                                deltaBuf.Append(th.Text);
                                if ((DateTime.UtcNow - lastDeltaFlush).TotalMilliseconds >= DeltaCoalesceMs)
                                {
                                    await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                                    lastDeltaFlush = DateTime.UtcNow;
                                }
                                continue;
                            }
                            await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                            deltaLane = null;
                            await PublishAsync(AgentEventType.ModelStreamEvent, options, ModelEventWireMapper.ToWire(ev), ct);
                            if (ev is UsageUpdated uu) Current.LastPromptTokens = uu.PromptTokens;
                            if (ev is ModelCompleted mc) assistant = mc.Message;
                            else if (ev is ModelFailed mf)
                            {
                                modelError = mf.Error;
                                failedModelId = mf.ModelId;
                                failed = true;
                                break; // stop consuming the failed stream
                            }
                        }
                        await FlushDeltaBufAsync(deltaLane, deltaBuf, options, ct);
                        deltaLane = null;
                    }
                    catch (OperationCanceledException) { throw; } // cancellation is not retryable
                    catch (Exception ex)
                    {
                        // Providers may throw (e.g. connection refused) instead of
                        // yielding ModelFailed; treat it as one failed attempt.
                        modelError = ex.Message;
                        failedModelId = request.ModelId;
                        failed = true;
                    }
                    if (!failed) break; // success


                    await PublishAsync(AgentEventType.ModelRequestFailed, options,
                        new ModelEventWire { Kind = "model-failed", ModelId = failedModelId ?? options.ModelId ?? string.Empty, Error = modelError ?? string.Empty }, ct);

                    // Retry decision (PLAN §34): the policy plugin owns the rule.
                    var decision = retryPolicy is { IsEnabled: true }
                        ? retryPolicy.Decide(attempt, modelError ?? string.Empty)
                        : RetryDecision.Abort;
                    if (!decision.ShouldRetry)
                    {
                        Current.State = AgentState.Idle;
                        throw new Exception(modelError ?? "model request failed");
                    }

                    // PLAN §34: mark the failed attempt. If it streamed partial
                    // content, the Web client resets its in-progress assistant
                    // block on model.retrying so the retry restarts cleanly
                    // (no duplication of the partial). Persisted content is
                    // unaffected because the partial is only appended after a
                    // successful completion.
                    assistant = null;
                    await PublishAsync(AgentEventType.ModelRetrying, options,
                        new ModelEventWire { Kind = "model-retrying", ModelId = failedModelId ?? options.ModelId, Error = modelError,
                                             PromptTokens = decision.Attempt, CompletionTokens = decision.MaxAttempts, TotalTokens = decision.DelayMs }, ct);

                    // PLAN §34: failed attempt marked, then backoff wait.
                    Current.State = AgentState.Retrying;
                    if (decision.DelayMs > 0) await Task.Delay(decision.DelayMs, ct);
                    attempt++;
                }


                assistant ??= new AgentMessage(NewId(), MessageRole.Assistant, [], DateTimeOffset.UtcNow);
                transcript.Add(assistant);
                if (!await AppendAsync(store, assistant, ct))
                {
                    // astra-1 §11a (A/B): the assistant's tool-call intent was not
                    // persisted — do NOT execute tools whose calls we have no durable
                    // record of (a retry could otherwise re-run side effects).
                    Current.State = AgentState.Idle;
                    return new AgentRunResult(false, assistant, turns,
                        "failed to persist the assistant tool-call intent");
                }
                await PublishAsync(AgentEventType.AssistantCompleted, options, ModelEventWireMapper.ToWire(new ModelCompleted(assistant)), ct);

                // ---- tool calls? -------------------------------------------
                var calls = assistant.Parts.OfType<ToolCallPart>().ToList();
                if (calls.Count == 0)
                {
                    var hasText = assistant.Parts.Any(p => p is TextPart t && t.Text.Length > 0);
                    if (!hasText)
                    {
                        // Empty turn: no answer text and no tool calls — usually a
                        // provider-truncated thinking stream. Publish TurnEmpty so
                        // nudge plugins can steer a continuation and the web surface
                        // can notice the cut-off; if a nudge queued a steering
                        // message, the loop-top drain injects it as a fresh user
                        // message and we continue instead of ending on nothing.
                        Current.State = AgentState.Idle;
                        await PublishAsync(AgentEventType.TurnEmpty, options,
                            new ModelEventWire { Kind = "turn-empty", ModelId = options.ModelId }, ct);
                        if (PendingCount(Current.SessionId) > 0)
                        {
                            await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);
                            continue;
                        }
                        return new AgentRunResult(true, assistant, turns, null);
                    }
                    Current.State = AgentState.Idle;
                    return new AgentRunResult(true, assistant, turns, null);
                }

                // ---- PLAN §11: hold the tools-plugin service lease from resolution
                // through execution completion — a reload of the tools plugin cannot
                // unload mid-batch (its lease drain blocks until released below).
                Current.State = AgentState.ExecutingTools;
                using var batchToolsLease = AcquireLease<IToolRegistry>("tools")
                    ?? throw new ServiceUnavailableException("tools", "Tools registry is not loaded.");
                await PublishAsync(AgentEventType.BeforeToolBatch, options, null, ct);
                // Pre-flight (PLAN §11, sequential): validate/announce each call
                // before any execution starts. No approval UI exists, so
                // preflight = argument/tool presence validation.
                foreach (var call in calls)
                    await PublishAsync(AgentEventType.BeforeToolCall, options,
                        new ModelEventWire { Kind = "tool-call-started", ToolCallId = call.Id, ToolName = call.Name }, ct);

                // ---- PLAN §11: resolve all tools, preflight sequentially, -------
                // then execute concurrently; results keep the original call order.
                // Resolved tool instances are held by reference for the whole
                // batch so a reload cannot unload them mid-invocation.
                var batch = await ResolveAndPreflightAsync(tools, calls, options.WorkspaceMode, ct);

                // Per-call preflight outcome; invalid calls become error results
                // without executing (PLAN §11: preflight validates arguments and
                // runtime prerequisites — there is no approval UI yet).
                var execs = new List<Task<ToolResultPart>>(batch.Prepared.Count);
                foreach (var (call, tool, error) in batch.Prepared)
                {
                    if (error is not null)
                    {
                        execs.Add(Task.FromResult(new ToolResultPart(call.Id, call.Name, [new TextPart(error)], IsError: true)));
                        continue;
                    }
                    execs.Add(ExecuteToolAsync(tool!, call, options, ct));

                }
                await Task.WhenAll(execs);

                // Original call order, not completion order (PLAN §11).
                var results = new List<MessagePart>(batch.Prepared.Count);
                for (var i = 0; i < batch.Prepared.Count; i++)
                {
                    var (call, _, error) = batch.Prepared[i];
                    var res = execs[i].Result;
                    results.Add(res);
                    await PublishAsync(AgentEventType.AfterToolCall, options,
                        new ModelEventWire
                        {
                            Kind = "tool-call-completed",
                            ToolCallId = call.Id,
                            ToolName = call.Name,
                            ToolOutput = string.Join("", res.Parts.Select(x => x is TextPart t ? t.Text : x.ToString())),
                            IsError = res.IsError,
                        }, ct);
                }
                await PublishAsync(AgentEventType.AfterToolBatch, options, null, ct);

                var toolMsg = new AgentMessage(NewId(), MessageRole.Tool, results, DateTimeOffset.UtcNow);
                transcript.Add(toolMsg);
                // astra-2 §15.D: the batch EXECUTED — from here until the
                // results are persisted, a crash would leave uncertain side
                // effects. Mark it in-flight durably (load-recovery then
                // quarantines the assignment instead of silently replaying it).
                await RecordToolBatchCheckpointAsync(options, transcript.Count,
                    batch.Prepared.Select(pr => pr.Call.Id).ToList(), ct);
                if (!await AppendAsync(store, toolMsg, ct))
                {
                    // astra-1 §11a (A/B): the tools EXECUTED but their results were not
                    // persisted. Stop before the next model turn (which would run on a
                    // transcript with no durable record of these results — and any
                    // later retry could re-run a side-effecting tool). Report the
                    // uncertain recovery state; never automatically rerun the tools.
                    // (A database transaction cannot make filesystem/process side
                    // effects exactly-once; we document that limit here.)
                    Current.State = AgentState.Idle;
                    return new AgentRunResult(false, toolMsg, turns,
                        "tools executed but their results could not be persisted; recovery state is uncertain — do not automatically rerun");
                }
                // Results are durable — the uncertain window is closed.
                await ClearToolBatchCheckpointAsync(options, ct);

                // ---- auto-compaction checkpoint (PLAN §32/§33) ----------------
                // After tool results, before the next assistant response.
                var compaction = TryResolveCompaction();
                if (compaction is not null && compaction.IsAvailable && store is not null)
                {
                    Current.State = AgentState.Compacting;
                    try
                    {
                        var comp = await compaction.CompactAsync(new CompactionRequest
                        {
                            SessionId = options.SessionId ?? Current.SessionId ?? string.Empty,
                            ModelId = options.ModelId ?? string.Empty,
                            ReasoningLevel = options.ReasoningLevel,
                            LastPromptTokens = Current.LastPromptTokens,
                            LastUsageMessageCount = Current.LastUsageMessageCount,
                        }, ct);
                        if (comp is { Performed: true } && comp.ActiveContext is { } active)
                        {
                            // PLAN §31: context = system prompt + summary + retained tail.
                            // The plugin's active context is [summary, retained...];
                            // re-prepend this run's system prompt (if any).
                            var rebuilt = new List<AgentMessage>(active);
                            if (transcript.Count > 0 && transcript[0].Role == MessageRole.System)
                                rebuilt.Insert(0, transcript[0]);
                            transcript = rebuilt;
                            await PublishAsync(AgentEventType.ContextBuilt, options, null, ct);
                        }
                    }

                    catch (Exception ex) { _ctx.Log.Warning($"compaction checkpoint failed: {ex.Message}"); }
                }

                await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);

            }
        }
        catch (OperationCanceledException)
        {
            return new AgentRunResult(false, null, turns, "cancelled");
        }
        finally
        {
            run.State = AgentState.Idle;
            lock (_activeLock) _activeScopes.Remove(scope);
            // astra-1 A (run cleanup): release the run-scoped service leases —
            // nullable: acquisition happens INSIDE the try, so a pre-acquisition
            // failure must still land here and release nothing it never took.
            try { run.SelfLease?.Dispose(); } catch { }
            run.SelfLease = null;
            try { providerLease?.Dispose(); } catch { }
            try { toolsLease?.Dispose(); } catch { }
            try { catalogLease?.Dispose(); } catch { }
            try { storeLease?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// PLAN §11 preflight phase: resolve every tool of the batch and validate its
    /// arguments sequentially, BEFORE any execution starts. The tool instances
    /// are captured by reference and held for the whole batch so a reload cannot
    /// unload them mid-invocation; a lease on the shared tools registry is held
    /// for the batch duration as well.
    /// </summary>
    private Task<ToolBatch> ResolveAndPreflightAsync(IToolRegistry? tools, IReadOnlyList<ToolCallPart> calls, string? workspaceMode, CancellationToken ct)
    {
        var prepared = new List<(ToolCallPart Call, IAgentTool? Tool, string? Error)>(calls.Count);
        foreach (var call in calls)
        {
            // astra-2 §8: a shared-read workspace runs a READ-ONLY tool policy —
            // mutating tools are withheld from the model's tool list and rejected
            // here if still called, so a generic shell can never mutate the shared
            // workspace through a loophole.
            string? error = null;
            IAgentTool? tool = null;
            if (workspaceMode == "shared-read" && IsMutatingTool(call.Name))
            {
                error = $"Tool '{call.Name}' is not available: this workspace is shared-read (read-only tool policy). Use read/grep/background_output/background_list instead, or ask for the workspace to be granted shared-write or isolated-worktree.";
            }
            else
            {
                tool = tools?.Find(call.Name);
                if (tool is null)
                    error = $"Unknown tool: {call.Name}";
                else if (call.Arguments.ValueKind != JsonValueKind.Object)
                    error = $"Tool '{call.Name}' expected a JSON object of arguments.";
            }
            prepared.Add((call, tool, error));
        }
        return Task.FromResult(new ToolBatch(prepared));
    }

    /// <summary>astra-2 §8: the mutating tools withheld from a shared-read workspace.
    /// A generic shell (bash/powershell) is NOT read-only enforcement — it can write,
    /// so it is withheld; a caller needs shared-write or isolated-worktree for it.</summary>
    internal static bool IsMutatingTool(string name) =>
        name is "write" or "edit" or "bash" or "powershell" or "background_start" or "background_kill";

    private static List<ToolDefinition> BuildToolDefs(IToolRegistry? tools)
        => tools?.All().Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList() ?? [];

    /// <summary>astra-2 §8: the shared-read model sees only non-mutating tools.</summary>
    private static List<ToolDefinition> BuildSharedReadToolDefs(IToolRegistry? tools)
        => tools?.All().Where(t => !IsMutatingTool(t.Name)).Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList() ?? [];

    private sealed record ToolBatch(IReadOnlyList<(ToolCallPart Call, IAgentTool? Tool, string? Error)> Prepared);

    private async Task<ToolResultPart> ExecuteToolAsync(IAgentTool tool, ToolCallPart call, AgentRunOptions options, CancellationToken ct)
    {
        // PLAN §18/§25: every tool sees the session workspace — relative paths
        // resolve against it and foreground shells start there.
        var workspace = string.IsNullOrEmpty(options.Workspace) ? Environment.CurrentDirectory : options.Workspace!;
        // PLAN §25: progressive tool output — the stream publishes
        // ModelStreamEvent kind "tool-output-chunk" for each chunk; WebApp
        // forwards them as WS tool.output { append: true }.
        var toolStream = new ProgressiveToolStream(this, options, call);
        // astra-2: the tool sees the TRUSTED run/session identity (stamped by the
        // runtime from the run, never parsed from model arguments).
        var toolCtx = new ToolContext(call.Arguments, workspace, options.SessionId, toolStream, options.RunId);
        try
        {
            var res = await tool.ExecuteAsync(toolCtx, ct);
            return new ToolResultPart(call.Id, call.Name, res.Parts, res.IsError);
        }
        catch (Exception ex)
        {
            return new ToolResultPart(call.Id, call.Name, [new TextPart($"Tool error: {ex.Message}")], IsError: true);
        }
    }

    /// <summary>
    /// PLAN §45: publish the buffered delta run, if any. <paramref name="lane"/>
    /// is null (fresh buffer), "text", or "thinking" — it selects the wire kind.
    /// </summary>
    private async ValueTask FlushDeltaBufAsync(string? lane, System.Text.StringBuilder buf, AgentRunOptions options, CancellationToken ct)
    {
        var text = buf.ToString();
        buf.Length = 0;
        if (lane is null || text.Length == 0) return;
        var kind = lane == "text" ? "text-delta" : "thinking-delta";
        await PublishAsync(AgentEventType.ModelStreamEvent, options,
            new ModelEventWire { Kind = kind, Text = text }, ct);
    }

    private T? Acquire<T>(string id) where T : notnull
    {
        try { return _services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default!; }
    }

    /// <summary>PLAN §11: acquire a lease (not just the value) so the caller can hold it.</summary>
    private IValueLease<T>? AcquireLease<T>(string id) where T : notnull
    {
        try { return _services.Acquire<T>(id); }
        catch (ServiceUnavailableException) { return null; }
    }

    private async ValueTask<bool> AppendAsync(ISessionStore? store, AgentMessage msg, CancellationToken ct)
    {
        if (store is null || string.IsNullOrEmpty(Current.SessionId)) return true; // nothing to persist
        try
        {
            await store.AppendAsync(new SessionEntry(NewId(), Current.SessionId, EntryKind.Message, msg, null, DateTimeOffset.UtcNow), ct);
            return true;
        }
        catch (OperationCanceledException) { throw; } // cancellation is not a persistence failure
        catch (Exception ex)
        {
            // astra-1 §11a (A/B): a persistence failure is NO LONGER swallowed. The
            // caller decides how to fail the run; we keep the warning for observability.
            _ctx.Log.Warning($"Failed to persist entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// astra-2 §15.D: durable in-flight checkpoint for the executing tool batch
    /// (side effects already happened; results not yet persisted). Written only
    /// when the run belongs to a logical assignment and the orchestration store is
    /// present — otherwise there is nothing to recover.
    /// </summary>
    private async ValueTask RecordToolBatchCheckpointAsync(AgentRunOptions options, int transcriptCursor,
        IReadOnlyList<string> toolCallIds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AssignmentId)) return;
        var store = TryResolveOrchestrationStore();
        if (store is null) return;
        try
        {
            await store.MarkToolBatchInFlightAsync(options.AssignmentId, options.SessionId ?? string.Empty,
                options.SessionId ?? string.Empty, transcriptCursor,
                JsonSerializer.Serialize(toolCallIds), ct);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warning($"tool-batch checkpoint failed for {options.AssignmentId}: {ex.Message}");
        }
    }

    private async ValueTask ClearToolBatchCheckpointAsync(AgentRunOptions options, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AssignmentId)) return;
        var store = TryResolveOrchestrationStore();
        if (store is null) return;
        try
        {
            await store.ClearToolBatchCheckpointAsync(options.AssignmentId, ct);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warning($"tool-batch checkpoint clear failed for {options.AssignmentId}: {ex.Message}");
        }
    }

    /// <summary>
    /// astra-2 §9: bounded mailbox delivery at a model-turn boundary. Delivers up
    /// to <see cref="MailboxDrainMax"/> unread messages for the run's agent as
    /// User-role entries with explicit provenance (sender + kind), persisted like
    /// any other session entry. <see cref="DrainOutcome.PersistenceFailed"/> means
    /// a message was consumed but its transcript entry failed to persist — the
    /// run must stop rather than proceed on a transcript the store lacks.
    /// </summary>
    private async ValueTask<DrainOutcome> DrainMailboxAsync(AgentRunOptions options,
        List<AgentMessage> transcript, ISessionStore? store, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AgentId)) return DrainOutcome.None;
        var orch = TryResolveOrchestrationStore();
        if (orch is null) return DrainOutcome.None;
        IReadOnlyList<AgentMailboxMessage> messages;
        try
        {
            messages = await orch.DrainMailboxAsync(options.AgentId, MailboxDrainMax, ct);
        }
        catch (Exception ex)
        {
            // A mailbox outage must not break the run: messages stay undelivered
            // for the next boundary (drain is consume-on-read, so nothing is lost).
            _ctx.Log.Warning($"mailbox drain failed for {options.AgentId}: {ex.Message}");
            return DrainOutcome.None;
        }
        if (messages.Count == 0) return DrainOutcome.None;
        foreach (var m in messages)
        {
            // Provider-compatible representation: a User entry whose text carries
            // the provenance (who sent it and why) — never an invented wire role.
            var msg = new AgentMessage(NewId(), MessageRole.User,
                [new TextPart($"[agent message from {m.FromAgentId} ({m.Kind})] {m.Body}")],
                DateTimeOffset.UtcNow);
            transcript.Add(msg);
            if (store is not null && !await AppendAsync(store, msg, ct))
                return DrainOutcome.PersistenceFailed;
        }
        _ctx.Log.Information($"delivered {messages.Count} mailbox message(s) to {options.AgentId} at the turn boundary");
        return DrainOutcome.Delivered;
    }

    /// <summary>astra-2 §9: delivery cap per turn (bounded count; the rest stays queued).</summary>
    private const int MailboxDrainMax = 10;

    private enum DrainOutcome { None, Delivered, PersistenceFailed }

    /// <summary>Resolve the orchestration store if present (id "orchestration-store").</summary>
    private IOrchestrationStore? TryResolveOrchestrationStore()
    {
        try { return _services.Resolve<IOrchestrationStore>("orchestration-store"); }
        catch (ServiceUnavailableException) { return null; }
    }

    /// <summary>Resolve the AutoCompact service if present (id "compaction").</summary>
    private ICompaction? TryResolveCompaction()
    {
        try { return _services.Resolve<ICompaction>("compaction"); }
        catch (ServiceUnavailableException) { return null; }
    }

    /// <summary>Resolve the Retry plugin's policy if present (id "retry").</summary>
    private IModelRetryPolicy? TryResolveRetryPolicy()
    {
        try { return _services.Resolve<IModelRetryPolicy>("retry"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private async ValueTask PublishAsync(AgentEventType type, AgentRunOptions options, ModelEventWire? wire, CancellationToken ct)
    {
        JsonElement? payload = null;
        if (wire is not null)
            payload = JsonSerializer.SerializeToElement(wire, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var evt = new AgentEvent(Guid.NewGuid().ToString("n"), type, DateTimeOffset.UtcNow, Current.SessionId, payload, options.RunId);
        try { await _bus.PublishAsync(evt, ct); }
        catch (Exception ex) { _ctx.Log.Warning($"Event publish failed ({type}): {ex.Message}"); }
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    private sealed class StateView(AgentRuntime owner) : IAgentState
    {
        public AgentState State => owner.ActiveBusiest()?.State ?? AgentState.Idle;
        public bool IsRunning => owner.AnyActive();
        public string? ActiveSessionId => owner.ActiveFirst()?.SessionId;
        public bool IsIdle => !IsRunning;
    }

    /// <summary>
    /// PLAN §25: IToolStream implementation for the agent runtime. Each Emit
    /// publishes a ModelStreamEvent (kind "tool-output-chunk") that the Web
    /// plugin forwards as a WS <c>tool.output</c> event with <c>append: true</c>,
    /// so the UI's tool block grows live while the command runs.
    /// </summary>
    private sealed class ProgressiveToolStream :
        IToolStream
    {
        private readonly AgentRuntime _owner;
        private readonly AgentRunOptions _options;
        private readonly ToolCallPart _call;
        public ProgressiveToolStream(AgentRuntime owner, AgentRunOptions options, ToolCallPart call)
        {
            _owner = owner;
            _options = options;
            _call = call;
        }

        public void Emit(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var wire = new ModelEventWire
            {
                Kind = "tool-output-chunk",
                ToolCallId = _call.Id,
                ToolName = _call.Name,
                ToolOutput = text,
            };
            // Fire-and-forget: the chunk is best-effort progressive output; a
            // publish failure must never break the running command.
            _ = _owner.PublishAsync(AgentEventType.ModelStreamEvent, _options, wire, default);
        }
    }
}
