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

    public AgentRunner(AgentRuntime runtime, IPluginContext ctx)
    {
        _runtime = runtime;
        _ctx = ctx;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _cts is not null; }
    }

    public async ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cts is not null)
                return new AgentRunStart(null, "A run is already in progress.");
            _cts = new CancellationTokenSource();
        }

        // Persist the initial user entry (the agent runtime does not persist
        // the initial input message itself).
        try
        {
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                var store = _ctx.Services.Resolve<ISessionStore>("sessions");
                var user = new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.User,
                    [new TextPart(request.Text)], DateTimeOffset.UtcNow);
                await store.AppendAsync(new SessionEntry(Guid.NewGuid().ToString("n"),
                    request.SessionId, EntryKind.Message, user, null, DateTimeOffset.UtcNow), cancellationToken);
            }
        }
        catch (Exception ex) { _ctx.Log.Warning($"user entry persist failed: {ex.Message}"); }

        var cts = _cts!;
        _ = Task.Run(() => ExecuteAsync(request, cts));

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

    private async Task ExecuteAsync(AgentRunRequest request, CancellationTokenSource cts)
    {
        string? sessionId = request.SessionId;
        bool cancelled = false;
        try
        {
            var workspace = string.IsNullOrEmpty(request.WorkspacePath)
                ? Environment.CurrentDirectory : request.WorkspacePath;
            var systemText = await BuildSystemPromptAsync(workspace);

            var transcript = new List<AgentMessage>();
            if (!string.IsNullOrWhiteSpace(systemText))
                transcript.Add(new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.System,
                    [new TextPart(systemText)], DateTimeOffset.UtcNow));
            transcript.Add(new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.User,
                [new TextPart(request.Text)], DateTimeOffset.UtcNow));

            await _runtime.RunAsync(new AgentRunOptions
            {
                SessionId = sessionId,
                ModelId = request.ModelId,
                Messages = transcript,
                ReasoningLevel = request.ReasoningLevel,
                Temperature = request.Temperature,
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("Agent run failed", ex);
        }
        finally
        {
            // Emit a terminal agent event so the Web plugin can flip the UI to Idle.
            try
            {
                var type = cancelled ? AgentEventType.AgentCancelled : AgentEventType.AgentCompleted;
                var evt = new AgentEvent(Guid.NewGuid().ToString("n"), type, DateTimeOffset.UtcNow, sessionId, null);
                await _ctx.Events.PublishAsync(evt, CancellationToken.None);
            }
            catch { /* bus may already be gone */ }

            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts?.Dispose();
                    _cts = null;
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
            var toolDefs = tools?.All().Select(t => new ToolDefinition(t.Name, t.Description, t.Parameters)).ToList() ?? [];
            inputs = inputs with { Tools = toolDefs };
            return await provider.BuildAsync(inputs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warning($"System prompt build failed: {ex.Message}");
            return string.Empty;
        }
    }

    private T? Resolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }
    }
}
