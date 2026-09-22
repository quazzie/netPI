using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Orchestration;

/// <summary>
/// astra-2: the NetPI.Orchestration orchestrator (service "orchestration").
/// Implements IAgentOrchestrator: team/agent lifecycle, spawn/continue/cancel,
/// mailbox/wake, and terminal reconciliation. Consumes the orchestration-store
/// and the runner through the service registry -- never their assemblies.
/// </summary>
public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IPluginContext _ctx;
    private readonly IOrchestrationStore _store;
    private readonly int _maxDelegationDepth;
    private readonly List<IDisposable> _subs = [];

    public AgentOrchestrator(IPluginContext ctx, IOrchestrationStore store, int maxDelegationDepth = 3)
    {
        _ctx = ctx;
        _store = store;
        _maxDelegationDepth = maxDelegationDepth;
    }

    private IAgentRunner? Runner()
    {
        try { return _ctx.Services.Resolve<IAgentRunner>("runner"); }
        catch { return null; }
    }

    private ILaneScheduler? Lanes()
    {
        try { return _ctx.Services.Resolve<ILaneScheduler>("lanes"); }
        catch { return null; }
    }

    /// <summary>
    /// Subscribe to the runner's terminal AgentEvents (published to the host bus by
    /// NetPI.Agent) and reconcile each into the assignment store (astra-2: run to
    /// assignment mapping via GetByRunIdAsync). Called once from StartAsync.
    /// </summary>
    public void SubscribeToRunnerEvents()
    {
        _subs.Add(_ctx.Events.Subscribe<AgentEvent>(e => _ = ReconcileTerminalAsync(e)));
    }

    private async Task ReconcileTerminalAsync(AgentEvent evt)
    {
        if (evt.RunId is null) return;
        if (evt.Type is not (AgentEventType.AgentCompleted or AgentEventType.AgentFailed or AgentEventType.AgentCancelled))
            return;
        var row = await _store.GetByRunIdAsync(evt.RunId);
        if (row is null) return;
        var lifecycle = evt.Type switch
        {
            AgentEventType.AgentCompleted => AgentAssignmentLifecycle.Completed,
            AgentEventType.AgentFailed    => AgentAssignmentLifecycle.Failed,
            _                             => AgentAssignmentLifecycle.Cancelled,
        };
        var ok = await _store.TransitionAsync(
            row.AssignmentId, row.Version, lifecycle, AgentState.Idle,
            row.PoolId, row.LaneId, row.DeploymentId,
            evt.Type == AgentEventType.AgentFailed ? "agent-failed" : null, null);
        if (!ok) return;
        var updated = await _store.GetAssignmentAsync(row.AssignmentId);
        _ctx.Events.PublishAsync(new AgentLifecycleEvent(
            AgentLifecycleEventKind.Updated, updated is null ? [row] : [updated], null, DateTimeOffset.UtcNow));
        _ctx.Events.PublishAsync(new LanesStateEvent(Pools(), DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
    }

    // ---- spawn / continue -------------------------------------------------

    public async ValueTask<AgentSpawnResult> SpawnChildAsync(
        string parentAgentId, AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        string? modelId = null;
        string? teamId = null;
        var parent = await _store.GetAgentAsync(parentAgentId, cancellationToken);
        if (parent is not null)
        {
            teamId = parent.TeamId;
            var parentNonterm = await _store.GetNonterminalAsync(parent.SessionId, cancellationToken);
            modelId = parentNonterm?.ModelId;
        }
        if (modelId is null) modelId = "default";

        var title = string.IsNullOrEmpty(request.Brief)
            ? "child" : (request.Brief.Length <= 32 ? request.Brief : request.Brief[..32] + "...");

        var outcome = await _store.SpawnChildAsync(
            request.OperationId, parentAgentId, teamId, modelId,
            request.PoolId, request.DeploymentId, request.Brief, title, cancellationToken);

        var runner = Runner();
        if (runner is not null)
        {
            var start = await runner.StartRunAsync(
                new AgentRunRequest(outcome.Agent.SessionId, null, modelId, request.Brief),
                cancellationToken);
            if (start.Disposition == RunDisposition.Admitted)
                await TryTransitionAsync(outcome.AssignmentId, AgentAssignmentLifecycle.Running, AgentState.CallingModel, cancellationToken);
        }

        return new AgentSpawnResult(outcome.Agent, outcome.AssignmentId, outcome.Agent.SessionId, outcome.Status, outcome.Reason);
    }

    public async ValueTask<AgentSpawnResult> ContinueAsync(
        string agentId, string text, string? operationId, CancellationToken cancellationToken)
    {
        var agent = await _store.GetAgentAsync(agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown agent {agentId}");
        var opId = operationId ?? Guid.NewGuid().ToString("N");
        var modelId = (await _store.GetNonterminalAsync(agent.SessionId, cancellationToken))?.ModelId ?? "default";

        var row = await _store.CreateAssignmentAsync(
            opId, agent.AgentId, agent.SessionId, agent.TeamId, agent.ParentAgentId,
            modelId, null, null, "follow-up", text, cancellationToken);

        var runner = Runner();
        if (runner is not null)
        {
            var start = await runner.StartRunAsync(
                new AgentRunRequest(agent.SessionId, null, modelId, text),
                cancellationToken);
            if (start.Disposition == RunDisposition.Admitted)
                await TryTransitionAsync(row.AssignmentId, AgentAssignmentLifecycle.Running, AgentState.CallingModel, cancellationToken);
        }

        return new AgentSpawnResult(agent, row.AssignmentId, agent.SessionId, row.Lifecycle, null);
    }

    // ---- messaging ---------------------------------------------------------

    public async ValueTask<int> SendMessageAsync(
        string fromAgentId, string toAgentId, string kind, string body,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
        => await _store.SendMessageAsync(
            Guid.NewGuid().ToString("N"), fromAgentId, toAgentId, null,
            kind, body, null, idempotencyKey, cancellationToken);

    public async ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(
        string agentId, int count, CancellationToken cancellationToken)
        => await _store.DrainMailboxAsync(agentId, count, cancellationToken);

    // ---- waits ---------------------------------------------------------------

    public async ValueTask RegisterWaitAsync(
        string agentId, AgentWaitCondition condition, CancellationToken cancellationToken)
    {
        var agent = await _store.GetAgentAsync(agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown agent {agentId}");
        var nonterm = await _store.GetNonterminalAsync(agent.SessionId, cancellationToken);

        // astra-2 §16: a wait dependency cycle is REJECTED with an actionable reason
        // (no stranded lane). An agent may wait on a descendant (the normal
        // delegation case), never on itself or an ancestor: an ancestor cannot
        // reach terminal while one of its descendants is still live.
        foreach (var targetId in condition.AssignmentIds)
        {
            var target = await _store.GetAssignmentAsync(targetId, cancellationToken);
            if (target is null) continue; // unknown targets are ignored (satisfied-by-none semantics)
            if (target.AgentId == agent.AgentId)
                throw new InvalidOperationException(
                    $"wait rejected: agent {agent.AgentId} cannot wait on its own assignment {targetId} (dependency cycle)");
            if (nonterm is not null)
            {
                var ancestorSubtree = await _store.ListSubtreeAsync(target.AgentId, cancellationToken);
                if (ancestorSubtree.Any(r => r.AssignmentId == nonterm.AssignmentId))
                    throw new InvalidOperationException(
                        $"wait rejected: agent {agent.AgentId} is a descendant of {target.AgentId} and cannot wait for it (dependency cycle)");
            }
        }

        var waitId = Guid.NewGuid().ToString("N");
        await _store.RegisterWaitAsync(waitId, agentId, nonterm?.AssignmentId, condition, cancellationToken);
        if (nonterm is not null)
            await TryTransitionAsync(nonterm.AssignmentId, AgentAssignmentLifecycle.Waiting, AgentState.Idle, cancellationToken);
    }

    // ---- queries ---------------------------------------------------------------

    public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken cancellationToken)
        => _store.GetAssignmentAsync(assignmentId, cancellationToken);

    public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken cancellationToken)
        => _store.GetNonterminalAsync(sessionId, cancellationToken);

    public async ValueTask<IReadOnlyList<AgentAssignmentRow>> ListAssignmentsAsync(CancellationToken cancellationToken)
    {
        var nonterm = await _store.ListNonterminalAsync(cancellationToken);
        var term = await _store.ListRecentTerminalAsync(20, cancellationToken);
        var all = new List<AgentAssignmentRow>(nonterm.Count + term.Count);
        all.AddRange(nonterm);
        all.AddRange(term);
        return all;
    }

    public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken cancellationToken)
        => _store.ListSubtreeAsync(agentId, cancellationToken);

    // ---- cancel -------------------------------------------------------------------

    public async ValueTask<AgentAssignmentLifecycle> CancelAsync(
        string assignmentId, bool subtree, CancellationToken cancellationToken)
    {
        var row = await _store.GetAssignmentAsync(assignmentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        if (!row.IsNonTerminal) return row.Lifecycle;

        var toCancel = new List<AgentAssignmentRow>();
        if (subtree && row.AgentId is not null)
            toCancel.AddRange(await _store.ListSubtreeAsync(row.AgentId, cancellationToken));
        toCancel.Add(row);

        foreach (var target in toCancel)
        {
            if (!target.IsNonTerminal) continue;
            await TryTransitionAsync(target.AssignmentId, AgentAssignmentLifecycle.Cancelled, AgentState.Idle, cancellationToken, reason: "cancelled");
            var runner = Runner();
            if (runner is not null)
            {
                var live = runner.ListRuns().FirstOrDefault(r => r.SessionId == target.SessionId && r.Outcome == RunState.Running);
                if (live is not null) runner.CancelRun(live.RunId);
            }
        }

        _ctx.Events.PublishAsync(new LanesStateEvent(Pools(), DateTimeOffset.UtcNow));
        return AgentAssignmentLifecycle.Cancelled;
    }

    // ---- helpers ----------------------------------------------------------------------

    public IReadOnlyList<AgentPoolSnapshot> Pools()
    {
        var lanes = Lanes();
        if (lanes is null) return [];
        return lanes.Snapshots()
            .Select(s => new AgentPoolSnapshot(
                s.PoolId, string.Empty, string.Empty,
                s.OwnedCount, s.QueueCount, s.EffectiveCapacity, s.Enabled, s.BlockedReason))
            .ToList();
    }

    private async ValueTask TryTransitionAsync(
        string assignmentId, AgentAssignmentLifecycle lifecycle, AgentState phase,
        CancellationToken ct, string? reason = null)
    {
        var row = await _store.GetAssignmentAsync(assignmentId, ct);
        if (row is null) return;
        var ok = await _store.TransitionAsync(
            assignmentId, row.Version, lifecycle, phase,
            row.PoolId, row.LaneId, row.DeploymentId, reason, null, ct);
        if (!ok)
        {
            var fresh = await _store.GetAssignmentAsync(assignmentId, ct);
            if (fresh is not null)
                await _store.TransitionAsync(
                    assignmentId, fresh.Version, lifecycle, phase,
                    fresh.PoolId, fresh.LaneId, fresh.DeploymentId, reason, null, ct);
        }
    }

    /// <summary>
    /// astra-2 section 14: on load, reconcile durable queued/running state. A segment
    /// that was Running before a restart but no longer has a live runner run is reset
    /// to Queued (it will be re-admitted on the next admission pass).
    /// </summary>
    public async ValueTask ReconcileOnLoadAsync(CancellationToken cancellationToken)
    {
        var nonterm = await _store.ListNonterminalAsync(cancellationToken);
        foreach (var row in nonterm)
        {
            if (row.Lifecycle != AgentAssignmentLifecycle.Running) continue;
            var runner = Runner();
            var isLive = runner is not null
                && runner.ListRuns().Any(r => r.SessionId == row.SessionId && r.Outcome == RunState.Running);
            if (!isLive)
            {
                var ok = await _store.TransitionAsync(
                    row.AssignmentId, row.Version,
                    AgentAssignmentLifecycle.Queued, AgentState.Idle,
                    row.PoolId, row.LaneId, row.DeploymentId, "restart-reconciled", null, cancellationToken);
                if (ok)
                    _ctx.Log.Information($"Reconciled assignment {row.AssignmentId} to Queued (no live runner after restart)");
            }
        }
    }
}
