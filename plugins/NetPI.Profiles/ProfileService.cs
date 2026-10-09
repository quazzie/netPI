using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Profiles;

/// <summary>
/// A profile from the settings: the opening of the system prompt, the tools switched off, and the tools kept out of
/// requests until used (<see cref="ToolExposure"/>; null: the setting <c>tools.deferred</c>).
/// </summary>
internal sealed record Profile(string Id, string Name, string? Prompt, IReadOnlyList<string> ToolsOff, IReadOnlyList<string>? ToolsDeferred = null);

internal sealed class ProfileService(IPluginContext ctx)
{
    /// <summary>Settings key of the global default (not a profile).</summary>
    public const string DefaultKey = "defaultProfile";
    /// <summary>Settings key of the profile for chats the app starts by itself (not a profile): a profile id, <c>"none"</c>,
    /// or unset for the global default.</summary>
    public const string AutoKey = "autoProfile";
    /// <summary>Session and project meta key; a project's <c>"none"</c> means no profile. The key is shared contract
    /// (<see cref="SessionProfile.MetaKey"/>), because <c>session.changed</c> reports it as data.</summary>
    public const string MetaKey = SessionProfile.MetaKey;
    public const string None = "none";
    public const string NoticeKind = "profile";

    private readonly Lock _gate = new();

    public IReadOnlyList<Profile> All()
    {
        var list = new List<Profile>();
        JsonNode? root;
        try { root = ctx.Settings.GetNode("profiles"); } catch { root = null; }
        if (root is not JsonObject profiles) return list;
        foreach (var (id, node) in profiles)
        {
            if (id == DefaultKey || id == AutoKey || id == None || node is not JsonObject o) continue;
            var off = o["toolsOff"] is JsonArray a
                ? a.Select(Str).OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            List<string>? deferred = o["toolsDeferred"] is JsonArray d
                ? [.. d.Select(Str).OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]
                : null;
            list.Add(new Profile(id, Str(o["name"]) is { Length: > 0 } name ? name.Trim() : id, Str(o["prompt"]) is { } p && p.Trim().Length > 0 ? p.Trim() : null, off, deferred));
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public Profile? Get(string? id) => string.IsNullOrWhiteSpace(id) ? null : All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The global default, if it names a profile that exists.</summary>
    public Profile? Default()
    {
        try { return Get(Str(ctx.Settings.GetNode("profiles." + DefaultKey))); } catch { return null; }
    }

    /// <summary>The profile of chats the app starts by itself: <c>profiles.autoProfile</c> (<c>"none"</c>: no profile), else the global default.</summary>
    public Profile? AutoDefault()
    {
        string? chosen;
        try { chosen = Str(ctx.Settings.GetNode("profiles." + AutoKey)); } catch { chosen = null; }
        if (chosen == None) return null;
        return Get(chosen) ?? Default();
    }

    /// <summary>
    /// What a new chat starts with: its project's default (a profile, or "none"), else the global default; for a chat the app
    /// starts by itself (<paramref name="auto"/>: a plan carried out, idea work, a schedule) the automatic profile takes the global default's place.
    /// </summary>
    public Profile? DefaultFor(SessionInfo session, bool auto = false)
    {
        var project = session.ProjectId is null ? null : ctx.Sessions.GetProject(session.ProjectId);
        if (Str(project?.Meta?[MetaKey]) is { Length: > 0 } chosen)
        {
            if (chosen == None) return null;
            if (Get(chosen) is { } p) return p;
        }
        return auto ? AutoDefault() : Default();
    }

    /// <summary>
    /// A chat that has not chosen yet gets its default profile: only before its first model call (at most its first
    /// message), so an older chat never changes behind its back. Subagent sessions don't use profiles.
    /// </summary>
    public void EnsureDefault(string sessionId)
    {
        lock (_gate)
        {
            var s = ctx.Sessions.GetSession(sessionId);
            if (s is null || s.Kind != "chat" || s.Meta?.ContainsKey(MetaKey) == true || s.MessageCount > 1) return;
            if (DefaultFor(s) is { } p) Write(sessionId, p);
        }
    }

    /// <summary>Give a chat a profile (null: none). In a started chat its prompt is rendered again at the next call.</summary>
    public Task<SessionInfo> ApplyAsync(string sessionId, string? profileId, CancellationToken ct)
    {
        var session = RequireChat(sessionId, ct);
        var profile = profileId is null ? null : Get(profileId) ?? throw new RpcException("not_found", $"No profile \"{profileId}\"");
        return ApplyCore(session, profile, null, ct);
    }

    /// <summary>
    /// Give a chat the app started by itself the automatic profile (its project's default first). <paramref name="alsoOff"/>:
    /// tools the starter keeps off whatever the profile says (idea refinement is read-only).
    /// </summary>
    public Task<SessionInfo> ApplyAutoAsync(string sessionId, IReadOnlyList<string>? alsoOff, CancellationToken ct)
    {
        var session = RequireChat(sessionId, ct);
        return ApplyCore(session, DefaultFor(session, auto: true), alsoOff, ct);
    }

    private SessionInfo RequireChat(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = ctx.Sessions.Require(sessionId);
        if (session.Kind != "chat") throw new RpcException("bad_request", "Subagents don't use profiles: the agent that starts one chooses its tools.");
        return session;
    }

    private Task<SessionInfo> ApplyCore(SessionInfo session, Profile? profile, IReadOnlyList<string>? alsoOff, CancellationToken ct)
    {
        var sessionId = session.Id;
        SessionInfo updated;
        lock (_gate) updated = Write(sessionId, profile, alsoOff);
        if (session.MessageCount == 0) return Task.FromResult(updated);

        // The durable revision works with Context absent, replaced, or supplied by another prompt builder.
        ct.ThrowIfCancellationRequested();
        ctx.Sessions.AppendMessage(sessionId, ChatMessage.NoticeText(profile is null
            ? "The user took this chat's profile away: your system prompt and tools are the default ones now."
            : $"The user switched this chat to the profile \"{profile.Name}\": your system prompt and tools have changed.", NoticeKind));
        return Task.FromResult(ctx.Sessions.GetSession(sessionId) ?? updated);
    }

    /// <summary>The chat's profile, the opening of its prompt, its tool switches and its deferred list, all from the profile.</summary>
    private SessionInfo Write(string sessionId, Profile? profile, IReadOnlyList<string>? alsoOff = null) =>
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            SessionPrompt.Invalidate(s);
            s.Meta[MetaKey] = profile?.Id;
            if (profile?.Prompt is { } prompt) s.Meta[SessionIdentity.MetaKey] = prompt;
            else s.Meta.Remove(SessionIdentity.MetaKey);
            var off = (profile?.ToolsOff ?? []).Concat(alsoOff ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (off.Count > 0) s.Meta[SessionTools.MetaKey] = new JsonArray([.. off.Select(n => (JsonNode?)n)]);
            else s.Meta.Remove(SessionTools.MetaKey);
            // the profile's deferred list, in the order written ("*" before its "!" exceptions reads best); none: the setting's
            if (profile?.ToolsDeferred is { } deferred) s.Meta[ToolExposure.MetaKey] = new JsonArray([.. deferred.Select(n => (JsonNode?)n)]);
            else s.Meta.Remove(ToolExposure.MetaKey);
        });

    public JsonObject ListJson() => new()
    {
        ["defaultProfile"] = Default()?.Id,
        ["profiles"] = new JsonArray([.. All().Select(p => (JsonNode?)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["prompt"] = p.Prompt,
            ["toolsOff"] = new JsonArray([.. p.ToolsOff.Select(n => (JsonNode?)n)]),
            ["toolsDeferred"] = p.ToolsDeferred is { } d ? new JsonArray([.. d.Select(n => (JsonNode?)n)]) : null,
        })]),
    };

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
