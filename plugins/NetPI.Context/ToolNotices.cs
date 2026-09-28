using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// Tells the model when its tools change during a session (a plugin loaded, reloaded or disabled, <c>tools.disabled</c>, the
/// session's own switches <c>meta.toolsOff</c>).
/// Every request already carries the current tool definitions, but the frozen system prompt still has the guidelines of
/// the first call and nothing in the conversation says what changed, so a "tools" notice names the added and removed
/// tools, says why (<see cref="ToolChanges"/>, kept in the notice's meta) and carries the new tools' guidelines. The
/// baseline is the tool set of the session's first model call; each notice records its change in meta (<c>added</c>,
/// <c>removed</c>, <c>cause</c>, <c>plugins</c>), so the known set is the baseline plus the notices still in the context
/// (a notice compacted away is announced again) and <c>context.toolsets</c> can replay the history.
/// </summary>
internal sealed class ToolNotices(IPluginContext ctx, PromptStore store) : IAgentHook
{
    public const string Kind = "tools";

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);
    private readonly ToolChanges _changes = new(ctx);

    /// <summary>After compaction (-100), the project (500) and instruction (510) notices.</summary>
    public int Order => 520;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var sessionId = turn.Run.Session.Id;
        var names = Names(turn.Tools);
        _changes.Remember(sessionId, turn.Tools);  // before the diff: a tool that came back with its plugin
        // the session.changed this call owns (a profile switch since the last call), taken whatever happens next: a change
        // that turns out to explain nothing must not be blamed for a later one
        var changed = _changes.TakePending(sessionId);
        var baseline = store.GetTools(sessionId);
        if (baseline is null)
        {
            store.FreezeTools(sessionId, names, turn.Messages.Count > 0 ? turn.Messages.Max(m => m.Seq) : 0);
            return;
        }
        var known = Known(baseline, turn.Messages);
        if (known.SetEquals(names)) return;
        if (Announce(sessionId, turn.Tools, baseline, changed)) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>Appends a notice when the tools differ from what the context says, under a per-session lock.</summary>
    internal bool Announce(string sessionId, IReadOnlyList<ToolDefinition> tools, ToolBaseline baseline, ToolChanges.MetaChange? changed = null)
    {
        lock (_gates.GetOrAdd(sessionId, _ => new object()))
        {
            var context = ctx.Sessions.GetContextMessages(sessionId);
            var known = Known(baseline, context);
            var names = Names(tools);
            var added = names.Where(n => !known.Contains(n)).ToList();
            var removed = known.Where(n => !names.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (added.Count == 0 && removed.Count == 0) return false;
            var session = ctx.Sessions.GetSession(sessionId);
            var off = SessionTools.Off(session);
            var cause = _changes.Cause(sessionId, added, removed, off, context, changed);
            var notice = ChatMessage.NoticeText(Text(tools, added, removed, off, cause), Kind);
            notice.Meta!["added"] = new JsonArray(added.Select(n => (JsonNode?)n).ToArray());
            notice.Meta["removed"] = new JsonArray(removed.Select(n => (JsonNode?)n).ToArray());
            notice.Meta["cause"] = cause.Cause;
            if (cause.Plugins.Count > 0) notice.Meta["plugins"] = new JsonArray(cause.Plugins.Select(p => (JsonNode?)p).ToArray());
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    /// <summary>The reloads behind the tool changes seen lately (for <c>context.toolsets</c>).</summary>
    internal IReadOnlyList<ToolChanges.Reload> Reloads(TimeSpan? window = null) => _changes.Reloads(window);

    /// <summary>Receives <c>plugins.reloaded</c>: the cause of the next tool-set change.</summary>
    internal void OnPluginsReloaded(BusEvent e) => _changes.OnPluginsReloaded(e);

    /// <summary>Receives <c>session.changed</c>: the meta keys the user changed (the profile, the tools off for this chat).</summary>
    internal void OnSessionChanged(BusEvent e) => _changes.OnSessionChanged(e);

    /// <summary>A deleted session: nothing left to remember its tools for.</summary>
    internal void Forget(string sessionId) => _changes.Forget(sessionId);

    /// <summary>
    /// The notice text: tools in <paramref name="switchedOff"/> (the session's own switches) are named as switched off by
    /// the user, the rest get the cause as one clause ("plugin reload netpi.tools.web") when there is one to name.
    /// </summary>
    internal static string Text(IReadOnlyList<ToolDefinition> tools, IReadOnlyList<string> added, IReadOnlyList<string> removed,
        IReadOnlySet<string>? switchedOff = null, ToolChanges.Change? cause = null)
    {
        var sb = new StringBuilder("Your tools changed.");
        if (added.Count > 0) sb.Append(" New: ").Append(string.Join(", ", added)).Append('.');
        var byUser = removed.Where(n => switchedOff?.Contains(n) == true).ToList();
        var gone = removed.Where(n => switchedOff?.Contains(n) != true).ToList();
        if (byUser.Count > 0) sb.Append(" The user switched off for this session: ").Append(string.Join(", ", byUser)).Append('.');
        if (gone.Count > 0)
        {
            sb.Append(" No longer available: ").Append(string.Join(", ", gone));
            if (cause is { Clause.Length: > 0 } c && c.Cause != ToolChanges.User) sb.Append(" (").Append(c.Clause).Append(')');
            sb.Append('.');
        }
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
