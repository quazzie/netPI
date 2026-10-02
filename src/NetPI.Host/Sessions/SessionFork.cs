using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Host.Sessions;

/// <summary>
/// What a fork of a chat takes along (<c>sessions.fork</c>): its setup (project, model, reasoning, and meta such as the
/// profile, its identity, the tools switched off), not where it is in its work. The meta keys it starts without are the
/// ones plugins declared as run state (<see cref="ISessionStore.DeclareForkReset"/>) and the core's own (lineage, the prompt
/// rule, and the folder: a fork is a new writer, so it starts in its project's folder). Plugins with state of their own
/// act on <c>session.forked</c> (the context plugin copies the system prompt in effect, the todo plugin the checklist).
/// </summary>
public static class SessionFork
{
    public static SessionInfo Template(SessionInfo from, long upToSeq, long contextTokens, IEnumerable<string> resetKeys, IReadOnlySet<string>? taken = null)
    {
        var meta = from.Meta?.DeepClone() as JsonObject ?? [];
        foreach (var key in resetKeys) meta.Remove(key);
        SessionPrompt.Fork(meta, upToSeq);
        meta["forkedFrom"] = new JsonObject { ["sessionId"] = from.Id, ["title"] = from.Title, ["seq"] = upToSeq };
        return new SessionInfo
        {
            Title = Title(from.Title, taken),
            ProjectId = from.ProjectId,
            Model = from.Model,
            Reasoning = from.Reasoning,
            Kind = "chat",
            ContextTokens = contextTokens,
            Meta = meta,
        };
    }

    /// <summary>"Title (fork)", then "Title (fork 2)"…: the first that no chat has (a fork of a fork counts on).</summary>
    public static string Title(string title, IReadOnlySet<string>? taken = null)
    {
        var name = BaseTitle(title);
        var first = name == title ? 1 : 2;
        for (var n = first; ; n++)
        {
            var candidate = n == 1 ? $"{name} (fork)" : $"{name} (fork {n})";
            if (taken is null || !taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>The title without its " (fork)" or " (fork N)".</summary>
    public static string BaseTitle(string title)
    {
        var m = Regex.Match(title ?? "", @"^(.*) \(fork(?: \d+)?\)$");
        return m.Success ? m.Groups[1].Value : title ?? "";
    }

    /// <summary>The context size at the fork point: the prompt and output of the last model call before it, 0 when unknown.</summary>
    public static long ContextTokens(IEnumerable<ChatMessage> messagesUpToFork) =>
        messagesUpToFork.LastOrDefault(m => m.Role == MessageRole.Assistant && m.Usage is not null)?.Usage!.ContextTokens ?? 0;
}
