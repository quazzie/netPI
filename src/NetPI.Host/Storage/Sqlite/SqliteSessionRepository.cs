using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Sqlite;

/// <summary>Projects, sessions and messages in SQLite: the statements of the old session store, one per primitive of <see cref="ISessionRepository"/>.</summary>
internal sealed class SqliteSessionRepository(Database db) : ISessionRepository
{
    private const string SessionColumns =
        "id, title, project_id, parent_session_id, kind, model, reasoning, created_at, updated_at, archived, pinned, message_count, context_tokens, meta";
    private const string MessageColumns =
        "id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta";

    private const int ConstraintViolation = 19;

    public T Atomic<T>(Func<ISessionRepository, T> work) => db.Transaction(_ => work(this));

    public void Atomic(Action<ISessionRepository> work) => db.Transaction(_ => work(this));

    // ------------------------------------------------------------------ projects

    public IReadOnlyList<ProjectInfo> ListProjects() =>
        db.Query("SELECT * FROM projects ORDER BY COALESCE(last_used_at, updated_at) DESC, name COLLATE NOCASE", null, ReadProject);

    public ProjectInfo? GetProject(string id) => db.QuerySingle("SELECT * FROM projects WHERE id = @id", new { id }, ReadProject);

    public void InsertProject(ProjectInfo project)
    {
        try
        {
            db.Execute("""
                INSERT INTO projects(id, name, path, created_at, updated_at, last_used_at, meta)
                VALUES(@Id, @Name, @Path, @CreatedAt, @UpdatedAt, @LastUsedAt, @Meta)
                """, project);
        }
        catch (SqliteException ex) when (ex.Code == ConstraintViolation)
        {
            throw new StorageException($"Project {project.Id} already exists", ex);
        }
    }

    public bool UpdateProject(ProjectInfo project) =>
        db.Execute("UPDATE projects SET name = @Name, path = @Path, updated_at = @UpdatedAt, meta = @Meta WHERE id = @Id", project) > 0;

    public void TouchProject(string id, DateTimeOffset at) =>
        db.Execute("UPDATE projects SET last_used_at = @at WHERE id = @id", new { at, id });

    public bool DeleteProject(string id) => db.Execute("DELETE FROM projects WHERE id = @id", new { id }) > 0;

    public int ClearProject(string projectId, DateTimeOffset at) =>
        db.Execute("UPDATE sessions SET project_id = NULL, updated_at = @at WHERE project_id = @projectId", new { at, projectId });

    // ------------------------------------------------------------------ sessions

    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var args = new Dictionary<string, object?>();
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
        // An attached key with no value to compare it to matches nothing.
        if (query.AttachedKey is { Length: > 0 } && query.AttachedValue is null) return [];
        var attached = query.AttachedKey is { Length: > 0 } key ? (Key: key, Value: query.AttachedValue!) : default;
        if (attached.Key is not null)
        {
            // A cheap text prefilter; the exact test is below, on the parsed value (the sessions table is small, so no JSON function is needed).
            where.Add("meta LIKE @attached ESCAPE '\\'");
            args["attached"] = "%\"" + attached.Key.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "\"%";
        }
        var limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 5000);
        var offset = Math.Max(0, query.Offset);
        var sql = $"SELECT {SessionColumns} FROM sessions" +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY pinned DESC, updated_at DESC, id DESC";
        if (attached.Key is null)
        {
            args["limit"] = limit;
            args["offset"] = offset;
            return db.Query(sql + " LIMIT @limit OFFSET @offset", args, ReadSession);
        }
        var matches = db.Query(sql, args, ReadSession)
            .Where(s => s.Meta?[attached.Key] is JsonValue v && v.TryGetValue<string>(out var text) && text == attached.Value);
        return matches.Skip(offset).Take(limit).ToList();
    }

    public SessionInfo? GetSession(string id) => db.QuerySingle($"SELECT {SessionColumns} FROM sessions WHERE id = @id", new { id }, ReadSession);

    public void InsertSession(SessionInfo session)
    {
        try
        {
            db.Execute($"""
                INSERT INTO sessions({SessionColumns})
                VALUES(@Id, @Title, @ProjectId, @ParentSessionId, @Kind, @Model, @Reasoning, @CreatedAt, @UpdatedAt, @Archived, @Pinned, @MessageCount, @ContextTokens, @Meta)
                """, session);
        }
        catch (SqliteException ex) when (ex.Code == ConstraintViolation)
        {
            throw new StorageException($"Session {session.Id} cannot be stored: {ex.Message}", ex);
        }
    }

    public bool UpdateSession(SessionInfo session) => db.Execute("""
        UPDATE sessions SET title = @Title, project_id = @ProjectId, parent_session_id = @ParentSessionId, kind = @Kind,
            model = @Model, reasoning = @Reasoning, updated_at = @UpdatedAt, archived = @Archived, pinned = @Pinned,
            message_count = @MessageCount, context_tokens = @ContextTokens, meta = @Meta
        WHERE id = @Id
        """, session) > 0;

    public IReadOnlyList<string> DeleteSessionTree(string id)
    {
        const string Tree = """
            WITH RECURSIVE tree(id) AS (
                SELECT id FROM sessions WHERE id = @id
                UNION
                SELECT s.id FROM sessions s JOIN tree t ON s.parent_session_id = t.id
            )
            """;
        return db.Transaction(_ =>
        {
            var ids = db.Query(Tree + "SELECT id FROM tree", new { id }, r => r.GetString("id"));
            if (ids.Count == 0) return (IReadOnlyList<string>)ids;
            // Two set-based deletes over the tree, not two statements per session in it.
            db.Execute(Tree + "DELETE FROM messages WHERE session_id IN (SELECT id FROM tree)", new { id });
            db.Execute(Tree + "DELETE FROM sessions WHERE id IN (SELECT id FROM tree)", new { id });
            return ids;
        });
    }

    // ------------------------------------------------------------------ messages

    public ChatMessage AppendMessage(ChatMessage message)
    {
        // The next seq is worked out by the insert itself and handed back with it: one statement per message. Safe under
        // the unique index on (session_id, seq) and the transaction.
        (long Id, long Seq) stored;
        try
        {
            stored = db.QuerySingle<(long Id, long Seq)>("""
                INSERT INTO messages(session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta)
                SELECT @SessionId, COALESCE((SELECT MAX(seq) FROM messages WHERE session_id = @SessionId), 0) + 1,
                    @Role, @Parts, @CreatedAt, @Provider, @Model, @StopReason, @Usage, @DurationMs, @Compacted, @Meta
                RETURNING id, seq
                """, MessageArgs(message), r => (r.GetInt64("id"), r.GetInt64("seq")));
        }
        catch (SqliteException ex) when (ex.Code == ConstraintViolation)
        {
            throw new KeyNotFoundException($"Session {message.SessionId} not found");
        }
        message.Id = stored.Id;
        message.Seq = stored.Seq;
        return message;
    }

    public void RecordAppend(string sessionId, DateTimeOffset at, string title) =>
        db.Execute("UPDATE sessions SET updated_at = @at, message_count = message_count + 1, title = @title WHERE id = @sessionId",
            new { at, title, sessionId });

    public bool UpdateMessage(ChatMessage message) => db.Execute("""
        UPDATE messages SET role = @Role, parts = @Parts, provider = @Provider, model = @Model, stop_reason = @StopReason,
            usage = @Usage, duration_ms = @DurationMs, compacted = @Compacted, meta = @Meta
        WHERE id = @Id
        """, MessageArgs(message)) > 0;

    public ChatMessage? GetMessage(long id) => db.QuerySingle($"SELECT {MessageColumns} FROM messages WHERE id = @id", new { id }, ReadMessage);

    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null)
    {
        if (beforeSeq is null && limit is null)
            return db.Query($"SELECT {MessageColumns} FROM messages WHERE session_id = @sessionId ORDER BY seq", new { sessionId }, ReadMessage);
        // A NULL @before means "no upper bound"; COALESCE keeps the predicate a range seek on (session_id, seq) — the
        // OR form of it was not seekable (0.77 ms vs 0.12 ms on 15k rows).
        var page = db.Query($"""
            SELECT {MessageColumns} FROM messages
            WHERE session_id = @sessionId AND seq < COALESCE(@before, 9223372036854775807)
            ORDER BY seq DESC LIMIT @limit
            """, new { sessionId, before = beforeSeq, limit = limit is > 0 ? limit.Value : -1 }, ReadMessage);
        page.Reverse();
        return page;
    }

    public IReadOnlyList<ChatMessage> GetMessagesAfter(string sessionId, long afterSeq, int limit) =>
        // A range seek on (session_id, seq), like the page before.
        db.Query($"""
            SELECT {MessageColumns} FROM messages
            WHERE session_id = @sessionId AND seq > @after
            ORDER BY seq LIMIT @limit
            """, new { sessionId, after = afterSeq, limit = Math.Max(1, limit) }, ReadMessage);

    public (IReadOnlyList<ChatMessage> Rows, long Newest) ReadContext(string sessionId)
    {
        // One statement, one consistent view: the rows are every live message of the session, so the newest live seq is the
        // last row's (0 when there is none). Read as a second statement it could be lower — a MarkCompacted committed in
        // between takes the newest row out of the MAX after the rows already carried it — which the port rules out.
        var rows = db.Query($"SELECT {MessageColumns} FROM messages WHERE session_id = @sessionId AND compacted = 0 ORDER BY seq",
            new { sessionId }, ReadMessage);
        return (rows, rows.Count == 0 ? 0 : rows[^1].Seq);
    }

    public void MarkCompacted(string sessionId, long upToSeq) =>
        db.Execute("UPDATE messages SET compacted = 1 WHERE session_id = @sessionId AND seq <= @upToSeq AND compacted = 0", new { sessionId, upToSeq });

    public int CopyMessages(string fromSessionId, string toSessionId, long upToSeq)
    {
        try
        {
            return db.Execute("""
                INSERT INTO messages(session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta)
                SELECT @to, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta
                FROM messages WHERE session_id = @from AND seq <= @upToSeq ORDER BY seq
                """, new { to = toSessionId, from = fromSessionId, upToSeq });
        }
        catch (SqliteException ex) when (ex.Code == ConstraintViolation)
        {
            // the target session is missing (a foreign key), or already holds one of these seqs (the unique index)
            throw new StorageException($"The messages of {fromSessionId} cannot be copied into {toSessionId}: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<MessageStub> MessageStubs(string sessionId) =>
        db.Query("SELECT id, seq, role, compacted, meta FROM messages WHERE session_id = @sessionId ORDER BY seq", new { sessionId },
            r => new MessageStub(r.GetInt64("id"), r.GetInt64("seq"), ParseRole(r.GetString("role")), r.GetInt64("compacted") != 0,
                ParseMeta(r.GetStringOrNull("meta"), "message " + r.GetInt64("id").ToString(CultureInfo.InvariantCulture))));

    public void SetCompacted(string sessionId, IReadOnlySet<long> compactedSeqs) =>
        db.Transaction(_ =>
        {
            db.Execute("UPDATE messages SET compacted = 0 WHERE session_id = @sessionId AND compacted <> 0", new { sessionId });
            // In chunks: SQLite caps the number of parameters of one statement.
            foreach (var chunk in compactedSeqs.Chunk(500))
            {
                var args = new List<object?>(chunk.Length + 1) { sessionId };
                args.AddRange(chunk.Select(s => (object?)s));
                db.Execute($"UPDATE messages SET compacted = 1 WHERE session_id = ? AND seq IN ({string.Join(",", chunk.Select(_ => "?"))})", args);
            }
        });

    public void UpdateMessageMeta(long id, JsonObject? meta) =>
        db.Execute("UPDATE messages SET meta = @meta WHERE id = @id", new { meta = meta?.ToJsonString(), id });

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

    private static string RoleName(MessageRole role) => role switch
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

    private static ChatMessage ReadMessage(ISqlRow r)
    {
        var id = r.GetInt64("id");
        return new ChatMessage
        {
            Id = id,
            SessionId = r.GetString("session_id"),
            Seq = r.GetInt64("seq"),
            Role = ParseRole(r.GetString("role")),
            Parts = ReadStored<List<MessagePart>>(r.GetString("parts"), id) ?? [],
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
            Provider = r.GetStringOrNull("provider"),
            Model = r.GetStringOrNull("model"),
            StopReason = r.GetStringOrNull("stop_reason"),
            Usage = r.GetStringOrNull("usage") is { } u ? ReadStored<Usage>(u, id) : null,
            DurationMs = r.GetInt64OrNull("duration_ms"),
            Compacted = r.GetInt64("compacted") != 0,
            Meta = ParseMeta(r.GetStringOrNull("meta"), "message " + id.ToString(CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>
    /// A column of a stored row, back into the type it left as. A row that no longer parses is a data problem, not a
    /// bad request: as a <see cref="JsonException"/> it would reach the caller as "Invalid parameters" with nothing
    /// logged and the chat unreadable forever. Named after the row so the corruption is findable.
    /// </summary>
    private static T? ReadStored<T>(string json, long messageId)
    {
        try { return JsonSerializer.Deserialize<T>(json, NetPiJson.Options); }
        catch (JsonException ex) { throw new InvalidDataException($"Stored message {messageId} is corrupted and cannot be read: {ex.Message}", ex); }
    }

    private static SessionInfo ReadSession(ISqlRow r) => new()
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
        Pinned = r.GetInt64("pinned") != 0,
        MessageCount = r.GetInt64("message_count"),
        ContextTokens = r.GetInt64("context_tokens"),
        Meta = ParseMeta(r.GetStringOrNull("meta"), "session " + r.GetString("id")),
    };

    private static ProjectInfo ReadProject(ISqlRow r) => new()
    {
        Id = r.GetString("id"),
        Name = r.GetString("name"),
        Path = r.GetString("path"),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("updated_at")),
        LastUsedAt = r.GetInt64OrNull("last_used_at") is { } l ? DateTimeOffset.FromUnixTimeMilliseconds(l) : null,
        Meta = ParseMeta(r.GetStringOrNull("meta"), "project " + r.GetString("id")),
    };

    /// <summary>
    /// A row's meta column, back into the object it left as. Like <see cref="ReadStored{T}"/>: a column that no longer parses
    /// is a data error naming the row and the column, never "no meta" — read as null, the next update of the row would write
    /// the null back and the corruption would be gone with whatever the meta held.
    /// </summary>
    private static JsonObject? ParseMeta(string? json, string row)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException ex) { throw new InvalidDataException($"Stored {row} is corrupted and cannot be read: its meta is not JSON: {ex.Message}", ex); }
    }
}
