using System.Text.Json.Nodes;

namespace NetPI.Plan;

/// <summary>
/// A chat's plan mode, in its meta (<c>meta.planMode</c>: <c>{ state, planId?, title?, since, newSessionId? }</c>):
/// <c>planning</c> (exploring, no plan yet or one being revised), <c>awaiting</c> (a plan waits for the user's decision) and
/// <c>approved</c> (the mode is over; the plan was approved). Planning and awaiting are the active states: the hook blocks
/// everything that changes things. Run state of this plugin: a fork starts without it.
/// </summary>
internal static class PlanMode
{
    public const string MetaKey = "planMode";
    /// <summary>The chat a plan was moved from (<c>meta.planFrom</c>), on the new chat that carries it out.</summary>
    public const string FromKey = "planFrom";
    public const string Planning = "planning";
    public const string Awaiting = "awaiting";
    public const string Approved = "approved";

    public static string? State(SessionInfo? session) => session?.Meta?[MetaKey]?["state"]?.GetValue<string>();

    public static bool Active(SessionInfo? session) => State(session) is Planning or Awaiting;

    public static string? PlanId(SessionInfo? session) => session?.Meta?[MetaKey]?["planId"]?.GetValue<string>();

    /// <summary>Sets the state (null: the chat leaves plan mode). Keeps the plan id and title unless new ones are given.</summary>
    public static SessionInfo Set(ISessionStore sessions, string sessionId, string? state, string? planId = null, string? title = null, string? newSessionId = null)
    {
        return sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            if (state is null)
            {
                s.Meta.Remove(MetaKey);
                return;
            }
            var old = s.Meta[MetaKey] as JsonObject;
            var oldState = old?["state"]?.GetValue<string>();
            // the plan and title belong to one episode of plan mode: entering again after an approval starts without them
            var episode = oldState is Planning or Awaiting;
            s.Meta[MetaKey] = new JsonObject
            {
                ["state"] = state,
                ["planId"] = planId ?? (episode ? old?["planId"]?.GetValue<string>() : null),
                ["title"] = title ?? (episode ? old?["title"]?.GetValue<string>() : null),
                ["since"] = oldState == state && old?["since"]?.GetValue<string>() is { } since ? since : DateTimeOffset.UtcNow.ToString("O"),
                ["newSessionId"] = newSessionId,
            };
        });
    }
}
