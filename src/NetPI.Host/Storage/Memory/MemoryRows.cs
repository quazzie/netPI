using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Memory;

/// <summary>
/// The conversions every stored row shares: times as Unix milliseconds (the port stores millisecond precision) and
/// the JSON columns (a message's parts, usage and meta) as the text they are kept as, like the SQL columns they
/// stand for.
/// </summary>
internal static class Row
{
    public static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    public static DateTimeOffset Stamp(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static long? Ms(DateTimeOffset? at) => at?.ToUnixTimeMilliseconds();

    public static string? Json(JsonObject? node) => node?.ToJsonString();

    /// <summary>
    /// A meta column back into the object it left as. A column that no longer parses is a data error naming the row and
    /// the column (as the sqlite provider reads one), never "no meta": read as null, the next update of the row would
    /// write the null back and the corruption would be gone with whatever the meta held.
    /// </summary>
    public static JsonObject? Object(string? json, string row)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException ex) { throw new InvalidDataException($"Stored {row} is corrupted and cannot be read: its meta is not JSON: {ex.Message}", ex); }
    }

    /// <summary>What a message row is called in a data error: its id, like the sqlite provider names it.</summary>
    public static string Message(long id) => "message " + id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A project row. Immutable: a write replaces the record (see <see cref="MemoryStorage"/>'s undo log).</summary>
internal sealed record ProjectRow(string Id, string Name, string Path, long CreatedAt, long UpdatedAt, long? LastUsedAt, string? Meta)
{
    public static ProjectRow Of(ProjectInfo p) =>
        new(p.Id, p.Name, p.Path, Row.Ms(p.CreatedAt), Row.Ms(p.UpdatedAt), Row.Ms(p.LastUsedAt), Row.Json(p.Meta));

    public ProjectInfo ToInfo() => new()
    {
        Id = Id, Name = Name, Path = Path, CreatedAt = Row.Stamp(CreatedAt), UpdatedAt = Row.Stamp(UpdatedAt),
        LastUsedAt = LastUsedAt is { } l ? Row.Stamp(l) : null, Meta = Row.Object(Meta, "project " + Id),
    };
}

/// <summary>
/// A session row. No workspace: the port has no workspace methods, and a binding is <c>meta.workspaceId</c> on the
/// session (the column the kernel carried is gone with it).
/// </summary>
internal sealed record SessionRow(
    string Id, string Title, string? ProjectId, string? ParentSessionId, string Kind, string? Model, string? Reasoning,
    long CreatedAt, long UpdatedAt, bool Archived, bool Pinned, long MessageCount, long ContextTokens, string? Meta)
{
    public static SessionRow Of(SessionInfo s) => new(
        s.Id, s.Title, s.ProjectId, s.ParentSessionId, s.Kind, s.Model, s.Reasoning,
        Row.Ms(s.CreatedAt), Row.Ms(s.UpdatedAt), s.Archived, s.Pinned, s.MessageCount, s.ContextTokens, Row.Json(s.Meta));

    public SessionInfo ToInfo() => new()
    {
        Id = Id, Title = Title, ProjectId = ProjectId, ParentSessionId = ParentSessionId, Kind = Kind, Model = Model,
        Reasoning = Reasoning, CreatedAt = Row.Stamp(CreatedAt), UpdatedAt = Row.Stamp(UpdatedAt), Archived = Archived,
        Pinned = Pinned, MessageCount = MessageCount, ContextTokens = ContextTokens, Meta = Row.Object(Meta, "session " + Id),
    };
}

/// <summary>A message row: the port's <see cref="ChatMessage"/> flattened, with its JSON columns as text.</summary>
internal sealed record MessageRow(
    long Id, long Seq, string SessionId, MessageRole Role, string Parts, long CreatedAt, string? Provider, string? Model,
    string? StopReason, string? Usage, long? DurationMs, bool Compacted, string? Meta)
{
    public static MessageRow Of(ChatMessage m) => new(
        m.Id, m.Seq, m.SessionId, m.Role, JsonSerializer.Serialize(m.Parts ?? [], NetPiJson.Options), Row.Ms(m.CreatedAt),
        m.Provider, m.Model, m.StopReason, m.Usage is null ? null : JsonSerializer.Serialize(m.Usage, NetPiJson.Options),
        m.DurationMs, m.Compacted, Row.Json(m.Meta));

    public ChatMessage ToMessage() => new()
    {
        Id = Id, Seq = Seq, SessionId = SessionId, Role = Role,
        Parts = JsonSerializer.Deserialize<List<MessagePart>>(Parts, NetPiJson.Options) ?? [],
        CreatedAt = Row.Stamp(CreatedAt), Provider = Provider, Model = Model, StopReason = StopReason,
        Usage = Usage is null ? null : JsonSerializer.Deserialize<Usage>(Usage, NetPiJson.Options),
        DurationMs = DurationMs, Compacted = Compacted, Meta = Row.Object(Meta, Row.Message(Id)),
    };

    public MessageStub ToStub() => new(Id, Seq, Role, Compacted, Row.Object(Meta, Row.Message(Id)));
}
