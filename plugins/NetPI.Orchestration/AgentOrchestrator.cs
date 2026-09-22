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
    private readonly IOrchestrationStore? _store;
    private readonly int _maxDelegationDepth;
    private readonly List<IDisposable> _subs = [];
    private readonly int _maxOutstandingMessages;

    public AgentOrchestrator(IPluginContext ctx, IOrchestrationStore? store = null, int maxDelegationDepth = 3, int maxOutstandingMessages = 0)
    {
        _ctx = ctx;
        _store = store; // fixed store (tests); null => lazy resolution
        _maxDelegationDepth = maxDelegationDepth;
        _maxOutstandingMessages = maxOutstandingMessages;
    }

    /// <summary>
    /// Store resolution. The host loads plugins ALPHABETICALLY and runs every
    /// LoadAsync before any StartAsync — "NetPI.Orchestration" therefore loads
    /// before "NetPI.Storage.Sqlite" registers the "orchestration-store"
    /// service, so an eager resolve at load time always fails on a fresh host.
    /// Resolve on demand instead: by StartAsync all loads have completed, so the
    /// service is present (it throws ServiceUnavailableException — failing
    /// closed — only when storage genuinely is not loaded).
    /// </summary>
    private IOrchestrationStore Store()
    {
        if (_store is not null) return _store;
        return _ctx.Services.Resolve<IOrchestrationStore>("orchestration-store");
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

    /// <summary>Resolve the sessions service (id "sessions") when present.</summary>
    private ISessionStore? Sessions()
    {
        try { return _ctx.Services.Resolve<ISessionStore>("sessions"); }
        catch { return null; }
    }

    /// <summary>
    /// astra-2 §8: the workspace an agent's runs execute in. The assignment row's
    /// <c>workspace_path</c> wins (a provisioned worktree); otherwise the session's
    /// recorded workspace (null = host CWD, as before §8).
    /// </summary>
    private async ValueTask<string?> ResolveAgentWorkspaceAsync(AgentAssignmentRow row, string? sessionId, CancellationToken ct)
    {
        if (row.WorkspacePath is { } p && !string.IsNullOrEmpty(p)) return p;
        if (sessionId is not null)
        {
            var sessions = Sessions();
            if (sessions is not null)
            {
                try { return (await sessions.GetAsync(sessionId, ct))?.WorkspacePath; }
                catch { return null; }
            }
        }
        return null;
    }

    /// <summary>
    /// astra-2 §8 (isolated-worktree): provision an independent git worktree for a
    /// child — an independent checkout of the parent workspace's BASE revision on
    /// a fresh <c>codex/…</c> branch (worktree add -b: the parent's uncommitted
    /// changes are not copied in, and the parent's dirty worktree is never
    /// touched — §15.C's "preserve dirty worktree artifacts"). The worktree lives
    /// under <c>&lt;parent-workspace&gt;/.netpi/worktrees/</c>: a sibling artifact the
    /// user reviews after the run, kept until reviewed (no automatic push, merge
    /// or cleanup as a side effect of delegation). Returns the worktree path, or
    /// null when isolation cannot be provisioned (non-Git workspace, git
    /// unavailable, or a collision — the caller fails the spawn with a reason).
    /// </summary>
    private string? PrepareWorktree(string parentWorkspace, string operationId)
    {
        var dotGit = Path.Combine(parentWorkspace, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return null; // non-Git workspace: §8 requires an explicit shared mode (or a bounded snapshot)
        var branch = "codex/" + operationId[..Math.Min(12, operationId.Length)];
        var path = Path.Combine(parentWorkspace, ".netpi", "worktrees", branch.Replace('/', '-'));
        if (Directory.Exists(path) || File.Exists(path)) return null; // collision: never clobber a review artifact
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"worktree add -b \"{branch}\" \"{path}\"")
            { WorkingDirectory = parentWorkspace, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git spawn failed");
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(); } catch { } return null; }
            return proc.ExitCode == 0 ? path : null;
        }
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
        var row = await Store().GetByRunIdAsync(evt.RunId);
        if (row is null) return;
        var lifecycle = evt.Type switch
        {
            AgentEventType.AgentCompleted => AgentAssignmentLifecycle.Completed,
            AgentEventType.AgentFailed    => AgentAssignmentLifecycle.Failed,
            _                             => AgentAssignmentLifecycle.Cancelled,
        };
        var ok = await Store().TransitionAsync(
            row.AssignmentId, row.Version, lifecycle, AgentState.Idle,
            row.PoolId, row.LaneId, row.DeploymentId,
            evt.Type == AgentEventType.AgentFailed ? "agent-failed" : null, null);
        if (!ok) return;
        var updated = await Store().GetAssignmentAsync(row.AssignmentId);
        _ctx.Events.PublishAsync(new AgentLifecycleEvent(
            AgentLifecycleEventKind.Updated, updated is null ? [row] : [updated], null, DateTimeOffset.UtcNow));
        _ctx.Events.PublishAsync(new LanesStateEvent(Pools(), DateTimeOffset.UtcNow));

        // astra-2 §6.3 step 5: a satisfied wait's owner must be resumed — consume its
        // durable waits and re-enter the parent for admission (waking is never
        // permission to execute without a lane; resume re-acquires).
        var parentId = row.ParentAgentId;
        if (parentId is not null && parentId != row.AgentId)
        {
            try { await ConsumeSatisfiedWaitsAsync(parentId, CancellationToken.None); }
            catch (Exception ex) { _ctx.Log.Warning($"Wake consumer failed for {parentId}: {ex.Message}"); }
        }
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
        var parent = await Store().GetAgentAsync(parentAgentId, cancellationToken);
        if (parent is not null)
        {
            teamId = parent.TeamId;
            var parentNonterm = await Store().GetNonterminalAsync(parent.SessionId, cancellationToken);
            modelId = parentNonterm?.ModelId;
        }
        if (modelId is null) modelId = "default";
        // astra-2 §11: an explicit child model (e.g. a direct-cloud coordinator
        // delegating LOCAL pooled work) wins over the inherited parent model.
        if (request.ModelId is { } m) modelId = m;

        // astra-2 §8: workspace modes. The request's mode is the policy
        // (default shared-read). The child ALWAYS runs in the parent's workspace
        // — "shared" means the child's tools resolve relative to the same
        // workspace as the parent, not the host's CWD; for isolated-worktree we
        // provision an independent git worktree of the parent workspace NOW
        // (branch codex/…, base = the recorded parent HEAD) and the child's tools
        // are pointed at THAT instead. Shared modes record the parent's workspace
        // path on the child session so the run executes there.
        var workspaceMode = (request.WorkspaceMode ?? "shared-read").Trim().ToLowerInvariant();
        var parentWorkspace = parent is not null
            ? (Sessions() is { } ss ? (await ss.GetAsync(parent.SessionId, cancellationToken))?.WorkspacePath : null)
            : null;
        string? childWorkspace = workspaceMode == "isolated-worktree"
            ? (parentWorkspace is null
                ? null
                : PrepareWorktree(parentWorkspace, request.OperationId))
            : parentWorkspace;
        if (workspaceMode == "isolated-worktree" && childWorkspace is null)
        {
            return new AgentSpawnResult(
                new AgentIdentity(string.Empty, teamId, null, string.Empty, "child", IsChild: true, DateTimeOffset.UtcNow),
                string.Empty, string.Empty, AgentAssignmentLifecycle.Failed,
                parentWorkspace is null
                    ? "isolated-worktree requires a parent workspace (a Git repo) — the parent session has none"
                    : "isolated-worktree could not be provisioned (non-Git workspace, git unavailable, or a colliding worktree path) — non-Git workspaces need an explicit shared mode or a bounded snapshot");
        }

        var title = string.IsNullOrEmpty(request.Brief)
            ? "child" : (request.Brief.Length <= 32 ? request.Brief : request.Brief[..32] + "...");

        var outcome = await Store().SpawnChildAsync(
            request.OperationId, parentAgentId, teamId, modelId,
            request.PoolId, request.DeploymentId, request.Brief, title,
            workspaceMode, childWorkspace, cancellationToken);

        var runner = Runner();
        if (runner is not null)
        {
            // astra-2 §7: the store's run_id = operationId; the runner's terminal
            // AgentEvent carries the same id (request.RunId), so reconciliation
            // maps the event back to this assignment via GetByRunIdAsync.
            // astra-2 §8: the child's tools are scoped to ITS workspace — for an
            // isolated worktree that is the worktree, never the parent's working tree.
            var start = await runner.StartRunAsync(
                new AgentRunRequest(outcome.Agent.SessionId, childWorkspace, modelId, request.Brief,
                    RunId: request.OperationId),
                cancellationToken);
            if (start.Disposition == RunDisposition.Admitted)
                await TryTransitionAsync(outcome.AssignmentId, AgentAssignmentLifecycle.Running, AgentState.CallingModel, cancellationToken);
        }

        return new AgentSpawnResult(outcome.Agent, outcome.AssignmentId, outcome.Agent.SessionId, outcome.Status, outcome.Reason, childWorkspace);
    }

    public async ValueTask<AgentSpawnResult> ContinueAsync(
        string agentId, string text, string? operationId, CancellationToken cancellationToken)
    {
        var agent = await Store().GetAgentAsync(agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown agent {agentId}");
        var opId = operationId ?? Guid.NewGuid().ToString("N");
        var modelId = (await Store().GetNonterminalAsync(agent.SessionId, cancellationToken))?.ModelId ?? "default";

        // astra-2 §8: a follow-up keeps the agent's recorded workspace ownership —
        // its last assignment's workspace mode + the workspace that run executed in.
        var prior = await Store().GetNonterminalAsync(agent.SessionId, cancellationToken);
        var row = await Store().CreateAssignmentAsync(
            opId, agent.AgentId, agent.SessionId, agent.TeamId, agent.ParentAgentId,
            modelId, null, null, "follow-up", text,
            prior?.WorkspaceMode, prior?.WorkspacePath, cancellationToken);

        var runner = Runner();
        if (runner is not null)
        {
            // astra-2 §7: same id-mapping as spawn — the store row's run_id is opId
            // and the runner's terminal event carries it (request.RunId).
            var start = await runner.StartRunAsync(
                new AgentRunRequest(agent.SessionId, prior?.WorkspacePath, modelId, text, RunId: opId),
                cancellationToken);
            if (start.Disposition == RunDisposition.Admitted)
                await TryTransitionAsync(row.AssignmentId, AgentAssignmentLifecycle.Running, AgentState.CallingModel, cancellationToken);
        }

        return new AgentSpawnResult(agent, row.AssignmentId, agent.SessionId, row.Lifecycle, null, prior?.WorkspacePath);
    }

    // ---- messaging ---------------------------------------------------------

    public async ValueTask<int> SendMessageAsync(
        string fromAgentId, string toAgentId, string kind, string body,
        string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        // astra-2 §9: a per-agent outstanding-message bound keeps any one recipient's
        // mailbox from growing unboundedly (memory + turn-boundary drain cost). When
        // the recipient already has `bound` UNCONSUMED messages, a further send would
        // make it `bound+1` — reject it as a bounded result (an actionable reason),
        // never letting an unbounded queue form. A bound of 0 = unbounded (no check).
        if (_maxOutstandingMessages > 0)
        {
            var outstanding = await Store().CountOutstandingAsync(toAgentId, cancellationToken);
            if (outstanding >= _maxOutstandingMessages)
                throw new MessageBoundExceededException(toAgentId, outstanding, _maxOutstandingMessages);
        }

        return await Store().SendMessageAsync(
            Guid.NewGuid().ToString("N"), fromAgentId, toAgentId, null,
            kind, body, null, idempotencyKey, cancellationToken);
    }
    public async ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(
        string agentId, int count, CancellationToken cancellationToken)
        => await Store().DrainMailboxAsync(agentId, count, cancellationToken);

    // ---- task board (astra-2 §9) ---------------------------------------------


    public ValueTask<AgentTaskRecord> CreateTaskAsync(string taskId, string teamId, string title,
        IReadOnlyList<string> dependsOnTaskIds, CancellationToken cancellationToken = default)
        => Store().CreateTaskAsync(taskId, teamId, title, dependsOnTaskIds, cancellationToken);

    public ValueTask<IReadOnlyList<AgentTaskRecord>> ListTasksAsync(string teamId, string? status,
        CancellationToken cancellationToken = default)
        => Store().ListTasksAsync(teamId, status, cancellationToken);

    public ValueTask<bool> ClaimTaskAsync(string taskId, string agentId, CancellationToken cancellationToken = default)
        => Store().ClaimTaskAsync(taskId, agentId, cancellationToken);

    public ValueTask UpdateTaskStatusAsync(string taskId, string status, string? ownerAgentId,
        CancellationToken cancellationToken = default)
        => Store().UpdateTaskStatusAsync(taskId, status, ownerAgentId, cancellationToken);

    public ValueTask<bool> DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
        => Store().DeleteTaskAsync(taskId, cancellationToken);


    // ---- waits ---------------------------------------------------------------

    public async ValueTask RegisterWaitAsync(
        string agentId, AgentWaitCondition condition, CancellationToken cancellationToken)
    {
        var agent = await Store().GetAgentAsync(agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown agent {agentId}");
        var nonterm = await Store().GetNonterminalAsync(agent.SessionId, cancellationToken);

        // astra-2 §16: a wait dependency cycle is REJECTED with an actionable reason
        // (no stranded lane). An agent may wait on a descendant (the normal
        // delegation case), never on itself or an ancestor: an ancestor cannot
        // reach terminal while one of its descendants is still live.
        foreach (var targetId in condition.AssignmentIds)
        {
            var target = await Store().GetAssignmentAsync(targetId, cancellationToken);
            if (target is null) continue; // unknown targets are ignored (satisfied-by-none semantics)
            if (target.AgentId == agent.AgentId)
                throw new InvalidOperationException(
                    $"wait rejected: agent {agent.AgentId} cannot wait on its own assignment {targetId} (dependency cycle)");
            if (nonterm is not null)
            {
                var ancestorSubtree = await Store().ListSubtreeAsync(target.AgentId, cancellationToken);
                if (ancestorSubtree.Any(r => r.AssignmentId == nonterm.AssignmentId))
                    throw new InvalidOperationException(
                        $"wait rejected: agent {agent.AgentId} is a descendant of {target.AgentId} and cannot wait for it (dependency cycle)");
            }
        }

        var waitId = Guid.NewGuid().ToString("N");
        await Store().RegisterWaitAsync(waitId, agentId, nonterm?.AssignmentId, condition, cancellationToken);
        if (nonterm is not null)
            await TryTransitionAsync(nonterm.AssignmentId, AgentAssignmentLifecycle.Waiting, AgentState.Idle, cancellationToken);
    }

    // ---- delegation (astra-2 §6.2/§6.3) --------------------------------------

    /// <summary>
    /// astra-2 §6.3 sequence: (1) spawn the child, (2) persist the parent's durable
    /// wait on the child's assignment, (3) quiesce the parent's live segment — no
    /// further model call in this segment — releasing its lane so the child is
    /// admitted for it. The receipt returns promptly; the child's result arrives
    /// later as a mailbox event on resume.
    /// </summary>
    public async ValueTask<AgentDelegateResult> DelegateAsync(
        string parentAgentId, AgentSpawnRequest request, CancellationToken cancellationToken)
    {
        var parent = await Store().GetAgentAsync(parentAgentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown agent {parentAgentId}");

        // astra-2 §6.2: delegation is depth-bounded — a handoff stack cannot
        // rotate parent/child past the configured depth (depth 0 = the root).
        var depth = 0;
        var cursor = parent;
        while (cursor.ParentAgentId is { } p2)
        {
            depth++;
            if (depth + 1 > _maxDelegationDepth)
                throw new InvalidOperationException(
                    $"Delegation rejected: the child would be at depth {depth + 1}, past the configured maximum {_maxDelegationDepth} (astra-2 6.2).");
            cursor = await Store().GetAgentAsync(p2, cancellationToken)
                ?? throw new InvalidOperationException($"Ancestor agent missing for {p2}");
        }

        // Idempotent by operation id: the CHILD's assignment carries run_id =
        // request.OperationId (the store is the idempotency source of truth). A
        // retried delegation returns the original child and does NOT re-register
        // a wait or re-suspend — the original delegation already persisted the
        // durable wake.
        var existingChild = await Store().GetByRunIdAsync(request.OperationId, cancellationToken);
        if (existingChild is not null)
        {
            var existingAgent = await Store().GetAgentAsync(existingChild.AgentId, cancellationToken);
            var parentNonterm = await Store().GetNonterminalAsync(parent.SessionId, cancellationToken);
            return new AgentDelegateResult(
                existingAgent ?? throw new InvalidOperationException($"Child agent missing for {existingChild.AgentId}"),
                existingChild.AssignmentId, existingChild.SessionId,
                parentNonterm?.AssignmentId ?? string.Empty,
                AgentAssignmentLifecycle.Waiting, "idempotent replay");
        }

        var nonterm = await Store().GetNonterminalAsync(parent.SessionId, cancellationToken);

        // (1) spawn — the child gets its own session/assignment, queued or admitted.
        var spawn = await SpawnChildAsync(parentAgentId, request, cancellationToken);

        // (2) durable wait: the parent waits for the child's assignment to go terminal.
        var waitId = Guid.NewGuid().ToString("N");
        await Store().RegisterWaitAsync(
            waitId, parentAgentId, nonterm?.AssignmentId,
            new AgentWaitCondition { AssignmentIds = [spawn.AssignmentId] }, cancellationToken);

        // (3) quiesce the parent's live segment: Suspended, lane released (the child
        // is admitted for it through normal admission), no further model call.
        var runner = Runner();
        var parentStatus = AgentAssignmentLifecycle.Waiting;
        if (runner is not null && nonterm is not null)
        {
            var parentRun = runner.ListRuns()
                .FirstOrDefault(r => r.SessionId == parent.SessionId && r.Outcome == RunState.Running);
            if (parentRun is not null)
            {
                // best-effort: if the parent had already quiesced on its own, the wait
                // above still resumes it (the durable wake is the source of truth).
                _ = await runner.SuspendRunAsync(parentRun.RunId, cancellationToken);
            }
            await TryTransitionAsync(
                nonterm.AssignmentId, AgentAssignmentLifecycle.Waiting, AgentState.Idle,
                cancellationToken, reason: "delegated");
        }

        return new AgentDelegateResult(
            spawn.Agent, spawn.AssignmentId, spawn.SessionId,
            nonterm?.AssignmentId ?? string.Empty, parentStatus, spawn.Reason);
    }

    /// <summary>
    /// astra-2 §6.3 step 5: consume satisfied waits and resume the parent.
    /// Each resume = RequeueRunAsync with the SAME run id (fresh segment; the
    /// runner publishes RunResumed) carrying the bounded child result. The wait
    /// is consumed only AFTER the resume is accepted — a crash in between
    /// retries once, never zero or twice. Waking is never permission to
    /// execute without a lane: re-queue re-enters admission.
    /// </summary>
    public async ValueTask<int> ConsumeSatisfiedWaitsAsync(string agentId, CancellationToken cancellationToken)
    {
        var waits = await Store().ListSatisfiedWaitsAsync(agentId, cancellationToken);
        var agent = await Store().GetAgentAsync(agentId, cancellationToken);
        if (agent is null || waits.Count == 0) return 0;
        var runner = Runner();
        if (runner is null) return 0;

        var resumed = 0;
        foreach (var w in waits)
        {
            if (w.RunId is null) { await Store().MarkWaitConsumedAsync(w.WaitId, cancellationToken); continue; }

            // Bounded result: the child's outcome + summary — never its transcript.
            var resultParts = new System.Text.StringBuilder();
            foreach (var t in w.Targets)
            {
                if (t.AssignmentId is null) continue;
                var row = await Store().GetAssignmentAsync(t.AssignmentId, cancellationToken);
                if (row is null) continue;
                resultParts.AppendLine($"child {row.AgentId} (assignment {row.AssignmentId}): {AgentAssignmentLifecycleNames.Name(row.Lifecycle)} — {row.Title}");
            }
            var summary = resultParts.Length > 0
                ? resultParts.ToString().TrimEnd()
                : "wait satisfied";

            var brief = "delegation result: " + summary;
            // astra-2 §6.3: waking is never permission to execute without a lane —
            // the resume must carry the parent's model so re-queue re-enters the
            // SAME admission policy (pooled → re-acquire the lane) as the original.
            var nontermBefore = await Store().GetNonterminalAsync(agent.SessionId, cancellationToken);
            // astra-2 §8: resume in the SAME workspace the parent runs in — the
            // nonterminal assignment's recorded workspace, else the session's own.
            var parentWs = await ResolveAgentWorkspaceAsync(
                nontermBefore ?? new AgentAssignmentRow(string.Empty, agent.AgentId, null, agent.SessionId, null,
                    AgentAssignmentLifecycle.Running, AgentState.Idle, DeploymentExecutionMode.Pooled, null, null, null, null,
                    "resume", DateTimeOffset.UtcNow, null, null, null),
                agent.SessionId, cancellationToken);
            var requeued = await runner.RequeueRunAsync(new AgentRunRequest(
                agent.SessionId, parentWs, nontermBefore?.ModelId, brief, RunId: w.RunId), cancellationToken);
            if (requeued)
            {
                if (nontermBefore is not null)
                    await TryTransitionAsync(nontermBefore.AssignmentId, AgentAssignmentLifecycle.Running, AgentState.Preparing, cancellationToken, reason: "delegation-resume");
                resumed++;
            }
            // a held resume (capacity full) stays satisfied; the next consumer pass
            // retries — the durable wake is never lost.
            if (requeued) await Store().MarkWaitConsumedAsync(w.WaitId, cancellationToken);
        }
        return resumed;
    }

    // ---- queries ---------------------------------------------------------------

    public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken cancellationToken)
        => Store().GetAssignmentAsync(assignmentId, cancellationToken);

    public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken cancellationToken)
        => Store().GetNonterminalAsync(sessionId, cancellationToken);

    public async ValueTask<IReadOnlyList<AgentAssignmentRow>> ListAssignmentsAsync(CancellationToken cancellationToken)
    {
        var nonterm = await Store().ListNonterminalAsync(cancellationToken);
        var term = await Store().ListRecentTerminalAsync(20, cancellationToken);
        var all = new List<AgentAssignmentRow>(nonterm.Count + term.Count);
        all.AddRange(nonterm);
        all.AddRange(term);
        return all;
    }

    public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken cancellationToken)
        => Store().ListSubtreeAsync(agentId, cancellationToken);

    // ---- cancel -------------------------------------------------------------------

    public async ValueTask<AgentAssignmentLifecycle> CancelAsync(
        string assignmentId, bool subtree, CancellationToken cancellationToken)
    {
        var row = await Store().GetAssignmentAsync(assignmentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        if (!row.IsNonTerminal) return row.Lifecycle;

        var toCancel = new List<AgentAssignmentRow>();
        if (subtree && row.AgentId is not null)
            toCancel.AddRange(await Store().ListSubtreeAsync(row.AgentId, cancellationToken));
        toCancel.Add(row);

        foreach (var target in toCancel)
        {
            if (!target.IsNonTerminal) continue;
            await TryTransitionAsync(target.AssignmentId, AgentAssignmentLifecycle.Cancelled, AgentState.Idle, cancellationToken, reason: "cancelled");
            var runner = Runner();
            if (runner is not null)
            {
                // astra-2 §13: match by the durable run id (not by session) so a
                // CANCELLED target's runner record is reached precisely — a queued
                // or suspended record may share a session with a newer live run.
                var runId = await Store().GetRunIdAsync(target.AssignmentId, cancellationToken);
                var live = runner.ListRuns().FirstOrDefault(r =>
                    (runId is not null && r.RunId == runId) ||
                    (r.SessionId == target.SessionId && r.Outcome == RunState.Running));
                if (live is not null)
                {
                    runner.CancelRun(live.RunId);
                    // a CancelRun on a QUEUED record signals its token but leaves
                    // the queued record + the lane queue entry; purge both.
                    if (runId is not null)
                    {
                        try { await runner.CancelQueuedRun(runId); }
                        catch { /* best-effort: the row is already cancelled */ }
                    }
                }
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
        var row = await Store().GetAssignmentAsync(assignmentId, ct);
        if (row is null) return;
        var ok = await Store().TransitionAsync(
            assignmentId, row.Version, lifecycle, phase,
            row.PoolId, row.LaneId, row.DeploymentId, reason, null, ct);
        if (!ok)
        {
            var fresh = await Store().GetAssignmentAsync(assignmentId, ct);
            if (fresh is not null)
                await Store().TransitionAsync(
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
        var nonterm = await Store().ListNonterminalAsync(cancellationToken);
        foreach (var row in nonterm)
        {
            if (row.Lifecycle != AgentAssignmentLifecycle.Running) continue;
            var runner = Runner();
            var isLive = runner is not null
                && runner.ListRuns().Any(r => r.SessionId == row.SessionId && r.Outcome == RunState.Running);
            if (!isLive)
            {
                // astra-2 §15.D: a crash in the tool-batch window (side effects
                // executed, results not persisted) leaves a durable in-flight
                // checkpoint — quarantine the assignment as recovery-required
                // instead of re-running it: uncertain side effects are never
                // replayed automatically.
                if (await Store().HasToolBatchCheckpointAsync(row.AssignmentId, cancellationToken))
                {
                    var okQ = await Store().TransitionAsync(
                        row.AssignmentId, row.Version,
                        AgentAssignmentLifecycle.RecoveryRequired, AgentState.Idle,
                        row.PoolId, row.LaneId, row.DeploymentId,
                        "crash after tool effect before result persistence — side effects uncertain; recovery required, not auto-replayed", null, cancellationToken);
                    if (okQ)
                        _ctx.Log.Warning($"Reconcile: quarantined assignment {row.AssignmentId} (in-flight tool-batch checkpoint; recovery-required, no auto-replay)");
                    continue;
                }
                var ok = await Store().TransitionAsync(
                    row.AssignmentId, row.Version,
                    AgentAssignmentLifecycle.Queued, AgentState.Idle,
                    row.PoolId, row.LaneId, row.DeploymentId, "restart-reconciled", null, cancellationToken);
                if (ok)
                    _ctx.Log.Information($"Reconciled assignment {row.AssignmentId} to Queued (no live runner after restart)");
            }
        }

        // astra-2 §7/§16: durable queued records are re-entered into the admission
        // pipeline exactly once (adoption) — a restart must not strand them.
        try
        {
            var queued = await Store().ListQueuedForAdoptionAsync(cancellationToken);
            foreach (var q in queued)
            {
                var runner = Runner();
                if (runner is null)
                {
                    _ctx.Log.Information($"Reconcile: no runner for {q.AssignmentId} — left queued");
                    continue;
                }
                // astra-2 §8: a re-adopted run returns to the workspace its
                // assignment recorded (null = the session's workspace via the runner).
                var adoptionWs = await ResolveAgentWorkspaceAsync(
                    new AgentAssignmentRow(q.AssignmentId, string.Empty, null, q.SessionId, null,
                        AgentAssignmentLifecycle.Queued, AgentState.Idle, DeploymentExecutionMode.Pooled,
                        q.PoolId, null, q.DeploymentId, q.ModelId, "adopt", DateTimeOffset.UtcNow, null, null, null)
                    { WorkspaceMode = q.WorkspaceMode, WorkspacePath = q.WorkspacePath },
                    q.SessionId, cancellationToken);
                var requeued = await runner.RequeueRunAsync(new AgentRunRequest(
                    q.SessionId, adoptionWs, q.ModelId, q.Brief, RunId: q.OperationId), cancellationToken);
                _ctx.Log.Information(
                    $"Reconcile: re-entered queued assignment {q.AssignmentId} (run {q.OperationId}) as {(requeued ? "adopted" : "held")}");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warning($"Reconcile: queued adoption failed: {ex.Message}");
        }
    }
}
