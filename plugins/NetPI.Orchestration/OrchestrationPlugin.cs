using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Orchestration;

/// <summary>
/// astra-2: the NetPI.Orchestration plugin (service "orchestration").
/// Registers the IAgentOrchestrator implementation and the agents.* tools.
/// Consumes the orchestration-store (NetPI.Storage.Sqlite) and the runner
/// (NetPI.Agent) through the service registry — never their assemblies.
/// </summary>
public sealed class OrchestrationPlugin : INetPiPlugin
{
    private IPluginContext? _ctx;
    private AgentOrchestrator? _orchestrator;
    private IAgentTool[] _tools = [];
    private IDisposable[] _registrations = [];
    private IToolRegistry? _toolsRegistry;
    private IDisposable _toolsWatch;
    private readonly object _gate = new();
    private int _maxDelegationDepth;
    private int _maxOutstandingMessages;

    public PluginInfo Info { get; } = new("netPI.Orchestration", "Agent Orchestration", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        var cfg = context.OwnConfig;
        if (cfg.ValueKind == JsonValueKind.Object)
        {
            if (cfg.TryGetProperty("maxDelegationDepth", out var d) && d.ValueKind == JsonValueKind.Number)
                _maxDelegationDepth = d.GetInt32();
            if (cfg.TryGetProperty("maxOutstandingMessagesPerAgent", out var m) && m.ValueKind == JsonValueKind.Number)
                _maxOutstandingMessages = m.GetInt32();
        }
        var store = context.Services.Resolve<IOrchestrationStore>("orchestration-store");
        _orchestrator = new AgentOrchestrator(context, store, _maxDelegationDepth);
        context.Services.Register<IAgentOrchestrator>("orchestration", _orchestrator);
        await ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Not loaded.");
        await _orchestrator!.ReconcileOnLoadAsync(cancellationToken);
        _orchestrator.SubscribeToRunnerEvents();
        RegisterTools();
        _toolsWatch?.Dispose();
        _toolsWatch = ctx.Services.WatchServiceReplacement("tools", _ =>
        {
            lock (_gate)
            {
                try { RegisterTools(); ctx.Log.Information("Orchestration re-registered tools into reloaded tools registry"); }
                catch (Exception ex) { ctx.Log.Error($"Orchestration re-registration failed: {ex.Message}", ex); }
            }
        });
        ctx.Log.Information("Orchestration ready");
        await ValueTask.CompletedTask;
    }

    private void RegisterTools()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Not loaded.");
        IToolRegistry? registry = null;
        try { registry = ctx.Services.Resolve<IToolRegistry>("tools"); }
        catch { /* tools not loaded — skip registration */ }
        if (registry is null) return;

        var same = _toolsRegistry is not null && ReferenceEquals(_toolsRegistry, registry);
        if (same) foreach (var r in _registrations) r.Dispose();
        else _registrations = []; // stale handles belong to a reloaded registry

        var orch = _orchestrator!;
        _tools = [
            new AgentsSpawnTool(orch),
            new AgentsDelegateTool(orch),
            new AgentsMessageTool(orch),
            new AgentsWaitTool(orch),
            new AgentsInspectTool(orch),
            new AgentsContinueTool(orch),
            new AgentsCancelTool(orch),
            new LanesListTool(orch),
        ];
        _registrations = _tools.Select(t => registry.Register(t)).ToArray();
        _toolsRegistry = registry;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _toolsWatch?.Dispose();
        _toolsWatch = null;
        foreach (var r in _registrations) r.Dispose();
        _registrations = [];
        _orchestrator?.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _orchestrator = null;
        return ValueTask.CompletedTask;
    }
}

// ---- tools ----------------------------------------------------------------

internal static class Json
{
    public static JsonElement Obj(params (string k, object? v)[] props)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var (k, val) in props) obj[k] = val;
        return JsonSerializer.SerializeToElement(obj);
    }
}

internal abstract class AgentToolBase : IAgentTool
{
    protected readonly AgentOrchestrator Orch;
    protected AgentToolBase(AgentOrchestrator orch) => Orch = orch;
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement Parameters { get; }
    public abstract IReadOnlyList<string> Guidelines { get; }
    public abstract ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);
}

internal sealed class AgentsSpawnTool : AgentToolBase
{
    public AgentsSpawnTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.spawn";
    public override string Description => "Create a child agent with its own session and queued assignment. The child runs independently; you receive the agent id + assignment id. Use agents.inspect to monitor progress, agents.message to send follow-ups.";
    public override JsonElement Parameters => Json.Obj(
        ("brief", "The task brief (the child's first user message)"),
        ("parentAgentId", "The parent agent's id (from agents.inspect or runtime context)"),
        ("poolId", "Optional pool id to admit into (pooled deployment only)"),
        ("deploymentId", "Optional deployment id (trusted config, not model-chosen)"),
        ("operationId", "Stable id for idempotent retries (required)"));
    public override IReadOnlyList<string> Guidelines => [
        "Use a unique operationId per logical spawn so retries are idempotent.",
        "The child runs in its own session; you do NOT inherit its transcript."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var parentAgentId = S(args, "parentAgentId") ?? context.AgentId ?? "";
        var brief = S(args, "brief") ?? "";
        var opId = S(args, "operationId") ?? Guid.NewGuid().ToString("N");
        var poolId = S(args, "poolId");
        var depId = S(args, "deploymentId");
        if (string.IsNullOrEmpty(parentAgentId))
            return Err(context, "parentAgentId is required (or the runtime must provide AgentId)");
        if (string.IsNullOrEmpty(brief))
            return Err(context, "brief is required");

        try
        {
            var result = await Orch.SpawnChildAsync(parentAgentId, new AgentSpawnRequest
            {
                Brief = brief,
                OperationId = opId,
                PoolId = poolId,
                DeploymentId = depId,
            }, cancellationToken);
            return Ok(context, new
            {
                agentId = result.Agent.AgentId,
                sessionId = result.SessionId,
                assignmentId = result.AssignmentId,
                status = AgentAssignmentLifecycleNames.Name(result.Status),
                reason = result.Reason,
            });
        }
        catch (Exception ex)
        {
            return Err(context, $"Spawn failed: {ex.Message}");
        }
    }

    private static string? S(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ToolResult Ok(ToolContext ctx, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new ToolResult("", ctx is { } _ ? "agents.spawn" : "", [new TextPart(json)]);
    }
    private static ToolResult Err(ToolContext ctx, string msg)
        => new("", "agents.spawn", [new TextPart(msg)], IsError: true);
}

internal sealed class AgentsDelegateTool : AgentToolBase
{
    public AgentsDelegateTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.delegate";
    public override string Description => "Delegate a subtask: spawns a child agent, waits for it, and suspends your run until the child finishes. Your lane is released for the child; you are resumed with the child's bounded result (not its transcript). Use agents.spawn for fire-and-forget children.";
    public override JsonElement Parameters => Json.Obj(
        ("brief", "The subtask brief (the child's first user message)"),
        ("operationId", "Stable id for idempotent retries (required)"),
        ("poolId", "Optional pool id (pooled deployment only)"),
        ("deploymentId", "Optional deployment id (trusted config, not model-chosen)"),
        ("modelId", "Optional model the child runs on (a direct-cloud coordinator delegating local work names the local model here; default: inherit the parent's model)"));
    public override IReadOnlyList<string> Guidelines => [
        "Delegation SUSPENDS your run: no further model call happens in this segment after the tool returns.",
        "You are resumed when the child reaches a terminal outcome; the result is a bounded summary, not the child's transcript.",
        "Use agents.spawn (not delegate) when you want to keep working while the child runs."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var parentAgentId = S(args, "parentAgentId") ?? context.AgentId ?? "";
        var brief = S(args, "brief") ?? "";
        var opId = S(args, "operationId") ?? Guid.NewGuid().ToString("N");
        var poolId = S(args, "poolId");
        var depId = S(args, "deploymentId");
        var modelId = S(args, "modelId");
        if (string.IsNullOrEmpty(parentAgentId))
            return Err(context, "parentAgentId is required (or the runtime must provide AgentId)");
        if (string.IsNullOrEmpty(brief))
            return Err(context, "brief is required");

        try
        {
            var r = await Orch.DelegateAsync(parentAgentId, new AgentSpawnRequest
            {
                Brief = brief,
                OperationId = opId,
                PoolId = poolId,
                DeploymentId = depId,
                ModelId = modelId,
            }, cancellationToken);
            return Ok(context, new
            {
                childAgentId = r.ChildAgent.AgentId,
                childAssignmentId = r.ChildAssignmentId,
                childSessionId = r.ChildSessionId,
                parentAssignmentId = r.ParentAssignmentId,
                parentStatus = AgentAssignmentLifecycleNames.Name(r.ParentStatus),
                reason = r.Reason,
            });
        }
        catch (Exception ex)
        {
            return Err(context, $"Delegation failed: {ex.Message}");
        }
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.delegate", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.delegate", [new TextPart(m)], IsError: true);
}

internal sealed class AgentsMessageTool : AgentToolBase
{
    public AgentsMessageTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.message";
    public override string Description => "Send a durable message to an existing agent. The recipient sees it at its next safe boundary; no inference is triggered.";
    public override JsonElement Parameters => Json.Obj(
        ("toAgentId", "Target agent id"),
        ("kind", "Message kind (assignment|question|finding|answer|blocked|completion|control)"),
        ("body", "Bounded message body"),
        ("idempotencyKey", "Optional idempotency key for deduplication"));
    public override IReadOnlyList<string> Guidelines => [
        "Messages are durable and cursor-based: the recipient processes them at its next turn boundary.",
        "Use kind='question' or 'answer' for Q&A; 'finding' for results; 'blocked' when you need the recipient's attention."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var to = S(args, "toAgentId");
        var kind = S(args, "kind") ?? "note";
        var body = S(args, "body") ?? "";
        var key = S(args, "idempotencyKey");
        if (string.IsNullOrEmpty(to)) return Err(context, "toAgentId is required");
        if (string.IsNullOrEmpty(body)) return Err(context, "body is required");
        var from = context.AgentId ?? "";
        try
        {
            var seq = await Orch.SendMessageAsync(from, to!, kind, body, key, cancellationToken);
            return Ok(context, new { toAgentId = to, kind, seq });
        }
        catch (Exception ex) { return Err(context, $"Message failed: {ex.Message}"); }
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.message", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.message", [new TextPart(m)], IsError: true);
}

internal sealed class AgentsWaitTool : AgentToolBase
{
    public AgentsWaitTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.wait";
    public override string Description => "Register a wait condition: your agent is suspended (no lane held) until the referenced assignment(s) reach a terminal state OR a message arrives from one of the referenced agents.";
    public override JsonElement Parameters => Json.Obj(
        ("assignmentIds", "Assignment ids to await terminal outcomes for (JSON array)"),
        ("messageFromAgentIds", "Agent ids whose messages will satisfy this wait (JSON array)"),
        ("requireAll", "True = all conditions must be met; false (default) = any"),
        ("deadline", "Optional ISO 8601 deadline (missed deadlines are persisted wake events, not model calls)"));
    public override IReadOnlyList<string> Guidelines => [
        "Use agents.wait to yield your lane while children run — do NOT poll with agents.inspect.",
        "A wait condition is satisfied when ANY (or ALL if requireAll=true) of the referenced assignments go terminal.",
        "After the wait is satisfied, your agent resumes with the result in its mailbox."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var agentId = context.AgentId ?? "";
        if (string.IsNullOrEmpty(agentId)) return Err(context, "AgentId not available in runtime context");
        var assignmentIds = ParseStringArray(args, "assignmentIds");
        var messageFrom = ParseStringArray(args, "messageFromAgentIds");
        var requireAll = args.TryGetProperty("requireAll", out var ra) && ra.ValueKind == JsonValueKind.True;
        var deadlineS = S(args, "deadline");
        DateTimeOffset? deadline = null;
        if (deadlineS is not null && DateTimeOffset.TryParse(deadlineS, out var dt)) deadline = dt;

        try
        {
            await Orch.RegisterWaitAsync(agentId, new AgentWaitCondition
            {
                AssignmentIds = assignmentIds,
                MessageFromAgentIds = messageFrom,
                RequireAll = requireAll,
                Deadline = deadline,
            }, cancellationToken);
            return Ok(context, new { agentId, assignmentIds, messageFrom, requireAll });
        }
        catch (Exception ex) { return Err(context, $"Wait registration failed: {ex.Message}"); }
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static List<string> ParseStringArray(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.wait", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.wait", [new TextPart(m)], IsError: true);
}

internal sealed class AgentsInspectTool : AgentToolBase
{
    public AgentsInspectTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.inspect";
    public override string Description => "Look up the current state of an agent/assignment. Returns lifecycle, phase, session, and result (if terminal).";
    public override JsonElement Parameters => Json.Obj(
        ("agentId", "Agent id to look up"),
        ("assignmentId", "Assignment id to look up (use either agentId or assignmentId)"));
    public override IReadOnlyList<string> Guidelines => [
        "Use agents.inspect to check child status without consuming a lane.",
        "A nonterminal assignment is still running/queued/waiting; a terminal one has a final result."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var agentId = S(args, "agentId");
        var assignmentId = S(args, "assignmentId");
        AgentAssignmentRow? row = null;
        if (assignmentId is not null)
            row = await Orch.GetAssignmentAsync(assignmentId, cancellationToken);
        else if (agentId is not null)
        {
            var agent = await Orch.GetNonterminalAsync("", cancellationToken); // not used; we need by agent
            // Fallback: look up by agent's nonterminal assignment via the store
            // (the orchestrator doesn't expose GetAgentAsync publicly; use ListAssignmentsAsync)
            var all = await Orch.ListAssignmentsAsync(cancellationToken);
            row = all.FirstOrDefault(r => r.AgentId == agentId && r.IsNonTerminal)
                ?? all.FirstOrDefault(r => r.AgentId == agentId);
        }
        if (row is null) return Err(context, "Assignment not found");
        return Ok(context, new
        {
            assignmentId = row.AssignmentId,
            agentId = row.AgentId,
            sessionId = row.SessionId,
            lifecycle = AgentAssignmentLifecycleNames.Name(row.Lifecycle),
            phase = row.Phase.ToString(),
            poolId = row.PoolId,
            modelId = row.ModelId,
            title = row.Title,
            reason = row.Reason,
            nonTerminal = row.IsNonTerminal,
            createdAt = row.CreatedAt,
            startedAt = row.StartedAt,
            endedAt = row.EndedAt,
        });
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.inspect", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.inspect", [new TextPart(m)], IsError: true);
}

internal sealed class AgentsContinueTool : AgentToolBase
{
    public AgentsContinueTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.continue";
    public override string Description => "Send an explicit follow-up to a TERMINAL agent. Creates a new assignment in its existing session; the agent is re-admitted through normal policy.";
    public override JsonElement Parameters => Json.Obj(
        ("agentId", "The terminal agent's id"),
        ("text", "The follow-up text (new user message)"),
        ("operationId", "Stable id for idempotent retries (optional — generated if absent)"));
    public override IReadOnlyList<string> Guidelines => [
        "Use agents.continue to ask a finished child for more work — it creates a NEW assignment, not a lane entitlement.",
        "The child's previous session/transcript is preserved; the follow-up is appended as a new user message."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var agentId = S(args, "agentId") ?? context.AgentId ?? "";
        var text = S(args, "text") ?? "";
        var opId = S(args, "operationId");
        if (string.IsNullOrEmpty(agentId)) return Err(context, "agentId is required");
        if (string.IsNullOrEmpty(text)) return Err(context, "text is required");
        try
        {
            var result = await Orch.ContinueAsync(agentId, text, opId, cancellationToken);
            return Ok(context, new
            {
                agentId = result.Agent.AgentId,
                sessionId = result.SessionId,
                assignmentId = result.AssignmentId,
                status = AgentAssignmentLifecycleNames.Name(result.Status),
            });
        }
        catch (Exception ex) { return Err(context, $"Continue failed: {ex.Message}"); }
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.continue", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.continue", [new TextPart(m)], IsError: true);
}

internal sealed class AgentsCancelTool : AgentToolBase
{
    public AgentsCancelTool(AgentOrchestrator o) : base(o) { }
    public override string Name => "agents.cancel";
    public override string Description => "Cancel an assignment and optionally its descendant subtree. Idempotent — cancelling an already-terminal assignment returns the recorded outcome.";
    public override JsonElement Parameters => Json.Obj(
        ("assignmentId", "Assignment id to cancel"),
        ("subtree", "True to also cancel all descendants (default: false)"));
    public override IReadOnlyList<string> Guidelines => [
        "Cancelling a parent with subtree=true cancels all its children (queued, running, waiting).",
        "Cancelling without subtree only cancels the target; its siblings are unaffected."];
    public override async ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var assignmentId = S(args, "assignmentId");
        if (string.IsNullOrEmpty(assignmentId)) return Err(context, "assignmentId is required");
        var subtree = args.TryGetProperty("subtree", out var st) && st.ValueKind == JsonValueKind.True;
        try
        {
            var outcome = await Orch.CancelAsync(assignmentId!, subtree, cancellationToken);
            return Ok(context, new { assignmentId, subtree, outcome = AgentAssignmentLifecycleNames.Name(outcome) });
        }
        catch (Exception ex) { return Err(context, $"Cancel failed: {ex.Message}"); }
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static ToolResult Ok(ToolContext ctx, object p) => new("", "agents.cancel", [new TextPart(JsonSerializer.Serialize(p))]);
    private static ToolResult Err(ToolContext ctx, string m) => new("", "agents.cancel", [new TextPart(m)], IsError: true);
}

internal sealed class LanesListTool : AgentToolBase
{
    private readonly AgentOrchestrator _orch;
    public LanesListTool(AgentOrchestrator o) : base(o) => _orch = o;
    public override string Name => "lanes.list";
    public override string Description => "Read-only snapshot of all pools: owners, capacity, queue length, enabled state. Never a reservation.";
    public override JsonElement Parameters => Json.Obj(("poolId", "Optional pool filter"));
    public override IReadOnlyList<string> Guidelines => [
        "Use lanes.list to check capacity before spawning — it never reserves a lane.",
        "A queueCount > 0 means work is accepted but waiting for a free lane."];
    public override ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var args = context.Arguments;
        var poolFilter = S(args, "poolId");
        var pools = Orch.Pools();
        if (!string.IsNullOrEmpty(poolFilter))
            pools = pools.Where(p => p.PoolId == poolFilter).ToList();
        var json = JsonSerializer.Serialize(new { pools });
        return new ValueTask<ToolResult>(new ToolResult("", "lanes.list", [new TextPart(json)]));
    }
    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
