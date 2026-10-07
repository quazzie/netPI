using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// The Claude CLI session behind a NetPI session: the CLI keeps its own conversation (what it read, ran and decided), so a later turn
/// of the NetPI session resumes it and sends only what is new. <see cref="LastSeenId"/> is the id of the newest NetPI message the CLI
/// had when it took the session up: everything after it (but the CLI's own answers) is new to it.
/// </summary>
internal sealed record CliSession(string CliId, long LastSeenId);

internal interface ICliSessions
{
    CliSession? Get(string sessionId);
    void Put(string sessionId, CliSession session);
    void Remove(string sessionId);
}

/// <summary>Kept in memory only (tests, and a provider built without a store).</summary>
internal sealed class MemoryCliSessions : ICliSessions
{
    private readonly ConcurrentDictionary<string, CliSession> _map = new();
    public CliSession? Get(string sessionId) => _map.GetValueOrDefault(sessionId);
    public void Put(string sessionId, CliSession session) => _map[sessionId] = session;
    public void Remove(string sessionId) => _map.TryRemove(sessionId, out _);
}

/// <summary>Kept in the plugin's own data, so a restart of NetPI resumes the same CLI sessions.</summary>
internal sealed class DataCliSessions(IDataCollection collection) : ICliSessions
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    public CliSession? Get(string sessionId)
    {
        if (collection.Get(sessionId) is not { } d || d.Str("cliId") is not { Length: > 0 } cliId) return null;
        return new CliSession(cliId, d["lastSeenId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0);
    }

    public void Put(string sessionId, CliSession session) => collection.Put(sessionId, new JsonObject
    {
        ["cliId"] = session.CliId,
        ["lastSeenId"] = session.LastSeenId,
        ["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });

    public void Remove(string sessionId) => collection.Delete(sessionId);

    /// <summary>Forgets the sessions nobody has continued for two months (the CLI keeps its own files; this is only NetPI's pointer).</summary>
    public void Prune() => collection.DeleteWhere(new DataQuery().Lt("updatedAt", DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeSeconds()));
}
