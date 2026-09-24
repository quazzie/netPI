using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Profiles;

/// <summary>A profile from the settings: the opening of the system prompt and the tools switched off.</summary>
internal sealed record Profile(string Id, string Name, string? Prompt, IReadOnlyList<string> ToolsOff);

internal sealed class ProfileService(IPluginContext ctx)
{
    /// <summary>Settings key of the global default (not a profile).</summary>
    public const string DefaultKey = "defaultProfile";
    /// <summary>Session and project meta key; a project's <c>"none"</c> means no profile.</summary>
    public const string MetaKey = "profile";
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
            if (id == DefaultKey || id == None || node is not JsonObject o) continue;
            var off = o["toolsOff"] is JsonArray a
                ? a.Select(Str).OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            list.Add(new Profile(id, Str(o["name"]) is { Length: > 0 } name ? name.Trim() : id, Str(o["prompt"]) is { } p && p.Trim().Length > 0 ? p.Trim() : null, off));
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public Profile? Get(string? id) => string.IsNullOrWhiteSpace(id) ? null : All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The global default, if it names a profile that exists.</summary>
    public Profile? Default()
    {
        try { return Get(Str(ctx.Settings.GetNode("profiles." + DefaultKey))); } catch { return null; }
    }

    /// <summary>What a new chat starts with: its project's default (a profile, or "none"), else the global default.</summary>
    public Profile? DefaultFor(SessionInfo session)
    {
        var project = session.ProjectId is null ? null : ctx.Sessions.GetProject(session.ProjectId);
        if (Str(project?.Meta?[MetaKey]) is { Length: > 0 } chosen)
        {
            if (chosen == None) return null;
            if (Get(chosen) is { } p) return p;
        }
        return Default();
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
    public async Task<SessionInfo> ApplyAsync(string sessionId, string? profileId, CancellationToken ct)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
        if (session.Kind != "chat") throw new RpcException("bad_request", "Subagents don't use profiles: the agent that starts one chooses its tools.");
        var profile = profileId is null ? null : Get(profileId) ?? throw new RpcException("not_found", $"No profile \"{profileId}\"");
        SessionInfo updated;
        lock (_gate) updated = Write(sessionId, profile);
        if (session.MessageCount == 0) return updated;

        // started: the system prompt and the tools lead every request, so the next call is a full re-read either way
        try { await ctx.Rpc.InvokeAsync("context.reset", new { sessionId }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "context.reset failed for {Session}", sessionId); }
        ctx.Sessions.AppendMessage(sessionId, ChatMessage.NoticeText(profile is null
            ? "The user took this chat's profile away: your system prompt and tools are the default ones now."
            : $"The user switched this chat to the profile \"{profile.Name}\": your system prompt and tools have changed.", NoticeKind));
        return ctx.Sessions.GetSession(sessionId) ?? updated;
    }

    /// <summary>The chat's profile, the opening of its prompt and its tool switches, all from the profile.</summary>
    private SessionInfo Write(string sessionId, Profile? profile) =>
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            s.Meta[MetaKey] = profile?.Id;
            if (profile?.Prompt is { } prompt) s.Meta[SessionIdentity.MetaKey] = prompt;
            else s.Meta.Remove(SessionIdentity.MetaKey);
            if (profile is { ToolsOff.Count: > 0 }) s.Meta[SessionTools.MetaKey] = new JsonArray([.. profile.ToolsOff.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
            else s.Meta.Remove(SessionTools.MetaKey);
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
        })]),
    };

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
