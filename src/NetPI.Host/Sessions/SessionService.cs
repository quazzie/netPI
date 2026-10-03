using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Sessions;

/// <summary>
/// The session service: projects, sessions and messages over a storage provider's <see cref="ISessionRepository"/>. It owns
/// the behaviour, the provider owns persistence. Publishes <c>project.*</c> / <c>session.*</c> (broadcast) and
/// <c>message.*</c> / <c>messages.compacted</c> (session scoped) events. A session with no messages is transient: it lives in
/// memory until its first message materializes it, so an abandoned empty chat leaves nothing.
/// </summary>
internal sealed class SessionService : ISessionStore
{
    public const string DefaultTitle = "New session";
    private const int MaxTitleLength = 60;

    /// <summary>Where the remembered fork-reset keys live in the key-value store (a JSON array of key names).</summary>
    private const string ForkResetKeysValue = "fork.resetKeys";

    /// <summary>The meta keys that belong to the core's own rules for a fork: its lineage and prompt rule, and the folder (a fork starts in its project folder).</summary>
    private static readonly string[] CoreForkResetKeys = ["forkedFrom", SessionPrompt.ForkResetKey, SessionCwd.MetaKey];

    private readonly ISessionRepository _repo;
    private readonly IKeyValueStore _values;
    private readonly IEventBus _bus;
    private readonly string _defaultWorkspace;

    /// <summary>Sessions with no messages: in memory only, until the first message materializes them (AppendMessage).</summary>
    private readonly Dictionary<string, SessionInfo> _transient = new();

    /// <summary>
    /// An abandoned empty chat is dropped once this many exist, oldest first. They are UI scratchpads — a "new
    /// chat" that never got its first message — and an abandoned one never materializes, so keeping them forever
    /// is a leak, not a memory.
    /// </summary>
    private const int MaxTransientSessions = 100;

    /// <summary>
    /// Guards <see cref="_transient"/>, and it is the STORE'S lock (<see cref="IStorage.Lock"/>). It used to be a lock of its own, and that is
    /// a deadlock: the first message of a session took it and then the store's lock (to store the session), while any transaction —
    /// which holds the store's lock — reads a session, and reading one looks the transient sessions up under it. A thread in each order
    /// waits for the other for good, and every storage call of the process with it. One lock cannot be taken in two orders. It is re-entrant.
    /// </summary>
    private readonly object _transientLock;

    /// <summary>The deserialized contexts of the sessions being worked on: the few most recent, kept warm across appends.</summary>
    private readonly ContextCache _context = new();

    private readonly HashSet<string> _forkReset = new(StringComparer.Ordinal);
    private readonly object _forkResetLock = new();

    /// <summary>Times the cached context answered a read; the rest were read from storage. Diagnostics and tests.</summary>
    internal (long Hits, long Reads) ContextCache => _context.Counters;

    public SessionService(IStorage storage, IEventBus bus, string defaultWorkspace)
    {
        _repo = storage.Sessions;
        _values = storage.Values;
        _transientLock = storage.Lock;
        _bus = bus;
        _defaultWorkspace = defaultWorkspace;
        if (_values.Get(ForkResetKeysValue) is { } json && ParseObjectArray(json) is { } remembered)
            foreach (var key in remembered) _forkReset.Add(key);
    }

    // ------------------------------------------------------------------ projects

    public IReadOnlyList<ProjectInfo> ListProjects() => _repo.ListProjects();

    public ProjectInfo? GetProject(string id) => _repo.GetProject(id);

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
        _repo.InsertProject(project);
        Publish(EventTypes.ProjectCreated, new { project });
        return project;
    }

    public ProjectInfo UpdateProject(string id, string? name, string? path) => UpdateProject(id, name, path, null);

    /// <summary>Update a project; <paramref name="meta"/> is merged key by key (a null value removes the key).</summary>
    public ProjectInfo UpdateProject(string id, string? name, string? path, JsonObject? meta)
    {
        var project = _repo.Atomic(r =>
        {
            var p = r.GetProject(id) ?? throw new KeyNotFoundException($"Project {id} not found");
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
            r.UpdateProject(p);
            return p;
        });
        Publish(EventTypes.ProjectUpdated, new { project });
        return project;
    }

    public void DeleteProject(string id)
    {
        var (affected, now) = _repo.Atomic(r =>
        {
            if (r.GetProject(id) is null) throw new KeyNotFoundException($"Project {id} not found");
            var detached = new List<SessionInfo>();
            for (var page = r.ListSessions(new SessionQuery { ProjectId = id, IncludeSubagents = true, IncludeArchived = true, Limit = 5000 });
                 page.Count > 0;
                 page = page.Count < 5000 ? [] : r.ListSessions(new SessionQuery { ProjectId = id, IncludeSubagents = true, IncludeArchived = true, Limit = 5000, Offset = detached.Count }))
                detached.AddRange(page);
            var stamp = Now();
            r.ClearProject(id, stamp);
            r.DeleteProject(id);
            return (detached, stamp);
        });
        // Transient (no-message) sessions have no row: detach them in memory, like the ones above
        List<SessionInfo> detachedTransient = [];
        lock (_transientLock)
        {
            var t = _transient.Values.Where(s => s.ProjectId == id).ToList();
            if (t.Count > 0)
            {
                foreach (var s in t) { s.ProjectId = null; s.UpdatedAt = now; }
                detachedTransient = t;
            }
        }
        Publish(EventTypes.ProjectDeleted, new { id });
        // The sessions were read whole above, before they were cleared, so the copies carry what is now stale: say what the
        // store says now, or the UI keeps showing the deleted project.
        foreach (var s in affected)
        {
            s.ProjectId = null;
            s.UpdatedAt = now;
            Publish(EventTypes.SessionUpdated, new { session = s });
        }
        foreach (var s in detachedTransient) Publish(EventTypes.SessionUpdated, new { session = s });
    }

    // ------------------------------------------------------------------ sessions

    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stored = _repo.ListSessions(query);
        if (!query.IncludeUnmaterialized) return stored;
        List<SessionInfo> matches;
        lock (_transientLock)
            matches = _transient.Values.Where(s => Matches(s, query)).Select(CopySession).ToList();
        return matches.Count == 0 ? stored : [.. stored, .. matches];
    }

    /// <summary>The same filters the repository applies, for an in-memory session (paging and order are not applied to these).</summary>
    private static bool Matches(SessionInfo s, SessionQuery q)
    {
        if (q.ProjectId is not null && (q.ProjectId.Length == 0 ? s.ProjectId is not null : s.ProjectId != q.ProjectId)) return false;
        if (q.ParentSessionId is not null) { if (s.ParentSessionId != q.ParentSessionId) return false; }
        else if (!q.IncludeSubagents && s.Kind == "subagent") return false;
        if (q.ArchivedOnly) { if (!s.Archived) return false; }
        else if (!q.IncludeArchived && s.Archived) return false;
        if (!string.IsNullOrWhiteSpace(q.Search) && !s.Title.Contains(q.Search.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (q.AttachedKey is { Length: > 0 } key
            && !(q.AttachedValue is not null && s.Meta?[key] is JsonValue v && v.TryGetValue<string>(out var text) && text == q.AttachedValue)) return false;
        return true;
    }

    public SessionInfo? GetSession(string id) => TransientSession(id) ?? _repo.GetSession(id);

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
        Pinned = s.Pinned, MessageCount = s.MessageCount, ContextTokens = s.ContextTokens, Meta = s.Meta?.DeepClone() as JsonObject,
    };

    public SessionInfo CreateSession(SessionInfo template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var now = Now();
        var s = NewSession(template, now);
        s.Archived = template.Archived;
        s.Pinned = template.Pinned;
        if (s.ProjectId is not null && _repo.GetProject(s.ProjectId) is null) throw new KeyNotFoundException($"Project {s.ProjectId} not found");
        // No row, no session.created, no project last_used_at: the first message materializes the session (AppendMessage).
        lock (_transientLock)
        {
            _transient[s.Id] = s;
            EvictTransientLocked();
        }
        return s;
    }

    private static SessionInfo NewSession(SessionInfo template, DateTimeOffset now) => new()
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
        MessageCount = 0,
        ContextTokens = template.ContextTokens,
        Meta = template.Meta?.DeepClone() as JsonObject,
    };

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
        // mutate runs inside the store's lock (the same lock as every other write): do not call another plugin's storage from it.
        var session = _repo.Atomic(r =>
        {
            var s = r.GetSession(id) ?? throw new KeyNotFoundException($"Session {id} not found");
            // a snapshot, not the object: mutate() edits the very same JsonObject in place
            wasMeta = SessionMeta.Snapshot(s.Meta);
            mutate(s);
            s.Id = id;
            s.UpdatedAt = Now();
            if (string.IsNullOrWhiteSpace(s.Kind)) s.Kind = "chat";
            r.UpdateSession(s);
            return s;
        });
        Publish(EventTypes.SessionUpdated, new { session });
        PublishMetaChanged(id, wasMeta!, session.Meta);
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
                ForgetContext(id);
                Publish(EventTypes.SessionDeleted, new { id });
                return;
            }
        }
        var deleted = _repo.DeleteSessionTree(id);
        if (deleted.Count == 0) throw new KeyNotFoundException($"Session {id} not found");
        // Children first so a UI never sees an orphaned child of a deleted parent.
        for (var i = deleted.Count - 1; i >= 0; i--) Publish(EventTypes.SessionDeleted, new { id = deleted[i] });
        foreach (var sid in deleted) ForgetContext(sid);
    }

    /// <summary>
    /// Attach (or detach) a project. The core only stores it and publishes <see cref="EventTypes.SessionProject"/>; what the
    /// model is told is up to plugins: the conversation is only ever appended to.
    /// </summary>
    public SessionInfo SetSessionProject(string sessionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) projectId = null;
        lock (_transientLock)
        {
            if (_transient.TryGetValue(sessionId, out var t))
            {
                if (t.ProjectId == projectId) return CopySession(t);
                if (projectId is not null && _repo.GetProject(projectId) is null) throw new KeyNotFoundException($"Project {projectId} not found");
                t.ProjectId = projectId;
                t.UpdatedAt = Now();
                return CopySession(t); // no row, no session.project: nothing has been announced yet
            }
        }
        var (session, changed) = _repo.Atomic(r =>
        {
            var s = r.GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
            if (s.ProjectId == projectId) return (s, false);
            if (projectId is not null)
            {
                if (r.GetProject(projectId) is null) throw new KeyNotFoundException($"Project {projectId} not found");
                r.TouchProject(projectId, Now());
            }
            s.ProjectId = projectId;
            s.UpdatedAt = Now();
            r.UpdateSession(s);
            return (s, true);
        });
        if (changed)
        {
            Publish(EventTypes.SessionUpdated, new { session });
            Publish(EventTypes.SessionProject, new { sessionId, projectId, cwd = GetCwd(session) });
        }
        return session;
    }

    /// <summary>
    /// The folder a session runs in: the one it carries (<see cref="SessionCwd.MetaKey"/>, set by whoever decided it, such as a
    /// bound checkout), else its project's folder, else the default working folder. Reads the session's own data only.
    /// </summary>
    public string GetCwd(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (SessionCwd.Of(session) is { } own) return own;
        if (session.ProjectId is not null && _repo.GetProject(session.ProjectId) is { } p && !string.IsNullOrWhiteSpace(p.Path)) return p.Path;
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
        var (appended, session) = _repo.Atomic(r => AppendCore(r, sessionId, message));
        _context.Append(appended);   // committed: the cached context can take it (inside the transaction it might roll back)
        PublishMessage(EventTypes.MessageAdded, appended);
        Publish(EventTypes.SessionUpdated, new { session });
        return appended;
    }

    /// <summary>
    /// The first message of a session materializes it: the session (and the project's last_used_at) plus the message in one
    /// atomic step, then session.created (it now has a message, as <c>sessions.list</c> shows it), message.added and
    /// session.updated. Runs under the transient gate, so a racing append for the same session waits and then takes the stored path.
    /// </summary>
    private ChatMessage MaterializeFirstMessage(string sessionId, SessionInfo transient, ChatMessage message)
    {
        lock (_transientLock)
        {
            var (stored, session) = _repo.Atomic(r =>
            {
                if (transient.ProjectId is not null)
                {
                    if (r.GetProject(transient.ProjectId) is null) throw new KeyNotFoundException($"Project {transient.ProjectId} not found");
                    r.TouchProject(transient.ProjectId, Now());
                }
                r.InsertSession(transient);
                return AppendCore(r, sessionId, message);
            });
            _transient.Remove(sessionId);   // after the commit: a failed first message leaves the session as it was
            Publish(EventTypes.SessionCreated, new { session });
            PublishMessage(EventTypes.MessageAdded, stored);
            Publish(EventTypes.SessionUpdated, new { session });
            _context.Append(stored);   // committed: a fresh session has no cached context yet, so this is usually a no-op
            return stored;
        }
    }

    /// <summary>Store the message (inside an atomic step), bump the counters and auto-title. Returns the stored message and the session as it is now.</summary>
    private static (ChatMessage Message, SessionInfo Session) AppendCore(ISessionRepository r, string sessionId, ChatMessage message)
    {
        var current = r.GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found");
        message.SessionId = sessionId;
        message.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt.ToUnixTimeMilliseconds());
        r.AppendMessage(message);

        var title = current.Title;
        if (message.Role == MessageRole.User && (string.IsNullOrWhiteSpace(title) || title == DefaultTitle) && MakeTitle(message.Text) is { } auto)
            title = auto;
        var now = Now();
        r.RecordAppend(sessionId, now, title);
        current.UpdatedAt = now;
        current.MessageCount++;
        current.Title = title;
        return (message, current);
    }

    public void UpdateMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // The drop goes AFTER the write. With it before, a reader that starts between the drop and the commit re-fills
        // the slot with the old row — the generation it captured is only bumped by the drop, and the seq check cannot
        // see a content change at the same seq. Written this way, a reader that started before the write captured its
        // generation before the bump and must not fill after it, and one that fills in between the commit and the drop
        // is removed by the drop itself.
        if (!_repo.UpdateMessage(message)) throw new KeyNotFoundException($"Message {message.Id} not found");
        DropContext(message.SessionId);
        if (string.IsNullOrEmpty(message.SessionId) || message.Seq == 0)
        {
            var stored = _repo.GetMessage(message.Id)!;
            message.SessionId = stored.SessionId;
            message.Seq = stored.Seq;
        }
        PublishMessage(EventTypes.MessageUpdated, message);
    }

    public ChatMessage? GetMessage(long id) => _repo.GetMessage(id);

    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null) =>
        _repo.GetMessages(sessionId, beforeSeq, limit);

    public IReadOnlyList<ChatMessage> GetMessagesAfter(string sessionId, long afterSeq, int limit) =>
        _repo.GetMessagesAfter(sessionId, afterSeq, Math.Max(1, limit));

    public IReadOnlyList<ChatMessage> GetContextMessages(string sessionId)
    {
        // A warm read is just the copy of the cached context (before this, a turn re-read the whole history and
        // re-parsed its JSON — ~30 ms and megabytes of garbage for a 1,500-message chat, every turn). The read that
        // fills the cache happens OUTSIDE the cache lock, so one session's cold read does not hold up another
        // session's warm read or any append.
        return _context.TryGet(sessionId) is { } rows ? Ordered(rows) : Ordered(ContextRows(sessionId));
    }

    /// <summary>The caller's own copy, with the latest summary first (it is appended after the retained tail).</summary>
    private static List<ChatMessage> Ordered(IReadOnlyList<ChatMessage> rows)
    {
        var list = new List<ChatMessage>(rows);
        var idx = list.FindLastIndex(m => m.Role == MessageRole.Summary);
        if (idx > 0)
        {
            var summary = list[idx];
            list.RemoveAt(idx);
            list.Insert(0, summary);
        }
        return list;
    }

    /// <summary>
    /// The session's uncompacted messages in storage order: the read the cache could not answer. It begins (and so
    /// captures the session's generation for) a cold read, and puts the result in the cache when the cache may hold
    /// it — the judgement is the cache's, against the newest seq the store reported in the same read.
    /// </summary>
    private List<ChatMessage> ContextRows(string sessionId)
    {
        var generation = _context.BeginRead(sessionId);
        var (read, newest) = _repo.ReadContext(sessionId);
        var rows = read.ToList();
        _context.Fill(sessionId, rows, newest, generation);
        return rows;
    }

    /// <summary>
    /// The caller holds <see cref="_transientLock"/>: an abandoned empty chat is dropped (oldest first) once the
    /// cap is passed. Nothing is lost that a restart would not already have lost (a transient session has no row),
    /// and <c>session.deleted</c> keeps a UI's list from ghosting the dropped chat.
    /// </summary>
    private void EvictTransientLocked()
    {
        while (_transient.Count > MaxTransientSessions)
        {
            var oldest = _transient.Values.OrderBy(s => s.UpdatedAt).First();
            if (_transient.Remove(oldest.Id)) Publish(EventTypes.SessionDeleted, new { id = oldest.Id });
        }
    }

    /// <summary>Forget a session's cached context. Every write that changes a message calls it.</summary>
    private void DropContext(string sessionId) => _context.Drop(sessionId);

    /// <summary>A session that is going away: forget its cached context and let its generation go with it.</summary>
    private void ForgetContext(string sessionId) => _context.Forget(sessionId);

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        _repo.MarkCompacted(sessionId, upToSeq);
        DropContext(sessionId);
        _bus.Publish(new BusEvent
        {
            Type = EventTypes.MessagesCompacted, SessionId = sessionId, Source = "host",
            Data = new { sessionId, upToSeq },
        });
    }

    // ------------------------------------------------------------------ fork

    /// <summary>Plugins name the meta keys that are run state (a goal, a checklist, an allowance…): a fork starts without them. Remembered across restarts.</summary>
    public void DeclareForkReset(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // Written inside the lock: two declarations racing must not leave the smaller set stored last.
        // Remembered even if the plugin is absent at the next fork: the key is dropped whether or not its owner is loaded.
        lock (_forkResetLock)
        {
            var added = false;
            foreach (var key in keys)
                if (!string.IsNullOrWhiteSpace(key)) added |= _forkReset.Add(key.Trim());
            if (added) _values.Set(ForkResetKeysValue, new JsonArray(_forkReset.Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()).ToJsonString());
        }
    }

    /// <summary>The meta keys a fork drops: the core's own and every key a plugin has ever declared.</summary>
    internal IReadOnlyList<string> ForkResetKeys()
    {
        lock (_forkResetLock) return [.. CoreForkResetKeys, .. _forkReset];
    }

    /// <summary>
    /// A new session with a copy of the session's messages up to <paramref name="upToSeq"/>, in one atomic step: the same
    /// seqs (so seq references such as a summary's <c>coversUpToSeq</c> stay valid), times, parts, usage and meta. What is
    /// compacted is what was compacted at that point (<see cref="CompactionAsOfEnd"/>), and a copied <c>meta.for</c> that
    /// names a message id names its copy. <c>session.created</c> goes out once the copy is complete (a handler never sees
    /// it half done), then <c>session.forked</c>.
    /// </summary>
    public SessionInfo ForkSession(string sessionId, long upToSeq, SessionInfo template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var fork = NewSession(template, Now());
        if (GetSession(sessionId) is null) throw new KeyNotFoundException($"Session {sessionId} not found");
        if (!_repo.MessageStubs(sessionId).Any(m => m.Seq <= upToSeq))
        {
            // Nothing to copy: the fork stays transient, like a fresh empty chat — no row, no session.created, no session.forked.
            lock (_transientLock)
            {
                _transient[fork.Id] = fork;
                EvictTransientLocked();
            }
            return fork;
        }
        _repo.Atomic(r =>
        {
            if (fork.ProjectId is not null)
            {
                if (r.GetProject(fork.ProjectId) is null) throw new KeyNotFoundException($"Project {fork.ProjectId} not found");
                r.TouchProject(fork.ProjectId, fork.CreatedAt);
            }
            r.InsertSession(fork);
            fork.MessageCount = r.CopyMessages(sessionId, fork.Id, upToSeq);
            CompactionAsOfEnd(r, fork.Id);
            MapMessageIds(r, sessionId, fork.Id);
            r.UpdateSession(fork);
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
    private static void CompactionAsOfEnd(ISessionRepository r, string sessionId)
    {
        var stubs = r.MessageStubs(sessionId);
        var latest = stubs.LastOrDefault(m => m.Role == MessageRole.Summary);
        if (latest is null)
        {
            r.SetCompacted(sessionId, new HashSet<long>());
            return;
        }
        long? covers = latest.Meta?["coversUpToSeq"] is JsonValue v && v.TryGetValue<long>(out var c) ? c : null;
        var compacted = covers is { } upTo
            ? stubs.Where(m => m.Seq <= upTo || (m.Role == MessageRole.Summary && m.Seq < latest.Seq))
            : stubs.Where(m => m.Seq < latest.Seq && m.Compacted);
        r.SetCompacted(sessionId, compacted.Select(m => m.Seq).ToHashSet());
    }

    /// <summary>A copied message whose <c>meta.for</c> names a message of the original (a skill notice's user message) names the copy.</summary>
    private static void MapMessageIds(ISessionRepository r, string from, string to)
    {
        var copies = r.MessageStubs(to);
        var refs = copies.Where(m => m.Meta?["for"] is not null).ToList();
        if (refs.Count == 0) return;
        var seqOf = r.MessageStubs(from).ToDictionary(m => m.Id, m => m.Seq);
        var idAt = copies.ToDictionary(m => m.Seq, m => m.Id);
        foreach (var m in refs)
        {
            var meta = m.Meta!;
            if (meta["for"] is not JsonValue v || !long.TryParse(v.ToString(), out var old)) continue;
            if (!seqOf.TryGetValue(old, out var seq) || !idAt.TryGetValue(seq, out var copy)) continue;
            meta["for"] = copy.ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.UpdateMessageMeta(m.Id, meta);
        }
    }

    // ------------------------------------------------------------------ key/value (ui.state)

    public string? GetValue(string key) => _values.Get(key);

    public void SetValue(string key, string? value) => _values.Set(key, value);

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<string>? ParseObjectArray(string json)
    {
        try { return JsonNode.Parse(json) is JsonArray a ? a.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() : null; }
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
