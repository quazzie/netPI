using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetPI.Workspaces;

/// <summary>
/// Tells the model which checkout it is working in, the same way the context plugin tells it the working directory —
/// as an appended "workspace" notice, never in the (frozen) system prompt: at the session's first model call, when the
/// binding changes (<c>session.workspace</c>), and whenever the resolved root no longer matches the last notice.
/// <para>
/// The notice names what an agent otherwise has to guess: that relative paths resolve in this root and not in the
/// project's, which branch it is on, and that it is its own checkout — so a commit it makes belongs to the project's
/// backlog but lands on this branch, not on the project's.
/// </para>
/// </summary>
internal sealed class WorkspaceNotices(IPluginContext ctx, WorkspaceResolver resolver) : IAgentHook
{
    public const string Kind = "workspace";

    /// <summary>After the project notice (500), which states the working directory; before the instructions (510).</summary>
    public int Order => 505;

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var run = turn.Run;
        if (!Needed(Last(turn.Messages), run.Workspace(), run.Cwd)) return;
        if (Announce(run.Session.Id)) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>A binding switch: announce it right away, before the next message is read.</summary>
    public void OnWorkspaceChanged(string sessionId)
    {
        try { Announce(sessionId); }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Workspace notice for {Session} failed", sessionId); }
    }

    /// <summary>Appends the notice for a session if its last one does not already describe the current binding.</summary>
    internal bool Announce(string sessionId)
    {
        lock (_gates.GetOrAdd(sessionId, _ => new object()))
        {
            var session = ctx.Sessions.GetSession(sessionId);
            if (session is null) return false;
            var cwd = ctx.Sessions.GetCwd(session);
            WorkspaceBinding? binding;
            string? problem = null;
            try { binding = resolver.Resolve(session); }
            catch (WorkspaceUnavailableException ex) { binding = null; problem = ex.Message; }
            var last = Last(ctx.Sessions.GetContextMessages(sessionId));
            if (!Needed(last, binding, cwd)) return false;
            if (problem is null && string.Equals(last?.MetaString("cwd"), cwd, StringComparison.Ordinal) && binding is null && last is not null)
                return false;
            var notice = ChatMessage.NoticeText(Text(last, binding, cwd, problem), Kind);
            notice.Meta!["workspaceId"] = binding?.WorkspaceId;
            notice.Meta["cwd"] = cwd;
            notice.Meta["branch"] = binding?.Branch;
            if (binding is not null)
            {
                notice.Meta["isolated"] = binding.Isolated;
                notice.Meta["ownerSessionId"] = binding.OwnerSessionId;
            }
            if (problem is not null) notice.Meta["error"] = problem;
            notice.Meta["setup"] = true;
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    /// <summary>What the notice says. Short: the transcript around it says what the work was.</summary>
    internal static string Text(ChatMessage? last, WorkspaceBinding? binding, string cwd, string? problem)
    {
        if (problem is not null)
            return $"This session's workspace cannot be used: {problem} Relative paths will not resolve against the project checkout — bind a workspace that works (sessions.setWorkspace) before writing anything.";
        if (binding is null)
        {
            if (last is not null && last.MetaString("workspaceId") is { Length: > 0 })
                return $"This session left its workspace: it works in the project's folder again ({cwd}). Relative paths resolve against it.";
            return "";   // never had one: the project notice already says where it works
        }
        var sb = new System.Text.StringBuilder();
        var first = last is null || last.MetaString("workspaceId") is not { Length: > 0 };
        sb.Append(first
            ? $"Working in workspace \"{binding.WorkspaceId}\": {binding.Describe()}. Relative paths resolve there, not in the project folder."
            : $"This session moved to workspace \"{binding.WorkspaceId}\": {binding.Describe()}.");
        if (binding.Branch is { Length: > 0 } branch)
        {
            sb.Append($" It works on branch {branch}");
            if (binding.Isolated) sb.Append(" of its own, so its commits do not land on the project's branch until they are merged");
            sb.Append('.');
        }
        if (binding.BaseCommit is { Length: > 0 } baseCommit)
            sb.Append($" It started from {baseCommit[..Math.Min(8, baseCommit.Length)]}, so uncommitted changes in the project folder are not part of it.");
        if (binding.Isolated)
            sb.Append(" Writes outside this workspace are refused when they point at another checkout of the same repository.");
        return sb.ToString();
    }

    private static ChatMessage? Last(IReadOnlyList<ChatMessage> context) =>
        context.LastOrDefault(m => m.Role == MessageRole.Notice && m.MetaString("kind") == Kind);

    private static bool Needed(ChatMessage? last, WorkspaceBinding? binding, string cwd)
    {
        if (last is null) return binding is not null;   // no workspace, nothing to say (the project notice covers it)
        if (!string.Equals(last.MetaString("cwd"), cwd, StringComparison.Ordinal)) return true;
        var said = last.MetaString("workspaceId");
        return !string.Equals(said, binding?.WorkspaceId, StringComparison.Ordinal);
    }
}