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
    /// PLAN §44: this plugin generation's own lease, held for the whole run so a
    /// reload cannot unload the agent plugin mid-run.
    /// </summary>
    private IValueLease<object>? _selfLease;
    private volatile AgentState _state = AgentState.Idle;
    private string? _activeSession;
    private bool _everRan;
    /// <summary>
    /// PLAN §12: per-session steering queues. Each active session owns its own
    /// Channel&lt;QueuedUserMessage&gt;; a steer never cancels the running batch —
    /// it is drained at the turn boundary, after tool results are appended.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Channel<QueuedUserMessage>> _steer = new();

    /// <summary>PLAN §32: prompt tokens reported by the provider on the LAST model
    /// request — the authoritative base for the context estimate.</summary>
    private volatile int _lastUsagePromptTokens;
    /// <summary>PLAN §32: transcript length (message count) of the LAST model
    /// request — messages after this index were "added since" the provider usage.</summary>
    private volatile int _lastUsageMessageCount;

    public AgentRuntime(IPluginContext context)
    {
        _ctx = context;
        _bus = context.Events;
        _services = context.Services;
    }

    // ---- IAgentState -------------------------------------------------------
    public IAgentState State => new StateView(this);
    internal AgentState RawState => _state;
    internal bool RawRunning => _state != AgentState.Idle;
    internal string? RawSession => _activeSession;
    internal bool RawIdle => _everRan && _state == AgentState.Idle;

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
        var ch = _steer.TryGetValue(_activeSession ?? "_global", out var c) ? c : null;
        if (ch is null || !ch.Reader.TryRead(out var msg))
            return ValueTask.FromResult<QueuedUserMessage?>(null);
        return ValueTask.FromResult<QueuedUserMessage?>(msg);
    }

    // ---- run ---------------------------------------------------------------
    public async ValueTask<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct)
    {
        if (RawRunning)
            throw new InvalidOperationException("An agent run is already in progress.");
        // PLAN §44: hold the agent's own plugin lease for the whole run — a reload
        // cannot unload the agent plugin mid-run; the host's lease drain blocks
        // until this is released in the finally below.
        _selfLease = _ctx.LeaseSelf();
        _state = ct.IsCancellationRequested ? AgentState.Cancelling : AgentState.Preparing;
        _activeSession = options.SessionId;
        await PublishAsync(AgentEventType.AgentStarting, options, null, ct);

        var provider = Acquire<IModelProvider>("provider")
            ?? throw new ServiceUnavailableException("provider", "Model provider is not loaded.");
        var tools = Acquire<IToolRegistry>("tools");
        var catalog = Acquire<IModelCatalog>("catalog");
        var store = Acquire<ISessionStore>("sessions");
        int turns = 0;
        string? note = null;

        try
        {
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
                    await AppendAsync(store, userMsg, ct);
                    await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);
                }

                // ---- build + run the model ----------------------------------
                _state = AgentState.CallingModel;
                await PublishAsync(AgentEventType.BeforeModelRequest, options, null, ct);

                // PLAN §32: remember this request's transcript size so the
                // compaction estimate covers only the messages added since.
                _lastUsageMessageCount = transcript.Count;
                _lastUsagePromptTokens = 0; // reset; set from the usage event below
                var request = new ModelRequest
                {
                    ModelId = options.ModelId,
                    SessionId = options.SessionId,
                    Messages = transcript,
                    Tools = tools?.All().Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList() ?? [],
                    Temperature = options.Temperature,
                    ReasoningLevel = options.ReasoningLevel,
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
                            if (ev is UsageUpdated uu) _lastUsagePromptTokens = uu.PromptTokens;
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
                        _state = AgentState.Idle;
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
                    _state = AgentState.Retrying;
                    if (decision.DelayMs > 0) await Task.Delay(decision.DelayMs, ct);
                    attempt++;
                }


                assistant ??= new AgentMessage(NewId(), MessageRole.Assistant, [], DateTimeOffset.UtcNow);
                transcript.Add(assistant);
                await AppendAsync(store, assistant, ct);
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
                        _state = AgentState.Idle;
                        await PublishAsync(AgentEventType.TurnEmpty, options,
                            new ModelEventWire { Kind = "turn-empty", ModelId = options.ModelId }, ct);
                        if (PendingCount(_activeSession) > 0)
                        {
                            await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);
                            continue;
                        }
                        return new AgentRunResult(true, assistant, turns, null);
                    }
                    _state = AgentState.Idle;
                    return new AgentRunResult(true, assistant, turns, null);
                }

                // ---- PLAN §11: hold the tools-plugin service lease from resolution
                // through execution completion — a reload of the tools plugin cannot
                // unload mid-batch (its lease drain blocks until released below).
                _state = AgentState.ExecutingTools;
                using var toolsLease = AcquireLease<IToolRegistry>("tools")
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
                var batch = await ResolveAndPreflightAsync(tools, calls, ct);

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
                await AppendAsync(store, toolMsg, ct);

                // ---- auto-compaction checkpoint (PLAN §32/§33) ----------------
                // After tool results, before the next assistant response.
                var compaction = TryResolveCompaction();
                if (compaction is not null && compaction.IsAvailable && store is not null)
                {
                    _state = AgentState.Compacting;
                    try
                    {
                        var comp = await compaction.CompactAsync(new CompactionRequest
                        {
                            SessionId = options.SessionId ?? _activeSession ?? string.Empty,
                            ModelId = options.ModelId ?? string.Empty,
                            ReasoningLevel = options.ReasoningLevel,
                            LastPromptTokens = _lastUsagePromptTokens,
                            LastUsageMessageCount = _lastUsageMessageCount,
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
            _state = AgentState.Idle;
            _everRan = true;
            try { _selfLease?.Dispose(); } catch { }
            _selfLease = null;
        }
    }

    /// <summary>
    /// PLAN §11 preflight phase: resolve every tool of the batch and validate its
    /// arguments sequentially, BEFORE any execution starts. The tool instances
    /// are captured by reference and held for the whole batch so a reload cannot
    /// unload them mid-invocation; a lease on the shared tools registry is held
    /// for the batch duration as well.
    /// </summary>
    private Task<ToolBatch> ResolveAndPreflightAsync(IToolRegistry? tools, IReadOnlyList<ToolCallPart> calls, CancellationToken ct)
    {
        var prepared = new List<(ToolCallPart Call, IAgentTool? Tool, string? Error)>(calls.Count);
        foreach (var call in calls)
        {
            string? error = null;
            var tool = tools?.Find(call.Name);
            if (tool is null)
                error = $"Unknown tool: {call.Name}";
            else if (call.Arguments.ValueKind != JsonValueKind.Object)
                error = $"Tool '{call.Name}' expected a JSON object of arguments.";
            prepared.Add((call, tool, error));
        }
        return Task.FromResult(new ToolBatch(prepared));
    }

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
        var toolCtx = new ToolContext(call.Arguments, workspace, options.SessionId, toolStream);
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

    private async ValueTask AppendAsync(ISessionStore? store, AgentMessage msg, CancellationToken ct)
    {
        if (store is null || string.IsNullOrEmpty(_activeSession)) return;
        try
        {
            await store.AppendAsync(new SessionEntry(NewId(), _activeSession, EntryKind.Message, msg, null, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex) { _ctx.Log.Warning($"Failed to persist entry: {ex.Message}"); }
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
        var evt = new AgentEvent(Guid.NewGuid().ToString("n"), type, DateTimeOffset.UtcNow, _activeSession, payload);
        try { await _bus.PublishAsync(evt, ct); }
        catch (Exception ex) { _ctx.Log.Warning($"Event publish failed ({type}): {ex.Message}"); }
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    private sealed class StateView(AgentRuntime owner) : IAgentState
    {
        public AgentState State => owner.RawState;
        public bool IsRunning => owner.RawRunning;
        public string? ActiveSessionId => owner.RawSession;
        public bool IsIdle => owner.RawIdle;
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
