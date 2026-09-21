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
    private CancellationTokenSource? _cts;
    /// <summary>astra-1 A (run cleanup): the owned background run — observed (not
    /// fire-and-forgotten) so plugin stop can cancel and await it.</summary>
    private Task? _runTask;

    public AgentRunner(AgentRuntime runtime, IPluginContext ctx)
    {
        _runtime = runtime;
        _ctx = ctx;
    }

    /// <summary>astra-1 A (run cleanup): the owned background run task (null when none).</summary>
    public Task? RunTask
    {
        get { lock (_gate) return _runTask; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _cts is not null; }
    }

    public async ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        // astra-1 A (run cleanup): no text → the send fails (never a silent
        // empty run), and the gate is never taken.
        if (string.IsNullOrWhiteSpace(request.Text))
            return new AgentRunStart(request.SessionId, "Empty message — nothing to send.");

        lock (_gate)
        {
            if (_cts is not null)
                return new AgentRunStart(null, "A run is already in progress.");
            _cts = new CancellationTokenSource();
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
                lock (_gate) _cts = null; // no run may outlive a failed send
                return new AgentRunStart(request.SessionId, $"Failed to persist the message: {ex.Message}");
            }
        }
        var cts = _cts!;
        _runTask = Task.Run(() => ExecuteAsync(request, cts));

        return new AgentRunStart(request.SessionId, null);

    }

    public ValueTask CancelRunAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
        }
        cts?.Cancel();
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

    private async Task ExecuteAsync(AgentRunRequest request, CancellationTokenSource cts)
    {
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
                if (ReferenceEquals(_cts, cts))
                {
                    _cts?.Dispose();
                    _cts = null;
                    _runTask = null;
                }
            }

            // astra-1 A: exactly one terminal outcome per accepted run —
            // completed, cancelled or FAILED (a crashed loop must not be
            // reported as a completion).
            try
            {
                var type = cancelled ? AgentEventType.AgentCancelled
                    : failed ? AgentEventType.AgentFailed
                    : AgentEventType.AgentCompleted;
                var evt = new AgentEvent(Guid.NewGuid().ToString("n"), type, DateTimeOffset.UtcNow, sessionId, null);
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
