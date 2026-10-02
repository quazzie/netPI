using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Skills;

/// <summary>
/// Before each model call: (1) the skill catalog in a "skills" notice, only while the chat has the skill tool (switched
/// like any tool: per chat, <c>tools.disabled</c>, a subagent's tool list), appended only (the conversation's cached
/// prefix survives): every listed skill when a session first calls the model with the tool, afterwards only what changed.
/// What the model has is read from the notices still in the context, so after compaction it is announced again, with the
/// skills it had loaded before: the loads are recorded in <c>meta.loadedSkills</c> as they happen (the tool's result, a
/// /skill: notice), and the announcement reads that instead of the session's whole history. Catalog meta: <c>skills: [{ name,
/// hash, path }]</c>, <c>removed: [name]</c>. (2) A user message that starts
/// with <c>/skill:name</c> gets a "skill" notice with that skill's instructions right after it. Meta: <c>skill</c>,
/// <c>hash</c>, <c>path</c>, <c>for</c> (the message id); <c>missing: true</c> when there is no such skill.
/// </summary>
internal sealed class SkillNotices(IPluginContext ctx, SkillLoader loader) : IAgentHook
{
    public const string CatalogKind = "skills";
    public const string SkillKind = "skill";
    public const string Command = "/skill:";

    /// <summary>The session's loaded skill names: <c>meta.loadedSkills</c>, appended as a skill is loaded.</summary>
    public const string LoadedMetaKey = "loadedSkills";

    /// <summary>One gate per session, released with the session (a deleted session loads no skills).</summary>
    private readonly SessionState<object> _gates = new(ctx.Events);

    /// <summary>After the working-directory notice (500) and the instruction files (510).</summary>
    public int Order => 520;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var appended = false;
        lock (_gates.GetOrAdd(turn.Run.Session.Id, static _ => new object()))
        {
            var set = loader.Discover(turn.Run.Cwd);
            if (turn.Tools.Any(t => t.Name == SkillTool.Name) && Catalog(turn.Run.Session, turn.Messages, set) is { } catalog)
            {
                ctx.Sessions.AppendMessage(turn.Run.Session.Id, catalog);
                appended = true;
            }
            foreach (var notice in Invocations(turn.Messages, set))
            {
                ctx.Sessions.AppendMessage(turn.Run.Session.Id, notice);
                if (notice.Meta?["missing"] is null) RecordLoaded(ctx, turn.Run.Session.Id, notice.MetaString("skill") ?? "");
                appended = true;
            }
        }
        if (appended) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- catalog

    private ChatMessage? Catalog(SessionInfo session, IReadOnlyList<ChatMessage> context, SkillSet set)
    {
        var known = Known(context);
        var current = set.Listed.ToList();
        var changed = current.Where(s => !known.TryGetValue(s.Name, out var h) || h != s.Hash).ToList();
        var removed = known.Keys.Where(k => !current.Any(s => string.Equals(s.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (changed.Count == 0 && removed.Count == 0) return null;

        var first = known.Count == 0;
        var sb = new StringBuilder();
        if (first)
        {
            sb.Append("Skills: instructions for specific tasks. When a task matches a skill's description, load it with the skill tool " +
                      "before you start and follow it.\n\n");
            sb.Append(List(changed));
            var lost = LoadedBefore(session, context).Where(n => current.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (lost.Count > 0)
                sb.Append("\n\nBefore the conversation was compacted you had loaded: ").Append(string.Join(", ", lost))
                  .Append(". Load a skill again if you still need its instructions.");
        }
        else
        {
            sb.Append("The skills changed.");
            if (changed.Count > 0) sb.Append(" New or changed:\n\n").Append(List(changed));
            if (removed.Count > 0) sb.Append(changed.Count > 0 ? "\n\n" : " ").Append("No longer available: ").Append(string.Join(", ", removed)).Append('.');
        }

        var notice = ChatMessage.NoticeText(sb.ToString(), CatalogKind);
        notice.Meta!["skills"] = new JsonArray([.. changed.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["hash"] = s.Hash, ["path"] = s.Path })]);
        if (removed.Count > 0) notice.Meta["removed"] = new JsonArray([.. removed.Select(r => (JsonNode)JsonValue.Create(r))]);
        notice.Meta["setup"] = true;  // the chat's setting: at the first turn the model reads it before the first message
        return notice;
    }

    internal static string List(IEnumerable<Skill> skills)
    {
        var sb = new StringBuilder("<available_skills>\n");
        foreach (var s in skills)
        {
            sb.Append("  <skill>\n    <name>").Append(SecurityElement.Escape(s.Name)).Append("</name>\n");
            sb.Append("    <description>").Append(SecurityElement.Escape(s.Description)).Append("</description>\n  </skill>\n");
        }
        return sb.Append("</available_skills>").ToString();
    }

    /// <summary>Name → hash of the catalog the model has: the "skills" notices still in the context, in order.</summary>
    private static Dictionary<string, string> Known(IReadOnlyList<ChatMessage> context)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in context)
        {
            if (m.Role != MessageRole.Notice || m.MetaString("kind") != CatalogKind) continue;
            if (m.Meta?["skills"] is JsonArray skills)
                foreach (var s in skills.OfType<JsonObject>())
                    if (s["name"]?.GetValue<string>() is { } name && s["hash"]?.GetValue<string>() is { } hash) known[name] = hash;
            if (m.Meta?["removed"] is JsonArray removed)
                foreach (var r in removed)
                    if (r?.GetValue<string>() is { } name) known.Remove(name);
        }
        return known;
    }

    /// <summary>
    /// The skills loaded earlier in the session whose instructions are no longer in the context (compacted away): the
    /// session's meta (constant), minus what the context still holds. A session that was never recorded (created before
    /// the key existed) has none.
    /// </summary>
    private List<string> LoadedBefore(SessionInfo? session, IReadOnlyList<ChatMessage> context)
    {
        var inContext = Loaded(context);
        return LoadedFromMeta(session).Where(n => !inContext.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The skill names in the session's <c>meta.loadedSkills</c>, in load order.</summary>
    internal static List<string> LoadedFromMeta(SessionInfo? session)
    {
        var names = new List<string>();
        if (session?.Meta?[LoadedMetaKey] is JsonArray list)
            foreach (var n in list)
                if (n is JsonValue v && v.TryGetValue<string>(out var name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
        return names;
    }

    /// <summary>
    /// Record that a skill's instructions are in the session (a tool result, a /skill: notice). Names stay unique; a
    /// session that goes away meanwhile is simply not recorded.
    /// </summary>
    public static void RecordLoaded(IPluginContext ctx, string sessionId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            ctx.Sessions.UpdateSession(sessionId, s =>
            {
                var meta = s.Meta ??= new JsonObject();
                var list = meta[LoadedMetaKey] as JsonArray ?? [];
                meta[LoadedMetaKey] = list;
                if (list.Any(n => n is JsonValue v && v.TryGetValue<string>(out var x)
                    && string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(JsonValue.Create(name));
            });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) { } // deleted meanwhile
    }

    /// <summary>The names of the skills whose instructions these messages hold (skill tool results and "skill" notices), in order.</summary>
    internal static List<string> Loaded(IEnumerable<ChatMessage> messages)
    {
        var names = new List<string>();
        void Add(string? n)
        {
            if (!string.IsNullOrEmpty(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        foreach (var m in messages)
        {
            if (m.Role == MessageRole.Notice && m.MetaString("kind") == SkillKind && m.Meta?["missing"] is null) Add(m.MetaString("skill"));
            else if (m.Role == MessageRole.Tool)
                foreach (var r in m.ToolResults)
                    if (r.Name == SkillTool.Name && !r.IsError && r.Details is JsonObject d && d["name"] is JsonValue v && v.TryGetValue<string>(out var n)) Add(n);
        }
        return names;
    }

    // ---------------------------------------------------------------- /skill:name

    /// <summary>A notice for each user message since the model last answered that starts with /skill:name and has none yet.</summary>
    private static List<ChatMessage> Invocations(IReadOnlyList<ChatMessage> context, SkillSet set)
    {
        var start = 0;
        for (var i = context.Count - 1; i >= 0; i--)
            if (context[i].Role == MessageRole.Assistant) { start = i + 1; break; }
        var handled = context.Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == SkillKind)
            .Select(m => m.Meta?["for"]?.ToString()).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        var notices = new List<ChatMessage>();
        for (var i = start; i < context.Count; i++)
        {
            var m = context[i];
            if (m.Role != MessageRole.User || Parse(m.Text) is not { } name || handled.Contains(m.Id.ToString())) continue;
            notices.Add(Invocation(m, name, set));
        }
        return notices;
    }

    /// <summary>The skill name of "/skill:name …", or null.</summary>
    internal static string? Parse(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith(Command, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = t[Command.Length..];
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        return end == 0 ? null : rest[..end];
    }

    private static ChatMessage Invocation(ChatMessage message, string name, SkillSet set)
    {
        var skill = set.Find(name);
        var loaded = skill is { Disabled: false } ? SkillContent.Load(skill) : null;
        ChatMessage notice;
        if (skill is null || loaded is null)
        {
            var names = set.Skills.Where(s => !s.Disabled).Select(s => s.Name).ToList();
            var why = skill is { Disabled: true } ? "it is switched off in the settings"
                : skill is not null ? $"its SKILL.md can't be read ({skill.Path})"
                : "there is no such skill here";
            notice = ChatMessage.NoticeText($"The user asked for the skill \"{name}\", but {why}. " +
                (names.Count > 0 ? "The skills: " + string.Join(", ", names) + "." : "No skills are available here."), SkillKind);
            notice.Meta!["missing"] = true;
            notice.Meta["skill"] = skill?.Name ?? name;
        }
        else
        {
            notice = ChatMessage.NoticeText($"The user loaded the skill \"{skill.Name}\" for their message: follow its instructions.\n\n{loaded.Text}", SkillKind);
            notice.Meta!["skill"] = skill.Name;
            notice.Meta["hash"] = loaded.Hash;
            notice.Meta["path"] = skill.Path;
        }
        notice.Meta["for"] = message.Id.ToString();
        return notice;
    }
}
