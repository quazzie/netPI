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
}

/// <summary>Sessions are the primary unit of work. A session may or may not be attached to a project.</summary>
public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ParentSessionId { get; set; }
    /// <summary>chat | subagent</summary>
    public string Kind { get; set; } = "chat";
    /// <summary>"provider/model" ref, null = default model.</summary>
    public string? Model { get; set; }
    public string? Reasoning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
    public long MessageCount { get; set; }
    /// <summary>Last known context size in tokens (for the UI context meter).</summary>
    public long ContextTokens { get; set; }
    public JsonObject? Meta { get; set; }
}

public sealed class SessionQuery
{
    public string? ProjectId { get; set; }
    public string? Search { get; set; }
    /// <summary>Include subagent sessions (default false).</summary>
    public bool IncludeSubagents { get; set; }
    public string? ParentSessionId { get; set; }
    public bool IncludeArchived { get; set; }
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
    /// <summary>Working directory for a session: its project folder or the default workspace.</summary>
    string GetCwd(SessionInfo session);

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
}
