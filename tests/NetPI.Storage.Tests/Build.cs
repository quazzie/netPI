using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Storage.Tests;

/// <summary>The fixtures the scenarios are written with: a row, a message, a document.</summary>
public static class Build
{
    /// <summary>A fixed clock, so nothing in a test depends on how fast it runs.</summary>
    public static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    public static ProjectInfo Project(string id, Action<ProjectInfo>? setup = null)
    {
        var p = new ProjectInfo { Id = id, Name = id, Path = "/p/" + id, CreatedAt = At(0), UpdatedAt = At(0) };
        setup?.Invoke(p);
        return p;
    }

    public static SessionInfo Session(string id, Action<SessionInfo>? setup = null)
    {
        var s = new SessionInfo { Id = id, Title = id, Kind = "chat", CreatedAt = At(0), UpdatedAt = At(0) };
        setup?.Invoke(s);
        return s;
    }

    public static ChatMessage Message(string sessionId, string text = "hello", Action<ChatMessage>? setup = null)
    {
        var m = ChatMessage.UserText(text);
        m.SessionId = sessionId;
        m.CreatedAt = At(0);
        setup?.Invoke(m);
        return m;
    }

    /// <summary>Append a message and return the stored one (the port sets the id and the seq on it).</summary>
    public static ChatMessage Append(IStorage store, string sessionId, string text = "hello", Action<ChatMessage>? setup = null) =>
        store.Sessions.AppendMessage(Message(sessionId, text, setup));

    /// <summary>Insert a session, so a test can talk about one without materializing it first.</summary>
    public static SessionInfo WithMessages(IStorage store, string sessionId, int count)
    {
        var session = Session(sessionId);
        store.Sessions.InsertSession(session);
        for (var i = 0; i < count; i++) Append(store, sessionId, "m" + (i + 1));
        return session;
    }

    /// <summary>A document of field name and value (a string, a long, a double, a bool, or a node).</summary>
    public static JsonObject Doc(params (string Key, object? Value)[] fields)
    {
        var doc = new JsonObject();
        foreach (var (key, value) in fields) doc[key] = Node(value);
        return doc;
    }

    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value, value.GetType()),
    };

    public static List<string> Ids(IEnumerable<SessionInfo> sessions) => [.. sessions.Select(s => s.Id)];

    public static List<string> Keys(IEnumerable<DataDoc> docs) => [.. docs.Select(d => d.Key)];

    public static List<long> Seqs(IEnumerable<ChatMessage> messages) => [.. messages.Select(m => m.Seq)];

    /// <summary>Ids as one string: the assertions compare order, and a list would compare by reference.</summary>
    public static string Of(IEnumerable<long> values) => string.Join(",", values);
}
