using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Sessions;

/// <summary>
/// Projects, sessions and messages in SQLite (scope <c>core</c>). Publishes <c>project.*</c> / <c>session.*</c>
/// (broadcast) and <c>message.*</c> / <c>messages.compacted</c> (session scoped) events.
/// </summary>
internal sealed class SessionStore : ISessionStore
{
    public const string DefaultTitle = "New session";
    private const int MaxTitleLength = 60;

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE projects (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            last_used_at INTEGER
        );
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL DEFAULT '',
            project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
            parent_session_id TEXT,
            kind TEXT NOT NULL DEFAULT 'chat',
            model TEXT,
            reasoning TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0,
            message_count INTEGER NOT NULL DEFAULT 0,
            context_tokens INTEGER NOT NULL DEFAULT 0,
            meta TEXT
        );
        CREATE INDEX ix_sessions_updated ON sessions(updated_at DESC);
        CREATE INDEX ix_sessions_project ON sessions(project_id, updated_at DESC);
        CREATE INDEX ix_sessions_parent ON sessions(parent_session_id);
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            parts TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            provider TEXT,
            model TEXT,
            stop_reason TEXT,
            usage TEXT,
            duration_ms INTEGER,
            compacted INTEGER NOT NULL DEFAULT 0,
            meta TEXT
        );
        CREATE UNIQUE INDEX ix_messages_session_seq ON messages(session_id, seq);
        CREATE TABLE kv (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        """,
    ];

    private const string SessionColumns =
        "id, title, project_id, parent_session_id, kind, model, reasoning, created_at, updated_at, archived, message_count, context_tokens, meta";
    private const string MessageColumns =
        "id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta";

    private readonly IDatabase _db;
    private readonly IEventBus _bus;
    private readonly string _defaultWorkspace;

    public SessionStore(IDatabase db, IEventBus bus, string defaultWorkspace)
    {
        _db = db;
        _bus = bus;
        _defaultWorkspace = defaultWorkspace;
        _db.Migrate("core", Migrations);
    }

    // ------------------------------------------------------------------ projects

    public IReadOnlyList<ProjectInfo> ListProjects() =>
        _db.Query("SELECT * FROM projects ORDER BY COALESCE(last_used_at, updated_at) DESC, name COLLATE NOCASE", null, ReadProject);

    public ProjectInfo? GetProject(string id) =>
        _db.QuerySingle("SELECT * FROM projects WHERE id = @id", new { id }, ReadProject);

    public ProjectInfo CreateProject(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = PathUtil.Normalize(path);
        var now = Now();
        var project = new ProjectInfo
        {
            Id = Ids.New("prj"),
            Name = string.IsNullOrWhiteSpace(name) ? DefaultProjectName(full) : name.Trim(),
            Path = full,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Execute("INSERT INTO projects(id, name, path, created_at, updated_at) VALUES(@Id, @Name, @Path, @CreatedAt, @UpdatedAt)", project);
        Publish(EventTypes.ProjectCreated, new { project });
        return project;
    }

    public ProjectInfo UpdateProject(string id, string? name, string? path)
    {
        var project = _db.Transaction(_ =>
        {
            var p = GetProject(id) ?? throw new KeyNotFoundException($"Project {id} not found");
            if (!string.IsNullOrWhiteSpace(name)) p.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(path)) p.Path = PathUtil.Normalize(path);
            p.UpdatedAt = Now();
            _db.Execute("UPDATE projects SET name = @Name, path = @Path, updated_at = @UpdatedAt WHERE id = @Id", p);
            return p;
        });
        Publish(EventTypes.ProjectUpdated, new { project });
        return project;
    }

    public void DeleteProject(string id)
    {
        var affected = _db.Transaction(_ =>
        {
            if (GetProject(id) is null) throw new KeyNotFoundException($"Project {id} not found");
            var ids = _db.Query("SELECT id FROM sessions WHERE project_id = @id", new { id }, r => r.GetString("id"));
            _db.Execute("UPDATE sessions SET project_id = NULL WHERE project_id = @id", new { id });
            _db.Execute("DELETE FROM projects WHERE id = @id", new { id });
            return ids;
        });
        Publish(EventTypes.ProjectDeleted, new { id });
        foreach (var sid in affected)
            if (GetSession(sid) is { } s) Publish(EventTypes.SessionUpdated, new { session = s });
    }

    // ------------------------------------------------------------------ sessions

    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var args = new Dictionary<string, object?>
        {
            ["limit"] = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 5000),
            ["offset"] = Math.Max(0, query.Offset),
        };
        if (query.ProjectId is not null)
        {
            if (query.ProjectId.Length == 0) where.Add("project_id IS NULL");
            else
            {
                where.Add("project_id = @project");
                args["project"] = query.ProjectId;
            }
        }
        if (query.ParentSessionId is not null)
        {
            where.Add("parent_session_id = @parent");
            args["parent"] = query.ParentSessionId;
        }
        else if (!query.IncludeSubagents)
        {
            where.Add("kind <> 'subagent'");
        }
        if (!query.IncludeArchived) where.Add("archived = 0");
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("title LIKE @search ESCAPE '\\'");
            args["search"] = "%" + query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        }
        var sql = $"SELECT {SessionColumns} FROM sessions" +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY updated_at DESC, id DESC LIMIT @limit OFFSET @offset";
        return _db.Query(sql, args, ReadSession);
    }

    public SessionInfo? GetSession(string id) =>
        _db.QuerySingle($"SELECT {SessionColumns} FROM sessions WHERE id = @id", new { id }, ReadSession);

    public SessionInfo CreateSession(SessionInfo template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var now = Now();
        var s = new SessionInfo
        {
            Id = string.IsNullOrWhiteSpace(template.Id) ? Ids.New("ses") : template.Id,
            Title = string.IsNullOrWhiteSpace(template.Title) ? DefaultTitle : template.Title.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(template.ProjectId) ? null : template.ProjectId,
            ParentSessionId = string.IsNullOrWhiteSpace(template.ParentSessionId) ? null : template.ParentSessionId,
            Kind = string.IsNullOrWhiteSpace(template.Kind) ? "chat" : template.Kind,
            Model = template.Model,
            Reasoning = template.Reasoning,
            CreatedAt = now,
            UpdatedAt = now,
            Archived = template.Archived,
            MessageCount = 0,
            ContextTokens = template.ContextTokens,
            Meta = template.Meta?.DeepClone() as JsonObject,
        };
        _db.Transaction(_ =>
        {
            if (s.ProjectId is not null)
            {
                if (GetProject(s.ProjectId) is null) throw new KeyNotFoundException($"Project {s.ProjectId} not found");
                _db.Execute("UPDATE projects SET last_used_at = @now WHERE id = @id", new { now, id = s.ProjectId });
            }
            _db.Execute($"""
                INSERT INTO sessions({SessionColumns})
                VALUES(@Id, @Title, @ProjectId, @ParentSessionId, @Kind, @Model, @Reasoning, @CreatedAt, @UpdatedAt, @Archived, @MessageCount, @ContextTokens, @Meta)
                """, s);
        });
        Publish(EventTypes.SessionCreated, new { session = s });
        return s;
    }

    public SessionInfo UpdateSession(string id, Action<SessionInfo> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var session = _db.Transaction(_ =>
        {
            var s = GetSession(id) ?? throw new KeyNotFoundException($"Session {id} not found");
            mutate(s);
            s.Id = id;
            s.UpdatedAt = Now();
            if (string.IsNullOrWhiteSpace(s.Kind)) s.Kind = "chat";
            _db.Execute("""
                UPDATE sessions SET title = @Title, project_id = @ProjectId, parent_session_id = @ParentSessionId, kind = @Kind,
                    model = @Model, reasoning = @Reasoning, updated_at = @UpdatedAt, archived = @Archived,
                    message_count = @MessageCount, context_tokens = @ContextTokens, meta = @Meta
                WHERE id = @Id
                """, s);
            return s;
        });
        Publish(EventTypes.SessionUpdated, new { session });
        return session;
    }

    public void DeleteSession(string id)
    {
        var deleted = _db.Transaction(_ =>
        {
            var ids = _db.Query("""
                WITH RECURSIVE tree(id) AS (
                    SELECT id FROM sessions WHERE id = @id
                    UNION
                    SELECT s.id FROM sessions s JOIN tree t ON s.parent_session_id = t.id
                )
                SELECT id FROM tree
                """, new { id }, r => r.GetString("id"));
            if (ids.Count == 0) throw new KeyNotFoundException($"Session {id} not found");
            foreach (var sid in ids)
            {
                _db.Execute("DELETE FROM messages WHERE session_id = @sid", new { sid });
                _db.Execute("DELETE FROM sessions WHERE id = @sid", new { sid });
            }
            return ids;
        });
        // Children first so a UI never sees an orphaned child of a deleted parent.
        for (var i = deleted.Count - 1; i >= 0; i--) Publish(EventTypes.SessionDeleted, new { id = deleted[i] });
    }

    public SessionInfo SetSessionProject(string sessionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) projectId = null;
        var (session, notice) = _db.Transaction(_ =>
        {
            var s = GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
            if (s.ProjectId == projectId) return (s, (ChatMessage?)null);
            ProjectInfo? project = null;
            if (projectId is not null)
            {
                project = GetProject(projectId) ?? throw new KeyNotFoundException($"Project {projectId} not found");
                _db.Execute("UPDATE projects SET last_used_at = @now WHERE id = @id", new { now = Now(), id = projectId });
            }
            _db.Execute("UPDATE sessions SET project_id = @projectId, updated_at = @now WHERE id = @sessionId", new { projectId, now = Now(), sessionId });
            var text = project is not null
                ? $"The workspace changed: this session is now attached to project \"{project.Name}\" at {project.Path}. " +
                  $"Your working directory is now {project.Path} — resolve relative paths against it."
                : $"The workspace changed: this session is no longer attached to a project. " +
                  $"Your working directory is now the default workspace at {_defaultWorkspace} — resolve relative paths against it.";
            var msg = ChatMessage.NoticeText(text, "project");
            msg.Meta!["projectId"] = projectId;
            msg.Meta!["cwd"] = project?.Path ?? _defaultWorkspace;
            var appended = AppendMessageCore(sessionId, msg, out var updated);
            return (updated, appended);
        });
        if (notice is not null)
        {
            PublishMessage(EventTypes.MessageAdded, notice);
            Publish(EventTypes.SessionUpdated, new { session });
        }
        return session;
    }

    public string GetCwd(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ProjectId is not null && GetProject(session.ProjectId) is { } p && !string.IsNullOrWhiteSpace(p.Path)) return p.Path;
        return _defaultWorkspace;
    }

    // ------------------------------------------------------------------ messages

    public ChatMessage AppendMessage(string sessionId, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        SessionInfo session = null!;
        var appended = _db.Transaction(_ => AppendMessageCore(sessionId, message, out session));
        PublishMessage(EventTypes.MessageAdded, appended);
        Publish(EventTypes.SessionUpdated, new { session });
        return appended;
    }

    /// <summary>Insert (inside a transaction), bump counters and auto-title. Returns the stored message.</summary>
    private ChatMessage AppendMessageCore(string sessionId, ChatMessage message, out SessionInfo session)
    {
        var current = GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        var seq = (_db.Scalar<long?>("SELECT MAX(seq) FROM messages WHERE session_id = @sessionId", new { sessionId }) ?? 0) + 1;
        message.SessionId = sessionId;
        message.Seq = seq;
        message.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt.ToUnixTimeMilliseconds());
        message.Id = _db.Insert($"""
            INSERT INTO messages(session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta)
            VALUES(@SessionId, @Seq, @Role, @Parts, @CreatedAt, @Provider, @Model, @StopReason, @Usage, @DurationMs, @Compacted, @Meta)
            """, MessageArgs(message));

        var title = current.Title;
        if (message.Role == MessageRole.User && (string.IsNullOrWhiteSpace(title) || title == DefaultTitle) && MakeTitle(message.Text) is { } auto)
            title = auto;
        var now = Now();
        _db.Execute("UPDATE sessions SET updated_at = @now, message_count = message_count + 1, title = @title WHERE id = @sessionId",
            new { now, title, sessionId });
        current.UpdatedAt = now;
        current.MessageCount++;
        current.Title = title;
        session = current;
        return message;
    }

    public void UpdateMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var n = _db.Execute("""
            UPDATE messages SET role = @Role, parts = @Parts, provider = @Provider, model = @Model, stop_reason = @StopReason,
                usage = @Usage, duration_ms = @DurationMs, compacted = @Compacted, meta = @Meta
            WHERE id = @Id
            """, MessageArgs(message));
        if (n == 0) throw new KeyNotFoundException($"Message {message.Id} not found");
        if (string.IsNullOrEmpty(message.SessionId) || message.Seq == 0)
        {
            var stored = GetMessage(message.Id)!;
            message.SessionId = stored.SessionId;
            message.Seq = stored.Seq;
        }
        PublishMessage(EventTypes.MessageUpdated, message);
    }

    public ChatMessage? GetMessage(long id) =>
        _db.QuerySingle($"SELECT {MessageColumns} FROM messages WHERE id = @id", new { id }, ReadMessage);

    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null)
    {
        if (beforeSeq is null && limit is null)
            return _db.Query($"SELECT {MessageColumns} FROM messages WHERE session_id = @sessionId ORDER BY seq", new { sessionId }, ReadMessage);
        var page = _db.Query($"""
            SELECT {MessageColumns} FROM messages
            WHERE session_id = @sessionId AND (@before IS NULL OR seq < @before)
            ORDER BY seq DESC LIMIT @limit
            """, new { sessionId, before = beforeSeq, limit = limit is > 0 ? limit.Value : -1 }, ReadMessage);
        page.Reverse();
        return page;
    }

    public IReadOnlyList<ChatMessage> GetContextMessages(string sessionId)
    {
        var list = _db.Query($"SELECT {MessageColumns} FROM messages WHERE session_id = @sessionId AND compacted = 0 ORDER BY seq",
            new { sessionId }, ReadMessage);
        // Summaries are appended after the retained tail; the model must see the latest one first.
        var idx = list.FindLastIndex(m => m.Role == MessageRole.Summary);
        if (idx > 0)
        {
            var summary = list[idx];
            list.RemoveAt(idx);
            list.Insert(0, summary);
        }
        return list;
    }

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        _db.Execute("UPDATE messages SET compacted = 1 WHERE session_id = @sessionId AND seq <= @upToSeq AND compacted = 0",
            new { sessionId, upToSeq });
        _bus.Publish(new BusEvent
        {
            Type = EventTypes.MessagesCompacted, SessionId = sessionId, Source = "host",
            Data = new { sessionId, upToSeq },
        });
    }

    // ------------------------------------------------------------------ key/value (ui.state)

    public string? GetValue(string key) =>
        _db.Scalar<string>("SELECT value FROM kv WHERE key = @key", new { key });

    public void SetValue(string key, string? value)
    {
        if (value is null) _db.Execute("DELETE FROM kv WHERE key = @key", new { key });
        else _db.Execute("INSERT INTO kv(key, value) VALUES(@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { key, value });
    }

    // ------------------------------------------------------------------ mapping

    private static object MessageArgs(ChatMessage m) => new
    {
        m.Id,
        m.SessionId,
        m.Seq,
        Role = RoleName(m.Role),
        Parts = JsonSerializer.Serialize(m.Parts ?? [], NetPiJson.Options),
        m.CreatedAt,
        m.Provider,
        m.Model,
        m.StopReason,
        Usage = m.Usage is null ? null : JsonSerializer.Serialize(m.Usage, NetPiJson.Options),
        m.DurationMs,
        m.Compacted,
        Meta = m.Meta?.ToJsonString(),
    };

    public static string RoleName(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        MessageRole.Notice => "notice",
        MessageRole.Summary => "summary",
        _ => role.ToString().ToLowerInvariant(),
    };

    private static MessageRole ParseRole(string s) =>
        Enum.TryParse<MessageRole>(s, ignoreCase: true, out var r) ? r : MessageRole.Notice;

    private static ChatMessage ReadMessage(IDbRow r) => new()
    {
        Id = r.GetInt64("id"),
        SessionId = r.GetString("session_id"),
        Seq = r.GetInt64("seq"),
        Role = ParseRole(r.GetString("role")),
        Parts = JsonSerializer.Deserialize<List<MessagePart>>(r.GetString("parts"), NetPiJson.Options) ?? [],
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
        Provider = r.GetStringOrNull("provider"),
        Model = r.GetStringOrNull("model"),
        StopReason = r.GetStringOrNull("stop_reason"),
        Usage = r.GetStringOrNull("usage") is { } u ? JsonSerializer.Deserialize<Usage>(u, NetPiJson.Options) : null,
        DurationMs = r.GetInt64OrNull("duration_ms"),
        Compacted = r.GetInt64("compacted") != 0,
        Meta = ParseObject(r.GetStringOrNull("meta")),
    };

    private static SessionInfo ReadSession(IDbRow r) => new()
    {
        Id = r.GetString("id"),
        Title = r.GetString("title"),
        ProjectId = r.GetStringOrNull("project_id"),
        ParentSessionId = r.GetStringOrNull("parent_session_id"),
        Kind = r.GetString("kind"),
        Model = r.GetStringOrNull("model"),
        Reasoning = r.GetStringOrNull("reasoning"),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("updated_at")),
        Archived = r.GetInt64("archived") != 0,
        MessageCount = r.GetInt64("message_count"),
        ContextTokens = r.GetInt64("context_tokens"),
        Meta = ParseObject(r.GetStringOrNull("meta")),
    };

    private static ProjectInfo ReadProject(IDbRow r) => new()
    {
        Id = r.GetString("id"),
        Name = r.GetString("name"),
        Path = r.GetString("path"),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("updated_at")),
        LastUsedAt = r.GetInt64OrNull("last_used_at") is { } l ? DateTimeOffset.FromUnixTimeMilliseconds(l) : null,
    };

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>First line of the text, at most 60 characters.</summary>
    public static string? MakeTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim();
        var nl = line.IndexOfAny(['\r', '\n']);
        if (nl >= 0) line = line[..nl];
        line = line.Trim();
        if (line.Length == 0) return null;
        return line.Length <= MaxTitleLength ? line : line[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }

    private static string DefaultProjectName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void Publish(string type, object data) =>
        _bus.Publish(new BusEvent { Type = type, Data = data, Source = "host" });

    /// <summary>
    /// Publish a message event with a shallow copy (own Parts list) so a caller that keeps mutating the message after
    /// the call cannot race the serializer on the bus thread.
    /// </summary>
    private void PublishMessage(string type, ChatMessage m)
    {
        var copy = new ChatMessage
        {
            Id = m.Id, Seq = m.Seq, SessionId = m.SessionId, Role = m.Role, Parts = [.. m.Parts], CreatedAt = m.CreatedAt,
            Provider = m.Provider, Model = m.Model, StopReason = m.StopReason, Usage = m.Usage, DurationMs = m.DurationMs,
            Compacted = m.Compacted, Meta = m.Meta?.DeepClone() as JsonObject,
        };
        _bus.Publish(new BusEvent { Type = type, SessionId = m.SessionId, Source = "host", Data = new { sessionId = m.SessionId, message = copy } });
    }
}
