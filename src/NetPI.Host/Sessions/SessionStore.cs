using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Sessions;

/// <summary>
/// Projects, sessions and messages in SQLite (scope <c>core</c>). Publishes <c>project.*</c> / <c>session.*</c>
/// (broadcast) and <c>message.*</c> / <c>messages.compacted</c> (session scoped) events. A session with no messages is
/// transient: it lives in memory until its first message materializes it, so an abandoned empty chat leaves nothing.
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
        // per-project data other parts keep (e.g. the default profile of new sessions)
        "ALTER TABLE projects ADD COLUMN meta TEXT;",
    ];

    private const string SessionColumns =
        "id, title, project_id, parent_session_id, kind, model, reasoning, created_at, updated_at, archived, message_count, context_tokens, meta";
    private const string MessageColumns =
        "id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta";

    private readonly IDatabase _db;
    private readonly IEventBus _bus;
    private readonly string _defaultWorkspace;

    /// <summary>Sessions with no messages: in memory only, until the first message materializes them (AppendMessage).</summary>
    private readonly Dictionary<string, SessionInfo> _transient = new();
    private readonly object _transientLock = new();

    // The deserialized context of the sessions being worked on. Without it every turn re-reads and re-parses the whole
    // history (measured: ~30 ms and megabytes of garbage for a 1,500-message chat), and does so again for every hook
    // that appends a notice. Only the few most recent sessions are kept, and a session too big to be worth retaining
    // is never cached at all — see CacheContext.
    private const int ContextCacheSessions = 3;
    private const int ContextCacheMaxMessages = 2000;
    private readonly Dictionary<string, ChatMessage[]> _context = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _contextLru = new();
    private readonly object _contextLock = new();

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

    public ProjectInfo UpdateProject(string id, string? name, string? path) => UpdateProject(id, name, path, null);

    /// <summary>Update a project; <paramref name="meta"/> is merged key by key (a null value removes the key).</summary>
    public ProjectInfo UpdateProject(string id, string? name, string? path, JsonObject? meta)
    {
        var project = _db.Transaction(_ =>
        {
            var p = GetProject(id) ?? throw new KeyNotFoundException($"Project {id} not found");
            if (!string.IsNullOrWhiteSpace(name)) p.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(path)) p.Path = PathUtil.Normalize(path);
            if (meta is not null)
            {
                p.Meta ??= new JsonObject();
                foreach (var (key, value) in meta)
                {
                    if (value is null) p.Meta.Remove(key);
                    else p.Meta[key] = value.DeepClone();
                }
                if (p.Meta.Count == 0) p.Meta = null;
            }
            p.UpdatedAt = Now();
            _db.Execute("UPDATE projects SET name = @Name, path = @Path, updated_at = @UpdatedAt, meta = @Meta WHERE id = @Id", p);
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
            var detached = _db.Query($"SELECT {SessionColumns} FROM sessions WHERE project_id = @id", new { id }, ReadSession);
            _db.Execute("UPDATE sessions SET project_id = NULL WHERE project_id = @id", new { id });
            _db.Execute("DELETE FROM projects WHERE id = @id", new { id });
            return detached;
        });
        // Transient (no-message) sessions have no row: detach them in memory, like the ones above
        List<SessionInfo> detachedTransient = [];
        lock (_transientLock)
        {
            var t = _transient.Values.Where(s => s.ProjectId == id).ToList();
            if (t.Count > 0)
            {
                var now = Now();
                foreach (var s in t) { s.ProjectId = null; s.UpdatedAt = now; }
                detachedTransient = t;
            }
        }
        Publish(EventTypes.ProjectDeleted, new { id });
        // The sessions were read whole above: one query, not a GetSession per detached session.
        foreach (var s in affected) Publish(EventTypes.SessionUpdated, new { session = s });
        foreach (var s in detachedTransient) Publish(EventTypes.SessionUpdated, new { session = s });
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
        if (query.ArchivedOnly) where.Add("archived = 1");
        else if (!query.IncludeArchived) where.Add("archived = 0");
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

    public SessionInfo? GetSession(string id) => TransientSession(id) ??
        _db.QuerySingle($"SELECT {SessionColumns} FROM sessions WHERE id = @id", new { id }, ReadSession);

    /// <summary>A copy of a transient (no messages yet) session, or null.</summary>
    private SessionInfo? TransientSession(string id)
    {
        lock (_transientLock)
            return _transient.TryGetValue(id, out var s) ? CopySession(s) : null;
    }

    private static SessionInfo CopySession(SessionInfo s) => new()
    {
        Id = s.Id, Title = s.Title, ProjectId = s.ProjectId, ParentSessionId = s.ParentSessionId, Kind = s.Kind,
        Model = s.Model, Reasoning = s.Reasoning, CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt, Archived = s.Archived,
        MessageCount = s.MessageCount, ContextTokens = s.ContextTokens, Meta = s.Meta?.DeepClone() as JsonObject,
    };

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
        if (s.ProjectId is not null && GetProject(s.ProjectId) is null) throw new KeyNotFoundException($"Project {s.ProjectId} not found");
        // No row, no session.created, no project last_used_at: the first message materializes the session (AppendMessage).
        lock (_transientLock) _transient[s.Id] = s;
        return s;
    }

    public SessionInfo UpdateSession(string id, Action<SessionInfo> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        IReadOnlyDictionary<string, string>? wasMeta = null;
        lock (_transientLock)
        {
            if (_transient.TryGetValue(id, out var t))
            {
                // A no-message session is not announced: mutate the in-memory copy, no row, no session.updated.
                var s = CopySession(t);
                var wasTransientMeta = SessionMeta.Snapshot(s.Meta);
                mutate(s);
                s.Id = id;
                if (string.IsNullOrWhiteSpace(s.Kind)) s.Kind = "chat";
                s.UpdatedAt = Now();
                _transient[id] = s;
                PublishMetaChanged(id, wasTransientMeta, s.Meta);
                return s;
            }
        }
        var session = _db.Transaction(_ =>
        {
            var s = GetSession(id) ?? throw new KeyNotFoundException($"Session {id} not found");
            // a snapshot, not the object: mutate() edits the very same JsonObject in place
            wasMeta = SessionMeta.Snapshot(s.Meta);
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
        PublishMetaChanged(id, wasMeta, session.Meta);
        return session;
    }

    /// <summary>
    /// Publish <c>session.changed</c> when the update actually changed the session's meta, naming the keys. A plugin that
    /// needs to know that the user switched this chat's profile (or the tools off for it) reads this instead of looking for
    /// a notice kind (idea-m7vmue). Rewriting a key with the same value, or changing a field outside meta, says nothing.
    /// </summary>
    private void PublishMetaChanged(string id, IReadOnlyDictionary<string, string> before, JsonObject? after)
    {
        var keys = SessionMeta.Changed(before, after);
        if (keys.Count > 0) Publish(EventTypes.SessionChanged, new { sessionId = id, keys });
    }

    public void DeleteSession(string id)
    {
        lock (_transientLock)
        {
            if (_transient.Remove(id))
            {
                // No row to delete (and nothing can be a child: a subagent materializes with its task message, a fork has no parent).
                Publish(EventTypes.SessionDeleted, new { id });
                return;
            }
        }
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
            // Two set-based deletes over the tree, not two statements per session in it.
            const string Tree = """
                WITH RECURSIVE tree(id) AS (
                    SELECT id FROM sessions WHERE id = @id
                    UNION
                    SELECT s.id FROM sessions s JOIN tree t ON s.parent_session_id = t.id
                )
                """;
            _db.Execute(Tree + "DELETE FROM messages WHERE session_id IN (SELECT id FROM tree)", new { id });
            _db.Execute(Tree + "DELETE FROM sessions WHERE id IN (SELECT id FROM tree)", new { id });
            return ids;
        });
        // Children first so a UI never sees an orphaned child of a deleted parent.
        for (var i = deleted.Count - 1; i >= 0; i--) Publish(EventTypes.SessionDeleted, new { id = deleted[i] });
        foreach (var sid in deleted) DropContext(sid);
    }

    /// <summary>
    /// Attach (or detach) a project. The host only stores it and publishes <see cref="EventTypes.SessionProject"/>; what the
    /// model is told is up to plugins (the context plugin appends a "project" notice, the AGENTS.md plugin the new
    /// instructions): the conversation is only ever appended to.
    /// </summary>
    public SessionInfo SetSessionProject(string sessionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) projectId = null;
        lock (_transientLock)
        {
            if (_transient.TryGetValue(sessionId, out var t))
            {
                if (t.ProjectId == projectId) return CopySession(t);
                if (projectId is not null && GetProject(projectId) is null) throw new KeyNotFoundException($"Project {projectId} not found");
                t.ProjectId = projectId;
                t.UpdatedAt = Now();
                return CopySession(t); // no row, no session.project: nothing has been announced yet
            }
        }
        var (session, changed) = _db.Transaction(_ =>
        {
            var s = GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
            if (s.ProjectId == projectId) return (s, false);
            if (projectId is not null)
            {
                if (GetProject(projectId) is null) throw new KeyNotFoundException($"Project {projectId} not found");
                _db.Execute("UPDATE projects SET last_used_at = @now WHERE id = @id", new { now = Now(), id = projectId });
            }
            _db.Execute("UPDATE sessions SET project_id = @projectId, updated_at = @now WHERE id = @sessionId", new { projectId, now = Now(), sessionId });
            return (GetSession(sessionId)!, true);
        });
        if (changed)
        {
            Publish(EventTypes.SessionUpdated, new { session });
            Publish(EventTypes.SessionProject, new { sessionId, projectId, cwd = GetCwd(session) });
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
        lock (_transientLock)
        {
            if (_transient.TryGetValue(sessionId, out var transient))
                return MaterializeFirstMessage(sessionId, transient, message);
        }
        SessionInfo session = null!;
        var appended = _db.Transaction(_ => AppendMessageCore(sessionId, message, out session));
        PublishMessage(EventTypes.MessageAdded, appended);
        Publish(EventTypes.SessionUpdated, new { session });
        return appended;
    }

    /// <summary>
    /// The first message of a session materializes it: the sessions row (and the project's last_used_at) plus the
    /// message in one transaction, then session.created (it now has a message, as <c>sessions.list</c> shows it),
    /// message.added and session.updated. Runs under the transient gate, so a racing append for the same session
    /// waits and then takes the DB path.
    /// </summary>
    private ChatMessage MaterializeFirstMessage(string sessionId, SessionInfo transient, ChatMessage message)
    {
        lock (_transientLock)
        {
            SessionInfo session = null!;
            _db.Transaction(_ =>
            {
                var now = Now();
                if (transient.ProjectId is not null)
                {
                    if (GetProject(transient.ProjectId) is null) throw new KeyNotFoundException($"Project {transient.ProjectId} not found");
                    _db.Execute("UPDATE projects SET last_used_at = @now WHERE id = @id", new { now, id = transient.ProjectId });
                }
                _db.Execute($"""
                    INSERT INTO sessions({SessionColumns})
                    VALUES(@Id, @Title, @ProjectId, @ParentSessionId, @Kind, @Model, @Reasoning, @CreatedAt, @UpdatedAt, @Archived, @MessageCount, @ContextTokens, @Meta)
                    """, transient);
                _transient.Remove(sessionId);
                AppendMessageCore(sessionId, message, out session);
            });
            Publish(EventTypes.SessionCreated, new { session });
            PublishMessage(EventTypes.MessageAdded, message);
            Publish(EventTypes.SessionUpdated, new { session });
            return message;
        }
    }

    /// <summary>Insert (inside a transaction), bump counters and auto-title. Returns the stored message.</summary>
    private ChatMessage AppendMessageCore(string sessionId, ChatMessage message, out SessionInfo session)
    {
        DropContext(sessionId);   // the new message is in the context from here on
        var current = GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        message.SessionId = sessionId;
        message.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt.ToUnixTimeMilliseconds());
        // The next seq is worked out by the insert itself and handed back with it: one statement per message, not a
        // SELECT MAX(seq) and then the insert. Safe under the unique index on (session_id, seq) and the transaction.
        var stored = _db.QuerySingle<(long Id, long Seq)>("""
            INSERT INTO messages(session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta)
            SELECT @SessionId, COALESCE((SELECT MAX(seq) FROM messages WHERE session_id = @SessionId), 0) + 1,
                @Role, @Parts, @CreatedAt, @Provider, @Model, @StopReason, @Usage, @DurationMs, @Compacted, @Meta
            RETURNING id, seq
            """, MessageArgs(message), r => (r.GetInt64("id"), r.GetInt64("seq")));
        message.Id = stored.Id;
        message.Seq = stored.Seq;

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
        DropContext(message.SessionId);
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
        var rows = ContextRows(sessionId);
        // The caller gets its own list: it may reorder it, and the cached array must stay in database order.
        var list = new List<ChatMessage>(rows);
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

    /// <summary>The session's uncompacted messages in database order, from the cache when it has them.</summary>
    private ChatMessage[] ContextRows(string sessionId)
    {
        lock (_contextLock)
            if (_context.TryGetValue(sessionId, out var cached))
            {
                TouchContextLocked(sessionId);
                return cached;
            }
        var rows = _db.Query($"SELECT {MessageColumns} FROM messages WHERE session_id = @sessionId AND compacted = 0 ORDER BY seq",
            new { sessionId }, ReadMessage).ToArray();
        lock (_contextLock)
        {
            if (!_context.ContainsKey(sessionId) && rows.Length <= ContextCacheMaxMessages)
            {
                _context[sessionId] = rows;
                _contextLru.AddLast(sessionId);
                while (_contextLru.Count > ContextCacheSessions)
                {
                    _context.Remove(_contextLru.First!.Value);
                    _contextLru.RemoveFirst();
                }
            }
        }
        return rows;
    }

    /// <summary>Forget a session's cached context. Every write that changes a message calls it.</summary>
    private void DropContext(string sessionId)
    {
        lock (_contextLock)
        {
            if (_context.Remove(sessionId)) _contextLru.Remove(sessionId);
        }
    }

    private void TouchContextLocked(string sessionId)
    {
        var node = _contextLru.Find(sessionId);
        if (node is null) return;
        _contextLru.Remove(node);
        _contextLru.AddLast(node);
    }

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        _db.Execute("UPDATE messages SET compacted = 1 WHERE session_id = @sessionId AND seq <= @upToSeq AND compacted = 0",
            new { sessionId, upToSeq });
        DropContext(sessionId);
        _bus.Publish(new BusEvent
        {
            Type = EventTypes.MessagesCompacted, SessionId = sessionId, Source = "host",
            Data = new { sessionId, upToSeq },
        });
    }

    // ------------------------------------------------------------------ fork

    /// <summary>
    /// A new session with a copy of the session's messages up to <paramref name="upToSeq"/>, in one transaction: the same
    /// seqs (so seq references such as a summary's <c>coversUpToSeq</c> stay valid), times, parts, usage and meta. What is
    /// compacted is what was compacted at that point (<see cref="CompactionAsOfEnd"/>), and a copied <c>meta.for</c> that
    /// names a message id names its copy. <c>session.created</c> goes out once the copy is complete (a handler never sees
    /// it half done), then <c>session.forked</c>.
    /// </summary>
    public SessionInfo ForkSession(string sessionId, long upToSeq, SessionInfo template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var now = Now();
        var fork = new SessionInfo
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
            ContextTokens = template.ContextTokens,
            Meta = template.Meta?.DeepClone() as JsonObject,
        };
        if (GetSession(sessionId) is null) throw new KeyNotFoundException($"Session {sessionId} not found");
        var copies = _db.Scalar<long>("SELECT COUNT(*) FROM messages WHERE session_id = @sid AND seq <= @upToSeq", new { sid = sessionId, upToSeq });
        if (copies == 0)
        {
            // Nothing to copy: the fork stays transient, like a fresh empty chat — no row, no session.created, no session.forked.
            lock (_transientLock) _transient[fork.Id] = fork;
            return fork;
        }
        _db.Transaction(_ =>
        {
            if (fork.ProjectId is not null)
            {
                if (GetProject(fork.ProjectId) is null) throw new KeyNotFoundException($"Project {fork.ProjectId} not found");
                _db.Execute("UPDATE projects SET last_used_at = @now WHERE id = @id", new { now, id = fork.ProjectId });
            }
            _db.Execute($"""
                INSERT INTO sessions({SessionColumns})
                VALUES(@Id, @Title, @ProjectId, @ParentSessionId, @Kind, @Model, @Reasoning, @CreatedAt, @UpdatedAt, @Archived, @MessageCount, @ContextTokens, @Meta)
                """, fork);
            fork.MessageCount = _db.Execute("""
                INSERT INTO messages(session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta)
                SELECT @to, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta
                FROM messages WHERE session_id = @from AND seq <= @upToSeq ORDER BY seq
                """, new { to = fork.Id, from = sessionId, upToSeq });
            CompactionAsOfEnd(fork.Id);
            MapMessageIds(sessionId, fork.Id);
            _db.Execute("UPDATE sessions SET message_count = @MessageCount WHERE id = @Id", fork);
        });
        Publish(EventTypes.SessionCreated, new { session = fork });
        _bus.Publish(new BusEvent { Type = EventTypes.SessionForked, Source = "host", Data = new { sessionId = fork.Id, fromSessionId = sessionId, upToSeq } });
        return fork;
    }

    /// <summary>
    /// The compaction flags of a copy as they were at its last message: the latest summary in it covers what its
    /// <c>meta.coversUpToSeq</c> says and the summaries before it are superseded; anything after it is not compacted (a
    /// later compaction of the original, past the copy's end, does not count). A summary without that range (written
    /// before it was recorded) keeps the flags before it.
    /// </summary>
    private void CompactionAsOfEnd(string sessionId)
    {
        var summaries = _db.Query("SELECT seq, meta FROM messages WHERE session_id = @sessionId AND role = @role ORDER BY seq",
            new { sessionId, role = RoleName(MessageRole.Summary) }, r => (Seq: r.GetInt64("seq"), Meta: r.GetStringOrNull("meta")));
        if (summaries.Count == 0)
        {
            _db.Execute("UPDATE messages SET compacted = 0 WHERE session_id = @sessionId", new { sessionId });
            return;
        }
        var (latest, meta) = summaries[^1];
        long? covers = null;
        try
        {
            if (meta is not null && JsonNode.Parse(meta)?["coversUpToSeq"] is JsonValue v && v.TryGetValue<long>(out var c)) covers = c;
        }
        catch (JsonException) { }
        if (covers is { } upTo)
            _db.Execute("""
                UPDATE messages SET compacted = CASE WHEN seq <= @upTo OR (role = @role AND seq < @latest) THEN 1 ELSE 0 END
                WHERE session_id = @sessionId
                """, new { sessionId, upTo, latest, role = RoleName(MessageRole.Summary) });
        else
            _db.Execute("UPDATE messages SET compacted = 0 WHERE session_id = @sessionId AND seq >= @latest", new { sessionId, latest });
    }

    /// <summary>A copied message whose <c>meta.for</c> names a message of the original (a skill notice's user message) names the copy.</summary>
    private void MapMessageIds(string from, string to)
    {
        var refs = _db.Query("SELECT id, meta FROM messages WHERE session_id = @to AND meta LIKE '%\"for\"%'", new { to },
            r => (Id: r.GetInt64("id"), Meta: r.GetString("meta")));
        if (refs.Count == 0) return;
        var seqOf = _db.Query("SELECT id, seq FROM messages WHERE session_id = @from", new { from }, r => (Id: r.GetInt64("id"), Seq: r.GetInt64("seq")))
            .ToDictionary(x => x.Id, x => x.Seq);
        var idAt = _db.Query("SELECT id, seq FROM messages WHERE session_id = @to", new { to }, r => (Id: r.GetInt64("id"), Seq: r.GetInt64("seq")))
            .ToDictionary(x => x.Seq, x => x.Id);
        foreach (var (id, text) in refs)
        {
            JsonObject? meta;
            try { meta = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { continue; }
            if (meta?["for"] is not JsonValue v || !long.TryParse(v.ToString(), out var old)) continue;
            if (!seqOf.TryGetValue(old, out var seq) || !idAt.TryGetValue(seq, out var copy)) continue;
            meta["for"] = copy.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _db.Execute("UPDATE messages SET meta = @meta WHERE id = @id", new { meta = meta.ToJsonString(), id });
        }
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
        Meta = ParseObject(r.GetStringOrNull("meta")),
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
