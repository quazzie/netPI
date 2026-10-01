using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>Durable explicit prompt invalidation; ordinary settings/model changes do not change a sent prefix.</summary>
public static class SessionPrompt
{
    public const string RevisionKey = "promptRevision";
    public const string FallbackKey = "runtimePrompt";
    public const string FallbackRevisionKey = "runtimePromptRevision";
    public const string ForkResetKey = "promptForkReset";
    private static long Number(JsonNode? node) => node is JsonValue v ? v.TryGetValue<long>(out var n) ? n : v.TryGetValue<int>(out var i) ? i : 0 : 0;
    public static long Revision(SessionInfo session) => Number(session.Meta?[RevisionKey]);
    public static void Invalidate(SessionInfo session)
    {
        var next = checked(Revision(session) + 1);
        session.Meta ??= new JsonObject();
        session.Meta[RevisionKey] = next;
        if (session.Meta["forkedFrom"] is not null) session.Meta[ForkResetKey] = true;
    }
    public static string? Fallback(SessionInfo session) => session.Meta?[FallbackRevisionKey] is JsonValue r
        && Number(r) == Revision(session)
        && session.Meta?[FallbackKey] is JsonValue p && p.TryGetValue<string>(out var prompt) ? prompt : null;
}
