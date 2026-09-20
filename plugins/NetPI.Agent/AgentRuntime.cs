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
    public float? Temperature { get; init; }
    public int? MaxTurns { get; init; } = 32;
}

/// <summary>Outcome of an agent run.</summary>
public sealed record AgentRunResult(bool Ok, AgentMessage? FinalAssistant, int Turns, string? Note);

/// <summary>
/// The agent runtime (PLAN §10-§12). Runs the model loop: request → stream →
/// tool execution → feed results, until the model stops issuing tool calls or a
/// turn cap is hit. Publishes lifecycle events on the bus and streams model
/// events as <see cref="AgentEventType.ModelStreamEvent"/> payloads. Resolves
/// its services at run time so plugin reloads defer until the run drains.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime, ISteeringQueue
{
    private readonly IPluginContext _ctx;
    private readonly IEventBus _bus;
    private readonly IServiceRegistry _services;

    private volatile AgentState _state = AgentState.Idle;
    private string? _activeSession;
    private bool _everRan;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _steer = new();

    public AgentRuntime(IPluginContext context)
    {
        _ctx = context;
        _bus = context.Events;
        _services = context.Services;
    }

    // ---- IAgentState -------------------------------------------------------
    public IAgentState State => new StateView(this);
    internal AgentState RawState => _state;
    internal bool RawRunning => _state == AgentState.Running;
    internal string? RawSession => _activeSession;
    internal bool RawIdle => _everRan && _state == AgentState.Idle;

    // ---- ISteeringQueue ----------------------------------------------------
    public int PendingCount => _steer.Count;
    public ValueTask EnqueueAsync(string text, CancellationToken ct = default)
    {
        _steer.Enqueue(text);
        return ValueTask.CompletedTask;
    }
    public ValueTask<string?> TryDequeueAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_steer.TryDequeue(out var t) ? t : (string?)null);

    // ---- run ---------------------------------------------------------------
    public async ValueTask<AgentRunResult> RunAsync(AgentRunOptions options, CancellationToken ct)
    {
        if (RawRunning)
            throw new InvalidOperationException("An agent run is already in progress.");

        _state = AgentState.Running;
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
            var maxTurns = options.MaxTurns ?? 32;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                turns++;

                // ---- steering check (between turns) -------------------------
                var steer = await TryDequeueAsync(ct);
                if (steer is not null)
                {
                    var userMsg = new AgentMessage(NewId(), MessageRole.User, [new TextPart(steer)], DateTimeOffset.UtcNow);
                    transcript.Add(userMsg);
                    await AppendAsync(store, userMsg, ct);
                    await PublishAsync(AgentEventType.TurnBoundary, options, null, ct);
                }

                // ---- build + run the model ----------------------------------
                await PublishAsync(AgentEventType.BeforeModelRequest, options, null, ct);

                var request = new ModelRequest
                {
                    ModelId = options.ModelId,
                    SessionId = options.SessionId,
                    Messages = transcript,
                    Tools = tools?.All().Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList() ?? [],
                    Temperature = options.Temperature,
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
                        await foreach (var ev in provider.RunAsync(request, ct))
                        {
                            await PublishAsync(AgentEventType.ModelStreamEvent, options, ModelEventWireMapper.ToWire(ev), ct);
                            if (ev is ModelCompleted mc) assistant = mc.Message;
                            else if (ev is ModelFailed mf)
                            {
                                modelError = mf.Error;
                                failedModelId = mf.ModelId;
                                failed = true;
                                break; // stop consuming the failed stream
                            }
                        }
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
                        throw new Exception(modelError ?? "model request failed");

                    await PublishAsync(AgentEventType.ModelRetrying, options,
                        new ModelEventWire { Kind = "model-retrying", ModelId = failedModelId ?? options.ModelId, Error = modelError,
                                             PromptTokens = decision.Attempt, CompletionTokens = decision.MaxAttempts, TotalTokens = decision.DelayMs }, ct);

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
                    return new AgentRunResult(true, assistant, turns, null);

                if (turns >= maxTurns)
                    return new AgentRunResult(true, assistant, turns, "max turns reached");

                await PublishAsync(AgentEventType.BeforeToolBatch, options, null, ct);
                var results = new List<MessagePart>();
                foreach (var call in calls)
                {
                    ct.ThrowIfCancellationRequested();
                    await PublishAsync(AgentEventType.BeforeToolCall, options,
                        new ModelEventWire { Kind = "tool-call-started", ToolCallId = call.Id, ToolName = call.Name }, ct);

                    var result = await ExecuteToolAsync(tools, call, ct);
                    results.Add(result);
                    await PublishAsync(AgentEventType.AfterToolCall, options,
                        new ModelEventWire { Kind = "tool-call-completed", ToolCallId = call.Id, ToolName = call.Name }, ct);
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
                    try
                    {
                        var comp = await compaction.CompactAsync(new CompactionRequest
                        {
                            SessionId = options.SessionId ?? _activeSession ?? string.Empty,
                            ModelId = options.ModelId ?? string.Empty,
                            ReasoningLevel = options.ReasoningLevel,
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
        }
    }

    private async Task<ToolResultPart> ExecuteToolAsync(IToolRegistry? tools, ToolCallPart call, CancellationToken ct)
    {
        var tool = tools?.Find(call.Name);
        if (tool is null)
            return new ToolResultPart(call.Id, call.Name, [new TextPart($"Unknown tool: {call.Name}")], IsError: true);
        try
        {
            var res = await tool.ExecuteAsync(call.Arguments, ct);
            return new ToolResultPart(call.Id, call.Name, res.Parts, res.IsError);
        }
        catch (Exception ex)
        {
            return new ToolResultPart(call.Id, call.Name, [new TextPart($"Tool error: {ex.Message}")], IsError: true);
        }
    }

    private T? Acquire<T>(string id) where T : notnull
    {
        try { return _services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default!; }
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




    private async ValueTask AppendAsync(ISessionStore? store, AgentMessage msg, CancellationToken ct)
    {
        if (store is null || string.IsNullOrEmpty(_activeSession)) return;
        try
        {
            await store.AppendAsync(new SessionEntry(NewId(), _activeSession, EntryKind.Message, msg, null, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex) { _ctx.Log.Warning($"Failed to persist entry: {ex.Message}"); }
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
}
