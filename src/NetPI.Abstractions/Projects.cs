using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// astra-1 C: project identity. A project is a named workspace directory —
/// the unit that instruction discovery layers on. <see cref="WorkspacePath"/>
/// is the user-facing path; the storage layer keys uniqueness on its canonical
/// form (case/slash-insensitive on Windows) via
/// <c>SqliteSessionStore.NormalizeProjectPathKey</c>.
/// </summary>
public sealed record ProjectInfo(
    string Id,
    string Name,
    string WorkspacePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() => $"{Name} ({WorkspacePath})";
}

/// <summary>
/// astra-1 C: the EXACT effective instruction context for a project, captured
/// at selection. Reopening a session uses its persisted snapshot until an
/// explicit refresh is applied.
/// </summary>
public sealed record ProjectContextSnapshot(
    string ProjectId,
    string ProjectName,
    string WorkspacePath,
    /// <summary>File paths the snapshot was assembled from, layer order (broad → specific).</summary>
    IReadOnlyList<string> InstructionSources,
    /// <summary>The effective AGENTS text — the exact layered content as resolved.</summary>
    string EffectiveInstructions,
    /// <summary>Stable hash over EffectiveInstructions (change detection).</summary>
    string ContentHash,
    DateTimeOffset CapturedAt)
{
    public override string ToString() => $"{ProjectName} @ rev {ContentHash[0..8]}";
}

/// <summary>
/// astra-1 C: an idempotent request to (re)attach a project to a session and
/// persist a fresh context snapshot. <see cref="OperationId"/> is the stable
/// retry key — repeating the SAME operation must not create duplicate
/// transcript entries or bump the context revision twice.
/// <see cref="ExpectedContextRevision"/> is an optimistic-concurrency guard:
/// a non-zero revision that no longer matches the session rejects the change.
/// </summary>
public sealed record ProjectChangeRequest(
    string OperationId,
    string SessionId,
    string ProjectId,
    /// <summary>Optimistic-concurrency guard; -1 disables the check.</summary>
    int ExpectedContextRevision,
    /// <summary>The snapshot produced for this change (the caller resolves it).</summary>
    ProjectContextSnapshot Snapshot)
{
    public ProjectChangeRequest(string operationId, string sessionId, string projectId,
        ProjectContextSnapshot snapshot)
        : this(operationId, sessionId, projectId, -1, snapshot) { }
}

/// <summary>
/// astra-1 C: the result of an atomic session-project change — the resulting
/// session plus the persisted (or already-persisted, on retry) entry.
/// </summary>
public sealed record ProjectChangeResult(
    SessionInfo Session,
    SessionEntry Entry);

/// <summary>
/// astra-1 C: a project change failed to apply — an unreadable EXISTING
/// instruction file is an actionable error (distinct from a valid MISSING
/// file, which is an empty layer).
/// </summary>
public sealed class ProjectContextChangeException(string message) : Exception(message)
{
}

/// <summary>
/// astra-1 C: project CRUD, backed by the existing storage plugin (the plan
/// explicitly keeps project persistence in the SQLite plugin — a separate
/// project-management plugin is unnecessary for the initial implementation).
/// </summary>
public interface IProjectStore
{
    /// <summary>All projects, newest-first.</summary>
    ValueTask<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<ProjectInfo?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the project whose canonical workspace path matches
    /// <paramref name="workspacePath"/> (case/slash-insensitive on Windows).
    /// </summary>
    ValueTask<ProjectInfo?> GetByWorkspaceAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a project; a workspace path that already has a project returns
    /// the EXISTING one (upsert by canonical path — no duplicate projects for
    /// the same directory).
    /// </summary>
    ValueTask<ProjectInfo> CreateAsync(string name, string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Rename a project.</summary>
    ValueTask RenameAsync(string id, string newName, CancellationToken cancellationToken = default);

    /// <summary>Delete a project (sessions referencing it keep their snapshot; project_id is not FK-enforced).</summary>
    ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// astra-1 C: resolves a project's EXACT effective instruction context
/// (instruction discovery + snapshot). Implemented by the context plugin and
/// resolved by the agent surface so the two ALCs never reference each other
/// (reload-safe). The resolution happens at project SELECTION — never per
/// streamed token or tool call.
/// </summary>
public interface IInstructionContextResolver
{
    /// <summary>Capture the effective instruction context for a project right now.</summary>
    ValueTask<ProjectContextSnapshot> ResolveAsync(
        string projectId, string projectName, string workspace, CancellationToken cancellationToken = default);
}
