using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>A project is just a name and a folder on disk (a switchable workspace).</summary>
public sealed class ProjectInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Per-project data other parts keep here (e.g. <c>profile</c>: the default profile of new sessions).</summary>
    public JsonObject? Meta { get; set; }
}

/// <summary>Sessions are the primary unit of work. A session may or may not be attached to a project.</summary>
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectId { get; set; }
    /// <summary>The checkout this conversation works in (<c>sessions.workspace_id</c>), or null to use the project's path.
    /// Binding is per worker: two sessions of one project may be bound to different workspaces.</summary>
    public string? WorkspaceId { get; set; }
    public string? ParentSessionId { get; set; }
    /// <summary>chat | subagent</summary>
    public string Kind { get; set; } = "chat";
    /// <summary>"provider/model" ref, null = default model.</summary>
    public string? Model { get; set; }
    public string? Reasoning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
    /// <summary>Pinned: the UI keeps it in a "Pinned" group at the top of the session list, above the recency groups, and the
    /// list orders pinned sessions first so one stays inside the newest-first window even as it ages.</summary>
    public bool Pinned { get; set; }
    public long MessageCount { get; set; }
    /// <summary>Last known context size in tokens (for the UI context meter).</summary>
    public long ContextTokens { get; set; }
    public JsonObject? Meta { get; set; }
}

/// <summary>
/// The rule behind <c>session.changed</c>: which meta keys a write actually changed. It lives here, not in the store,
/// because it is the event's contract — a plugin that watches <c>session.changed</c> is told keys, and a test double of the
/// session store has to report the same ones as the real store or every plugin test drifts from production.
/// <para>A key with a null value counts as no key, and a key rewritten with the same value is not a change.</para>
/// </summary>
public static class SessionMeta
{
    /// <summary>The meta as it is now, key → its JSON. Take it <em>before</em> a write: the write edits the same object.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(JsonObject? meta)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (meta is not null)
            foreach (var (key, value) in meta)
                if (value is not null) map[key] = value.ToJsonString();
        return map;
    }

    /// <summary>The keys whose value is not what <paramref name="before"/> said: added, changed or removed, sorted.</summary>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string> before, JsonObject? after)
    {
        var now = Snapshot(after);
        var keys = new List<string>();
        foreach (var (key, value) in now)
            if (!before.TryGetValue(key, out var was) || was != value) keys.Add(key);
        foreach (var key in before.Keys)
            if (!now.ContainsKey(key)) keys.Add(key);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

/// <summary>
/// The profile a session runs with: <c>meta.profile</c>, written by the profiles plugin (and by a project's default for a new
/// chat). The key lives here, not in that plugin, because <c>session.changed</c> reports meta keys as data: a consumer of
/// the event must be able to name the key without knowing which plugin wrote it (idea-m7vmue).
/// </summary>
public static class SessionProfile
{
    public const string MetaKey = "profile";
}

/// <summary>
/// Tools switched off for one session: <c>meta.toolsOff</c> lists tool names its agent is not sent (the <c>agent.setTools</c>
/// RPC; a subagent session starts with its parent's list). A change after the first model call applies from the next call.
/// </summary>
public static class SessionTools
{
    public const string MetaKey = "toolsOff";

    /// <summary>The tool names switched off for the session (case-insensitive); empty when none.</summary>
    public static HashSet<string> Off(SessionInfo? session)
    {
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (session?.Meta?[MetaKey] is JsonArray names)
            foreach (var n in names)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) off.Add(s.Trim());
        return off;
    }
}

/// <summary>
/// The opening of a session's system prompt when its profile replaces the built-in one: <c>meta.identity</c>, set by the
/// profiles plugin and rendered by the context plugin at the first model call (and again after <c>context.reset</c>).
/// </summary>
public static class SessionIdentity
{
    public const string MetaKey = "identity";

    public static string? Of(SessionInfo? session) =>
        session?.Meta?[MetaKey] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

public sealed class SessionQuery
{
    public string? ProjectId { get; set; }
    public string? Search { get; set; }
    /// <summary>Include subagent sessions (default false).</summary>
    public bool IncludeSubagents { get; set; }
    public string? ParentSessionId { get; set; }
    public bool IncludeArchived { get; set; }
    /// <summary>Only archived sessions; takes precedence over <see cref="IncludeArchived"/>.</summary>
    public bool ArchivedOnly { get; set; }
    /// <summary>With <see cref="AttachedValue"/>: only sessions whose <c>meta[AttachedKey]</c> is that string. The way a plugin finds the sessions it attached something to.</summary>
    public string? AttachedKey { get; set; }
    public string? AttachedValue { get; set; }
    /// <summary>Also return message-less (in-memory) sessions that match; they are not stored and never show in the session list.</summary>
    public bool IncludeUnmaterialized { get; set; }
    public int Limit { get; set; } = 100;
    public int Offset { get; set; }
}

public interface ISessionStore
{
    // Projects
    IReadOnlyList<ProjectInfo> ListProjects();
    ProjectInfo? GetProject(string id);
    ProjectInfo CreateProject(string name, string path);
    ProjectInfo UpdateProject(string id, string? name, string? path);
    void DeleteProject(string id);

    // Sessions
    IReadOnlyList<SessionInfo> ListSessions(SessionQuery query);
    SessionInfo? GetSession(string id);
    SessionInfo CreateSession(SessionInfo template);
    SessionInfo UpdateSession(string id, Action<SessionInfo> mutate);
    void DeleteSession(string id);
    /// <summary>Change the session's project and append a notice so the agent learns about it.</summary>
    SessionInfo SetSessionProject(string sessionId, string? projectId);
    /// <summary>Working directory for a session: its workspace root when it is bound to one, else its project folder or the default workspace.</summary>
    string GetCwd(SessionInfo session);
    /// <summary>
    /// The ids of the sessions currently bound to <paramref name="workspaceId"/> — chats and subagents alike, and
    /// message-less (transient) sessions as well: the exact answer to "what is bound to this workspace?", not a
    /// page of <see cref="ListSessions"/>. Archived sessions are not counted by default: an archived session is not
    /// working in its workspace, and retiring the workspace unbinds it (the session falls back to its project).
    /// Pass <paramref name="includeArchived"/> to see them too.
    /// </summary>
    IReadOnlyList<string> SessionIdsUsingWorkspace(string workspaceId, bool includeArchived = false) =>
        throw new NotSupportedException("This store does not answer the exact question; implement SessionIdsUsingWorkspace.");

    // Messages
    /// <summary>Append a message (assigns Id/Seq, publishes message.added).</summary>
    ChatMessage AppendMessage(string sessionId, ChatMessage message);
    void UpdateMessage(ChatMessage message);
    ChatMessage? GetMessage(long id);
    /// <summary>Messages ordered by seq ascending. With <paramref name="beforeSeq"/>/<paramref name="limit"/> returns the page ending just before beforeSeq.</summary>
    IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null);
    /// <summary>Messages that make up the model context: the latest summary (if any) plus all non-compacted messages.</summary>
    IReadOnlyList<ChatMessage> GetContextMessages(string sessionId);
    /// <summary>Mark all messages with seq &lt;= upToSeq as compacted.</summary>
    void MarkCompacted(string sessionId, long upToSeq);

    /// <summary>
    /// A new session (from <paramref name="template"/>) with a copy of the session's messages up to
    /// <paramref name="upToSeq"/>: the same seqs, times, parts, usage and meta, and compaction as it was at that point.
    /// The host publishes session.created once the copy is complete, then session.forked. This default copies message by
    /// message (and publishes what <see cref="AppendMessage"/> does).
    /// </summary>
    SessionInfo ForkSession(string sessionId, long upToSeq, SessionInfo template)
    {
        var fork = CreateSession(template);
        foreach (var m in GetMessages(sessionId))
        {
            if (m.Seq > upToSeq) break;
            AppendMessage(fork.Id, new ChatMessage
            {
                Role = m.Role, Parts = m.Parts, CreatedAt = m.CreatedAt, Provider = m.Provider, Model = m.Model, StopReason = m.StopReason,
                Usage = m.Usage, DurationMs = m.DurationMs, Compacted = m.Compacted, Meta = m.Meta?.DeepClone() as JsonObject,
            });
        }
        return GetSession(fork.Id) ?? fork;
    }
}
