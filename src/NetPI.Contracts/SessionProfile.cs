using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// The profile a session runs with: <c>meta.profile</c>, written by the profiles plugin (and by a project's default for a new
/// chat). The key lives in this contract, not in that plugin, because <c>session.changed</c> reports meta keys as data: a consumer of
/// the event must be able to name the key without knowing which plugin wrote it (idea-m7vmue).
/// </summary>
public static class SessionProfile
{
    public const string MetaKey = "profile";
}
