namespace NetPI.Abstractions;

/// <summary>
/// astra-1 D2: a project change that was requested for a session WHILE one of
/// its runs is executing. The run's in-flight tool batch must keep its
/// original workspace, so the change is persisted as PENDING (visible to the
/// UI as "Switch to X pending.") and applied at the next safe boundary: after
/// the run unwinds, and before the session is usable for another send. The
/// pending selection is PERSISTED so a host restart retains it (no automatic
/// restart of the interrupted agent — the next run applies it).
///
/// One pending selection per session: a later enqueue REPLACES an earlier
/// unapplied one (visible via the replaced pending state).
/// </summary>
public interface IPendingProjectChangeStore
{
    /// <summary>
    /// Persist (or replace) the pending change. The
    /// <see cref="ProjectChangeRequest.Snapshot"/> embedded in the request is
    /// the authoritative context — applying later never re-resolves files.
    /// </summary>
    ValueTask<PendingProjectChangeInfo> EnqueueAsync(
        ProjectChangeRequest change, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current pending change for a session (the most recently enqueued
    /// one), or null when none is pending.
    /// </summary>
    ValueTask<PendingProjectChangeInfo?> PendingAsync(
        string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the pending change — only when it is still the one identified by
    /// <paramref name="operationId"/> (a newer selection enqueued in between
    /// must survive). Returns true when this call removed the row.
    /// </summary>
    ValueTask<bool> ClearAsync(string sessionId, string operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// astra-1 D2: one persisted pending change. Carries both the UI state (the
/// "Switch to X pending." indicator) and the FULL request, so applying it at
/// the safe boundary needs nothing but the session store.
/// </summary>
public sealed record PendingProjectChangeInfo(
    string SessionId,
    string OperationId,
    string ProjectId,
    /// <summary>Project name the user selected (for the pending indicator).</summary>
    string ProjectName,
    /// <summary>When the selection was made.</summary>
    DateTimeOffset EnqueuedAt,
    /// <summary>The full authoritative request (snapshot included).</summary>
    ProjectChangeRequest Request)
{
}
