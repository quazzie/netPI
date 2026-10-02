using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Memory;

/// <summary>
/// Projects, sessions and messages, in memory. Persistence only: what an append announces, what a fork copies, the
/// context cache and the title rules belong to the session service, which drives these primitives. Every read
/// hands back the caller's own copy, so nothing a caller does to what it got back can reach the store.
/// </summary>
internal sealed class MemorySessionRepository(MemoryStorage store) : ISessionRepository
{
    // ------------------------------------------------------------------ atomic

    public T Atomic<T>(Func<ISessionRepository, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return store.Atomic(() => work(this));
    }

    public void Atomic(Action<ISessionRepository> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        store.Atomic<object?>(() => { work(this); return null; });
    }

    // ------------------------------------------------------------------ projects

    public IReadOnlyList<ProjectInfo> ListProjects()
    {
        return store.Read<IReadOnlyList<ProjectInfo>>(() =>
            [
                .. store.Projects.Values
                    .OrderByDescending(p => p.LastUsedAt ?? p.UpdatedAt)
                    .ThenBy(p => p.Name, AsciiCase.Comparer)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => p.ToInfo())
            ]);
    }

    public ProjectInfo? GetProject(string id)
    {
        return store.Read(() => store.Projects.TryGetValue(id, out var row) ? row.ToInfo() : null);
    }

    public void InsertProject(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);
        store.Apply(() =>
        {
            if (store.Projects.ContainsKey(project.Id))
                throw new StorageException($"Project {project.Id} already exists");
            store.Touch(() => (ProjectRow?)null, was => { if (was is null) store.Projects.Remove(project.Id); else store.Projects[project.Id] = was; });
            store.Projects[project.Id] = ProjectRow.Of(project);
        });
    }

    public bool UpdateProject(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return store.Apply(() =>
        {
            if (!store.Projects.TryGetValue(project.Id, out var row)) return false;
            store.Touch(() => (ProjectRow?)store.Projects[project.Id], was => { if (was is not null) store.Projects[project.Id] = was; });
            store.Projects[project.Id] = row with
            {
                Name = project.Name,
                Path = project.Path,
                UpdatedAt = Row.Ms(project.UpdatedAt),
                Meta = Row.Json(project.Meta),
            };
            return true;
        });
    }

    public void TouchProject(string id, DateTimeOffset at)
    {
        store.Apply(() =>
        {
            if (!store.Projects.TryGetValue(id, out var row)) return;
            store.Touch(() => (ProjectRow?)store.Projects[id], was => { if (was is not null) store.Projects[id] = was; });
            store.Projects[id] = row with { LastUsedAt = Row.Ms(at) };
        });
    }

    public bool DeleteProject(string id)
    {
        return store.Apply(() =>
        {
            if (!store.Projects.TryGetValue(id, out var row)) return false;
            store.Touch(() => (ProjectRow?)store.Projects[id], was => { if (was is not null) store.Projects[id] = was; });
            store.Projects.Remove(id);
            return true;
        });
    }

    public int ClearProject(string projectId, DateTimeOffset at)
    {
        var stamp = Row.Ms(at);
        return store.Apply(() =>
        {
            var count = 0;
            foreach (var row in store.SessionRows.Values.Where(s => s.ProjectId == projectId).ToList())
            {
                store.Touch(() => (SessionRow?)store.SessionRows[row.Id], was => { if (was is not null) store.SessionRows[row.Id] = was; });
                store.SessionRows[row.Id] = row with { ProjectId = null, UpdatedAt = stamp };
                count++;
            }
            return count;
        });
    }

    // ------------------------------------------------------------------ sessions

    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<SessionRow> rows = store.SessionRows.Values;
        if (query.ProjectId is { } projectId)
            rows = projectId.Length == 0 ? rows.Where(s => s.ProjectId is null) : rows.Where(s => s.ProjectId == projectId);
        if (query.ParentSessionId is { } parent)
            rows = rows.Where(s => s.ParentSessionId == parent);
        else if (!query.IncludeSubagents)
            rows = rows.Where(s => s.Kind != "subagent");
        rows = query.ArchivedOnly ? rows.Where(s => s.Archived) : query.IncludeArchived ? rows : rows.Where(s => !s.Archived);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var needle = query.Search.Trim();
            rows = rows.Where(s => AsciiCase.Contains(s.Title, needle));
        }
        if (query.AttachedKey is { } key)
        {
            var value = query.AttachedValue;
            rows = rows.Where(s => Attached(s, key, value));
        }
        return store.Read<IReadOnlyList<SessionInfo>>(() =>
        [
            .. rows
                .OrderByDescending(s => s.Pinned)
                .ThenByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .Skip(Math.Max(0, query.Offset))
                .Take(Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 5000))
                .Select(s => s.ToInfo())
        ]);
    }

    /// <summary>The session's <c>meta[key]</c> is the string <paramref name="value"/>: the equality filter on one meta
    /// key the port calls <c>Attached</c>. A key that is not a string (or not there) does not match, a null
    /// <paramref name="value"/> matches nothing, and a meta column that does not parse matches nothing.</summary>
    private static bool Attached(SessionRow row, string key, string? value)
    {
        if (row.Meta is null) return false;
        JsonObject? meta;
        try { meta = JsonNode.Parse(row.Meta) as JsonObject; }
        catch (JsonException) { return false; }
        return meta is not null
            && meta.TryGetPropertyValue(key, out var node)
            && node is JsonValue v && v.TryGetValue<string>(out var text)
            && string.Equals(text, value, StringComparison.Ordinal);
    }

    public SessionInfo? GetSession(string id)
    {
        return store.Read(() => store.SessionRows.TryGetValue(id, out var row) ? row.ToInfo() : null);
    }

    public void InsertSession(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Id);
        store.Apply(() =>
        {
            if (store.SessionRows.ContainsKey(session.Id))
                throw new StorageException($"Session {session.Id} already exists");
            store.Touch(() => (SessionRow?)null, was => { if (was is null) store.SessionRows.Remove(session.Id); else store.SessionRows[session.Id] = was; });
            store.SessionRows[session.Id] = SessionRow.Of(session);
        });
    }

    public bool UpdateSession(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return store.Apply(() =>
        {
            if (!store.SessionRows.TryGetValue(session.Id, out var row)) return false;
            store.Touch(() => (SessionRow?)store.SessionRows[session.Id], was => { if (was is not null) store.SessionRows[session.Id] = was; });
            var updated = SessionRow.Of(session);
            // Id and CreatedAt are the row's own: an update writes everything else.
            store.SessionRows[session.Id] = updated with { Id = row.Id, CreatedAt = row.CreatedAt };
            return true;
        });
    }

    public IReadOnlyList<string> DeleteSessionTree(string id)
    {
        return store.Apply<IReadOnlyList<string>>(() =>
        {
            if (!store.SessionRows.ContainsKey(id)) return [];
            // Breadth first, so a parent comes before its children and a cycle cannot spin here.
            var tree = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current)) continue;
                tree.Add(current);
                foreach (var child in store.SessionRows.Values.Where(s => s.ParentSessionId == current).Select(s => s.Id))
                    queue.Enqueue(child);
            }
            foreach (var sid in tree)
            {
                store.Touch(() => (SessionRow?)store.SessionRows[sid], was => { if (was is not null) store.SessionRows[sid] = was; });
                store.SessionRows.Remove(sid);
                store.DropMessages(sid);
            }
            return tree;
        });
    }

    // ------------------------------------------------------------------ messages

    public ChatMessage AppendMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return store.Apply(() =>
        {
            if (!store.SessionRows.ContainsKey(message.SessionId))
                throw new KeyNotFoundException($"Session {message.SessionId} not found");
            var rows = store.MessagesOf(message.SessionId);
            var seq = rows.Count == 0 ? 1 : rows[^1].Seq + 1;
            store.TouchCounter(() => store.NextMessageId, was => store.NextMessageId = was);
            var stored = MessageRow.Of(message) with { Id = store.NextMessageId++, Seq = seq };
            message.Id = stored.Id;
            message.Seq = stored.Seq;
            store.StoreMessage(stored);
            return message;
        });
    }

    public void RecordAppend(string sessionId, DateTimeOffset at, string title)
    {
        var stamp = Row.Ms(at);
        store.Apply(() =>
        {
            if (!store.SessionRows.TryGetValue(sessionId, out var row)) return;
            store.Touch(() => (SessionRow?)store.SessionRows[sessionId], was => { if (was is not null) store.SessionRows[sessionId] = was; });
            store.SessionRows[sessionId] = row with { UpdatedAt = stamp, MessageCount = row.MessageCount + 1, Title = title };
        });
    }

    public bool UpdateMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return store.Apply(() =>
        {
            if (!store.MessageById.TryGetValue(message.Id, out var row)) return false;
            store.Touch(() => (MessageRow?)store.MessageById[message.Id], was => { if (was is null) store.MessageById.Remove(message.Id); else store.MessageById[message.Id] = was; });
            var updated = MessageRow.Of(message) with { Seq = row.Seq, SessionId = row.SessionId, CreatedAt = row.CreatedAt };
            store.StoreMessage(updated);
            return true;
        });
    }

    public ChatMessage? GetMessage(long id)
    {
        return store.Read(() => store.MessageById.TryGetValue(id, out var row) ? row.ToMessage() : null);
    }

    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null)
    {
        return store.Read<IReadOnlyList<ChatMessage>>(() =>
        {
            var rows = store.MessagesOf(sessionId);
            if (beforeSeq is null && limit is null) return [.. rows.Select(r => r.ToMessage())];
            // A page: the last <limit> messages, or the <limit> messages ending just before <beforeSeq> — taken
            // from the newest end, because that is the page a chat asks for, and handed back ascending like all.
            var page = rows.Where(r => beforeSeq is not { } before || r.Seq < before).ToList();
            if (limit is { } n && n > 0) page = page.Skip(Math.Max(0, page.Count - n)).ToList();
            return [.. page.Select(r => r.ToMessage())];
        });
    }

    public (IReadOnlyList<ChatMessage> Rows, long Newest) ReadContext(string sessionId)
    {
        return store.Read<(IReadOnlyList<ChatMessage> Rows, long Newest)>(() =>
        {
            var rows = store.MessagesOf(sessionId).Where(r => !r.Compacted).ToList();
            // The rows first and the newest seq after them, so Newest is never lower than the last row's seq: a
            // message that landed in between shows up as Newest > it, which the session service reads as "stale".
            var newest = 0L;
            foreach (var row in store.MessagesOf(sessionId))
                if (!row.Compacted && row.Seq > newest) newest = row.Seq;
            return ([.. rows.Select(r => r.ToMessage())], newest);
        });
    }

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        store.Apply(() =>
        {
            foreach (var row in store.MessagesOf(sessionId).Where(r => !r.Compacted && r.Seq <= upToSeq).ToList())
            {
                store.Touch(() => (MessageRow?)store.MessageById[row.Id], was => { if (was is null) store.MessageById.Remove(row.Id); else store.MessageById[row.Id] = was; });
                store.StoreMessage(row with { Compacted = true });
            }
        });
    }

    public int CopyMessages(string fromSessionId, string toSessionId, long upToSeq)
    {
        return store.Apply(() =>
        {
            // The target has to exist: a copy writes its messages under the target's id.
            if (!store.SessionRows.ContainsKey(toSessionId))
                throw new KeyNotFoundException($"Session {toSessionId} not found");
            var copies = store.MessagesUpTo(fromSessionId, upToSeq);
            if (copies.Count == 0) return 0;
            var taken = store.MessagesOf(toSessionId).Select(r => r.Seq).ToHashSet();
            var clash = copies.FirstOrDefault(r => taken.Contains(r.Seq));
            if (clash is not null)
                throw new StorageException($"Session {toSessionId} already has a message at seq {clash.Seq}");
            store.TouchCounter(() => store.NextMessageId, was => store.NextMessageId = was);
            foreach (var row in copies)
                // The same seq, time, parts, usage, flags and meta; a new id out of the store-wide counter.
                store.StoreMessage(row with { Id = store.NextMessageId++, SessionId = toSessionId });
            return copies.Count;
        });
    }

    public IReadOnlyList<MessageStub> MessageStubs(string sessionId)
    {
        return store.Read<IReadOnlyList<MessageStub>>(() => [.. store.MessagesOf(sessionId).Select(r => r.ToStub())]);
    }

    public void SetCompacted(string sessionId, IReadOnlySet<long> compactedSeqs)
    {
        ArgumentNullException.ThrowIfNull(compactedSeqs);
        store.Apply(() =>
        {
            foreach (var row in store.MessagesOf(sessionId))
            {
                var compacted = compactedSeqs.Contains(row.Seq);
                if (compacted == row.Compacted) continue;
                store.Touch(() => (MessageRow?)store.MessageById[row.Id], was => { if (was is null) store.MessageById.Remove(row.Id); else store.MessageById[row.Id] = was; });
                store.StoreMessage(row with { Compacted = compacted });
            }
        });
    }

    public void UpdateMessageMeta(long id, JsonObject? meta)
    {
        store.Apply(() =>
        {
            if (!store.MessageById.TryGetValue(id, out var row)) return;
            store.Touch(() => (MessageRow?)store.MessageById[id], was => { if (was is null) store.MessageById.Remove(id); else store.MessageById[id] = was; });
            store.StoreMessage(row with { Meta = Row.Json(meta) });
        });
    }
}
