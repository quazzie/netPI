using NetPI.Abstractions;

namespace NetPI.Agent;

/// <summary>
/// astra-1 D2 (slice 2): the RUNNER's side of the pending project change —
/// applying the selection at the run's SAFE BOUNDARY: after the run has
/// unwound (its in-flight tool batch — which must keep its original workspace
/// — is done) and BEFORE the session is usable for another send.
///
/// Serialized per session with the send path (the runner hands out the same
/// gates from <c>StartRunAsync</c>), so a boundary apply can never race a new
/// send on the same session — send, project-change and compaction all funnel
/// through one gate.
///
/// Applying uses the STORED snapshot (the request embedded in the pending row
/// — never re-resolving files) and commits via
/// <see cref="ISessionStore.SetProjectAsync"/> with the SAME OperationId as
/// the enqueue (entry id == OperationId → idempotent). On failure the prior
/// project stays active and the pending row is cleared (the selection is
/// consumed — the user sees the switch fail and can re-select).
/// </summary>
/// <summary>astra-1 D2 (slice 2): the pending change the boundary applied.</summary>
public sealed record ProjectApplyResult(string OperationId, string ProjectId, string ProjectName);

public sealed class RunnerSafeBoundary
{
    private readonly IPluginContext _ctx;

    /// <summary>
    /// One gate per session (send / project-change / compaction serialization).
    /// The map is owned by the runner (mutated under its lock).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _gates = new();

    public RunnerSafeBoundary(IPluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Acquire the gate for a session (owned by the CALLER — the runner's
    /// StartRunAsync and the boundary apply hold it across their critical
    /// sections). The gate lives until the dictionary is pruned; in practice
    /// sessions are long-lived so the map stays small.
    /// </summary>
    public System.Threading.SemaphoreSlim Gate(string sessionId)
        => _gates.GetOrAdd(sessionId, _ => new(1, 1));

    private T? Resolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }
    }

    /// <summary>
    /// Apply the session's pending project change at the safe boundary
    /// (NO-OP when there is none, the session is unknown, or another send
    /// already took the gate — in which case the NEXT boundary applies it;
    /// the row persists until it is consumed). Returns the applied operation
    /// id (for the <c>AgentEvent.ProjectApplied</c> broadcast), or null.
    /// </summary>
    public async ValueTask<ProjectApplyResult?> ApplyPendingAsync(string sessionId, CancellationToken ct)
    {
        var pendingStore = Resolve<IPendingProjectChangeStore>("pending-projects");
        var pending = pendingStore is null ? null : await pendingStore.PendingAsync(sessionId, ct);
        if (pending is null) return null;

        var store = Resolve<ISessionStore>("sessions");
        if (store is null) return null;

        var gate = Gate(sessionId);
        if (!await gate.WaitAsync(0, CancellationToken.None)) return null; // a send owns the boundary — next one applies
        try
        {
            // Re-read INSIDE the gate: a selection enqueued (or replaced) in
            // the tiny window before we took it must be the one committed.
            var current = pendingStore is null ? null : await pendingStore.PendingAsync(sessionId, ct);
            if (current is null) return null;

            try
            {
                // SAME OperationId as the enqueue → idempotent commit (entry
                // UNIQUE(id) on retry); the stored snapshot is authoritative.
                await store.SetProjectAsync(current.Request, ct);

                var removed = await Resolve<IPendingProjectChangeStore>("pending-projects")!
                    .ClearAsync(sessionId, current.OperationId, ct);
                if (!removed)
                    _ctx.Log.Warning($"pending project change for session {sessionId} was replaced during apply; " +
                                     $"the newer selection stays pending");

                return new ProjectApplyResult(current.OperationId, current.ProjectId, current.ProjectName);
            }
            catch (Exception ex)
            {
                // FAILED APPLY: the prior project stays active. Consume the
                // selection so a dead row (e.g. a since-deleted project)
                // never silently re-fires on every subsequent boundary.
                _ctx.Log.Warning($"pending project change for session {sessionId} failed to apply: {ex.Message}");
                try { await Resolve<IPendingProjectChangeStore>("pending-projects")!
                    .ClearAsync(sessionId, current.OperationId, ct); }
                catch { /* the row will surface again at the next boundary */ }
                return null;
            }
        }
        finally { gate.Release(); }
    }
}
