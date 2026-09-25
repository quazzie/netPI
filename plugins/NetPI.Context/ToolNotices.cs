using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// Tells the model when its tools change during a session (a plugin loaded, reloaded or disabled, <c>tools.disabled</c>, the
/// session's own switches <c>meta.toolsOff</c>).
/// Every request already carries the current tool definitions, but the frozen system prompt still has the guidelines of
/// the first call and nothing in the conversation says what changed, so a "tools" notice names the added and removed
/// tools and carries the new tools' guidelines. The baseline is the tool set of the session's first model call; each
/// notice records its change in meta (<c>added</c>, <c>removed</c>), so the known set is the baseline plus the notices still
/// in the context (a notice compacted away is announced again).
/// </summary>
internal sealed class ToolNotices(IPluginContext ctx, PromptStore store) : IAgentHook
{
    public const string Kind = "tools";

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    /// <summary>After compaction (-100), the project (500) and instruction (510) notices.</summary>
    public int Order => 520;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var sessionId = turn.Run.Session.Id;
        var names = Names(turn.Tools);
        var baseline = store.GetTools(sessionId);
        if (baseline is null)
        {
            store.FreezeTools(sessionId, names, turn.Messages.Count > 0 ? turn.Messages.Max(m => m.Seq) : 0);
            return;
        }
        var known = Known(baseline, turn.Messages);
        if (known.SetEquals(names)) return;
        if (Announce(sessionId, turn.Tools, baseline)) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>Appends a notice when the tools differ from what the context says, under a per-session lock.</summary>
    internal bool Announce(string sessionId, IReadOnlyList<ToolDefinition> tools, ToolBaseline baseline)
    {
        lock (_gates.GetOrAdd(sessionId, _ => new object()))
        {
            var known = Known(baseline, ctx.Sessions.GetContextMessages(sessionId));
            var names = Names(tools);
            var added = names.Where(n => !known.Contains(n)).ToList();
            var removed = known.Where(n => !names.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (added.Count == 0 && removed.Count == 0) return false;
            var notice = ChatMessage.NoticeText(Text(tools, added, removed, SessionTools.Off(ctx.Sessions.GetSession(sessionId))), Kind);
            notice.Meta!["added"] = new JsonArray(added.Select(n => (JsonNode?)n).ToArray());
            notice.Meta["removed"] = new JsonArray(removed.Select(n => (JsonNode?)n).ToArray());
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    /// <summary>The notice text; tools in <paramref name="switchedOff"/> (the session's own switches) are named as switched off by the user.</summary>
    internal static string Text(IReadOnlyList<ToolDefinition> tools, IReadOnlyList<string> added, IReadOnlyList<string> removed,
        IReadOnlySet<string>? switchedOff = null)
    {
        var sb = new StringBuilder("Your tools changed.");
        if (added.Count > 0) sb.Append(" New: ").Append(string.Join(", ", added)).Append('.');
        var byUser = removed.Where(n => switchedOff?.Contains(n) == true).ToList();
        var gone = removed.Where(n => switchedOff?.Contains(n) != true).ToList();
        if (byUser.Count > 0) sb.Append(" The user switched off for this session: ").Append(string.Join(", ", byUser)).Append('.');
        if (gone.Count > 0) sb.Append(" No longer available: ").Append(string.Join(", ", gone)).Append('.');
        // a line the new tools share with tools the model already has is in its context already (prompt or earlier notice)
        var had = ToolsSection.Guidelines(tools.Where(t => !added.Contains(t.Name))).ToHashSet(StringComparer.Ordinal);
        var guidelines = ToolsSection.Guidelines(tools.Where(t => added.Contains(t.Name))).Where(g => !had.Contains(g)).ToList();
        if (guidelines.Count > 0) sb.Append("\nGuidelines for the new tools:").Append(string.Concat(guidelines.Select(g => "\n- " + g)));
        return sb.ToString();
    }

    internal static HashSet<string> Known(ToolBaseline baseline, IReadOnlyList<ChatMessage> context)
    {
        var known = new HashSet<string>(baseline.Names, StringComparer.Ordinal);
        foreach (var m in context)
        {
            if (m.Role != MessageRole.Notice || m.MetaString("kind") != Kind) continue;
            if (m.Seq > 0 && m.Seq <= baseline.SinceSeq) continue; // before this baseline (a profile switch reset it)
            foreach (var n in m.Meta?["added"] as JsonArray ?? []) if (n?.GetValue<string>() is { } a) known.Add(a);
            foreach (var n in m.Meta?["removed"] as JsonArray ?? []) if (n?.GetValue<string>() is { } r) known.Remove(r);
        }
        return known;
    }

    private static List<string> Names(IReadOnlyList<ToolDefinition> tools) =>
        tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
}
