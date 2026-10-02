using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Workspaces;

/// <summary>
/// The workspace records, and the binding of a session to one. The records live in this plugin's own collection; the binding is
/// attached to the session's <c>meta</c> (the core stores no field it cannot interpret): <c>meta.workspaceId</c> names the workspace
/// and <c>meta.cwd</c> is its root, which the core understands as the folder the session runs in, so a bound session keeps working in its
/// checkout even while this plugin is not loaded.
/// <para>Collection <c>workspaces</c>: key = the workspace id; the document is the <see cref="WorkspaceInfo"/> as JSON (camelCase, ISO
/// dates) plus <c>updatedMs</c>; index fields <c>projectId</c> (Text), <c>ownerSessionId</c> (Text), <c>updatedMs</c> (Integer).</para>
/// </summary>
internal sealed class WorkspaceStore : IWorkspaceStore
{
    internal const string Collection = "workspaces";

    private readonly IPluginContext _ctx;
    private readonly IDataCollection _items;

    public WorkspaceStore(IPluginContext ctx)
    {
        _ctx = ctx;
        _items = ctx.Data.Collection(Collection, new CollectionSpec().Text("projectId").Text("ownerSessionId").Integer("updatedMs"));
    }

    public IReadOnlyList<WorkspaceInfo> ListWorkspaces(string? projectId = null)
    {
        var query = new DataQuery().Order("updatedMs", descending: true);
        if (projectId is not null) query.Eq("projectId", projectId);
        return _items.Find(query).Select(d => Read(d.Doc)).ToList();
    }

    public WorkspaceInfo? GetWorkspace(string id) => string.IsNullOrEmpty(id) ? null : _items.Get(id) is { } doc ? Read(doc) : null;

    public WorkspaceInfo CreateWorkspace(WorkspaceInfo template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Path);
        var now = Now();
        var w = new WorkspaceInfo
        {
            Id = string.IsNullOrWhiteSpace(template.Id) ? Ids.New("wsp") : template.Id,
            Name = template.Name?.Trim() ?? "",
            Path = Normalize(template.Path),
            ProjectId = string.IsNullOrWhiteSpace(template.ProjectId) ? null : template.ProjectId,
            Kind = string.IsNullOrWhiteSpace(template.Kind) ? "folder" : template.Kind.Trim(),
            Branch = template.Branch,
            BaseCommit = template.BaseCommit,
            RepoCommonDir = template.RepoCommonDir,
            OwnerSessionId = template.OwnerSessionId,
            OwnerAgentId = template.OwnerAgentId,
            Managed = template.Managed,
            CreatedAt = now,
            UpdatedAt = now,
            Meta = template.Meta?.DeepClone() as JsonObject,
        };
        if (w.ProjectId is not null && _ctx.Sessions.GetProject(w.ProjectId) is null) throw new KeyNotFoundException($"Project {w.ProjectId} not found");
        if (!_items.Insert(w.Id, Write(w))) throw new InvalidOperationException($"Workspace {w.Id} already exists");
        _ctx.Events.Publish(WorkspaceEvents.Created, new { workspace = w });
        return w;
    }

    public WorkspaceInfo UpdateWorkspace(string id, Action<WorkspaceInfo> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var w = _ctx.Data.Transaction(() =>
        {
            var current = GetWorkspace(id) ?? throw new KeyNotFoundException($"Workspace {id} not found");
            mutate(current);
            current.Id = id;
            current.UpdatedAt = Now();
            _items.Put(id, Write(current));
            return current;
        });
        _ctx.Events.Publish(WorkspaceEvents.Updated, new { workspace = w });
        return w;
    }

    /// <summary>
    /// Remove the record. The sessions that were bound to it work in their project again: the binding goes from each (the session
    /// service announces the change), and what announces the unbinding follows the deletion, as it always did.
    /// </summary>
    public bool DeleteWorkspace(string id)
    {
        if (!_items.Delete(id)) return false;
        var bound = BoundSessions(id, includeArchived: true);
        _ctx.Events.Publish(WorkspaceEvents.Deleted, new { id });
        foreach (var sessionId in bound)
        {
            SessionInfo session;
            try { session = _ctx.Sessions.UpdateSession(sessionId, s => Bind(s, null)); }
            catch (KeyNotFoundException) { continue; }   // deleted meanwhile
            // Every session that was bound is told, the message-less ones too (the session service announces a change to a stored
            // session itself, and nothing about one that is only in memory).
            if (session.MessageCount == 0) _ctx.Events.Publish(EventTypes.SessionUpdated, new { session });
            _ctx.Events.Publish(WorkspaceEvents.SessionBound, new { sessionId, workspaceId = (string?)null, cwd = _ctx.Sessions.GetCwd(session), binding = (object?)null });
        }
        return true;
    }

    public WorkspaceInfo? GetSessionWorkspace(string sessionId) =>
        SessionWorkspace.Of(_ctx.Sessions.GetSession(sessionId)) is { } id ? GetWorkspace(id) : null;

    /// <summary>
    /// The ids of the sessions bound to a workspace: every kind (chats and subagents), the message-less (in-memory only) ones as well, asked of
    /// the session service by the attached key, so there is no second record of the binding to go stale. Archived sessions are not counted
    /// unless asked: they are not working in their workspace, and deleting the workspace unbinds them.
    /// </summary>
    public IReadOnlyList<string> BoundSessions(string workspaceId, bool includeArchived = false) => BoundSessions(_ctx.Sessions, workspaceId, includeArchived);

    internal static IReadOnlyList<string> BoundSessions(ISessionStore sessions, string workspaceId, bool includeArchived = false)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return [];
        return sessions.ListSessions(new SessionQuery
        {
            AttachedKey = SessionWorkspace.MetaKey, AttachedValue = workspaceId,
            IncludeSubagents = true, IncludeArchived = includeArchived, IncludeUnmaterialized = true, Limit = 5000,
        }).Select(s => s.Id).ToList();
    }

    public void SetSessionWorkspace(string sessionId, string? workspaceId)
    {
        workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
        var workspace = workspaceId is null ? null : GetWorkspace(workspaceId) ?? throw new KeyNotFoundException($"Workspace {workspaceId} not found");
        var before = _ctx.Sessions.GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        if (SessionWorkspace.Of(before) == workspaceId) return;
        var session = _ctx.Sessions.UpdateSession(sessionId, s => Bind(s, workspace));
        // A session with no messages yet is in memory only and has announced nothing, so there is nothing to follow up.
        if (session.MessageCount == 0) return;
        _ctx.Events.Publish(WorkspaceEvents.SessionBound, new
        {
            sessionId,
            workspaceId,
            cwd = _ctx.Sessions.GetCwd(session),
            binding = workspace is null ? null : (object?)BindingOf(workspace),
        });
    }

    /// <summary>Write or clear the binding on a session's meta: the workspace id, and its root as the folder the session runs in.</summary>
    private static void Bind(SessionInfo session, WorkspaceInfo? workspace)
    {
        if (workspace is null)
        {
            session.Meta?.Remove(SessionWorkspace.MetaKey);
            session.Meta?.Remove(SessionCwd.MetaKey);
            if (session.Meta is { Count: 0 }) session.Meta = null;
            return;
        }
        session.Meta ??= new JsonObject();
        session.Meta[SessionWorkspace.MetaKey] = workspace.Id;
        session.Meta[SessionCwd.MetaKey] = workspace.Path;
    }

    /// <summary>The resolved binding of a workspace record: its root plus what git says about it (filled in by the resolver).</summary>
    internal static WorkspaceBinding BindingOf(WorkspaceInfo w) =>
        new(w.Id, w.Path, w.Branch, w.BaseCommit, w.RepoCommonDir, w.Kind, w.OwnerSessionId, w.OwnerAgentId, w.Managed);

    private static JsonObject Write(WorkspaceInfo w)
    {
        var doc = JsonSerializer.SerializeToNode(w, NetPiJson.Options) as JsonObject ?? [];
        doc["updatedMs"] = w.UpdatedAt.ToUnixTimeMilliseconds();
        return doc;
    }

    private static WorkspaceInfo Read(JsonObject doc) => doc.Deserialize<WorkspaceInfo>(NetPiJson.Options) ?? new WorkspaceInfo();

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
