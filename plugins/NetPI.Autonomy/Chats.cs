using System.Text.Json.Nodes;

namespace NetPI.Autonomy;

/// <summary>Who a chat works for: <c>user</c>, <c>schedule:&lt;id&gt;</c> or <c>webhook:&lt;id&gt;</c>, and how the UI names it.</summary>
internal sealed record Origin(string Id, string Label)
{
    public static readonly Origin User = new("user", "your chats");
    public bool IsUser => Id == User.Id;
}

/// <summary>
/// What the plugin knows about a chat, read from what the plugins that start chats already write: a webhook's chat carries
/// <c>meta.webhook</c>/<c>webhookName</c>, a scheduled one <c>meta.schedule</c>, a subagent its parent (whose origin it
/// inherits, so spawning never raises trust). <b>Tainted</b>: the chat has read text an outsider wrote — a webhook's payload
/// from the start, or the result of an external tool (<c>meta.autonomy.tainted</c>, set by <see cref="AutonomyHook"/>); a
/// subagent of a tainted chat is tainted too.
/// </summary>
internal sealed class Chats(IPluginContext ctx)
{
    public const string MetaKey = "autonomy";
    private const int MaxDepth = 32;

    public SessionInfo? Get(string sessionId) => ctx.Sessions.GetSession(sessionId);

    public Origin OriginOf(string sessionId) => OriginOf(Get(sessionId));

    public Origin OriginOf(SessionInfo? session)
    {
        for (var depth = 0; session is not null && depth < MaxDepth; depth++)
        {
            if (Str(session.Meta, "webhook") is { } hook) return new Origin("webhook:" + hook, "webhook " + (Str(session.Meta, "webhookName") ?? hook));
            if (Str(session.Meta, "schedule") is { } schedule) return new Origin("schedule:" + schedule, "schedule " + ScheduleName(session, schedule));
            session = session.ParentSessionId is { Length: > 0 } parent ? Get(parent) : null;
        }
        return Origin.User;
    }

    /// <summary>Nobody watches this chat: it was started by a schedule or a webhook, or it is a subagent.</summary>
    public bool Unattended(SessionInfo? session) => session is not null && (session.Kind == "subagent" || !OriginOf(session).IsUser);

    public bool Tainted(SessionInfo? session)
    {
        for (var depth = 0; session is not null && depth < MaxDepth; depth++)
        {
            if (Str(session.Meta, "webhook") is not null) return true;
            if (session.Meta?[MetaKey] is JsonObject a && a["tainted"] is JsonValue v && v.TryGetValue<bool>(out var t) && t) return true;
            session = session.ParentSessionId is { Length: > 0 } parent ? Get(parent) : null;
        }
        return false;
    }

    /// <summary>The chat read outside text: kept on the chat (it stays tainted), with the tool that brought it.</summary>
    public void Taint(string sessionId, string tool)
    {
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            if (s.Meta[MetaKey] is not JsonObject a) s.Meta[MetaKey] = a = new JsonObject();
            a["tainted"] = true;
            a["taintedBy"] = tool;
        });
    }

    /// <summary>A scheduled chat's title is "Scheduled: &lt;name&gt;" (ScheduleRunner); the id when it is not.</summary>
    private static string ScheduleName(SessionInfo session, string id) =>
        session.Title.StartsWith("Scheduled: ", StringComparison.Ordinal) && session.Title.Length > 11 ? session.Title[11..] : id;

    private static string? Str(JsonObject? meta, string key) =>
        meta?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
