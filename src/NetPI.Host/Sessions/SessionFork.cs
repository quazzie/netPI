using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Host.Sessions;

/// <summary>
/// What a fork of a chat takes along (<c>sessions.fork</c>): its setup (project, model, reasoning, and meta such as the
/// profile, its identity, the tools switched off, the agent), not where it is in its work. Plugins with state of their
/// own act on <c>session.forked</c> (the context plugin copies the system prompt in effect, the todo plugin the checklist).
/// </summary>
public static class SessionFork
{
    /// <summary>
    /// Meta a fork starts without: a goal would run on by itself, the checklist is the original's latest (the todo plugin
    /// sets it from the copy), the budget allowance is a decision per chat, the rest belongs to subagents.
    /// </summary>
    public static readonly IReadOnlyList<string> RunState = ["goal", "todo", "budgetAllowedFrom", "agentId", "parentAgentId", "agentInstructions", "forkedFrom"];

    public static SessionInfo Template(SessionInfo from, long upToSeq, long contextTokens, IReadOnlySet<string>? taken = null)
    {
        var meta = from.Meta?.DeepClone() as JsonObject ?? [];
        foreach (var key in RunState) meta.Remove(key);
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
