using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetPI.Context;

/// <summary>
/// Tells the model its working directory and project with small "project" notices instead of the system prompt: at the
/// session's first model call, right after a project switch (event <c>session.project</c>), and whenever the directory
/// no longer matches the last notice in the context (a project folder moved, the notice was compacted away). Notices are
/// only ever appended, so the conversation's cached prefix survives a switch.
/// <para>Its text names the project's folder; when the session is bound to a workspace, the workspace plugin's own notice
/// says which checkout that actually is, so the two do not have to be merged here.</para>
/// </summary>
internal sealed class ProjectNotices(IPluginContext ctx) : IAgentHook
{
    public const string Kind = "project";

    private readonly ConcurrentDictionary<string, object> _gates = new(StringComparer.Ordinal);

    /// <summary>After compaction (-100), which may compact the last notice away.</summary>
    public int Order => 500;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var run = turn.Run;
        if (!Needed(Last(turn.Messages), run.Cwd, run.Project?.Id)) return;
        if (Announce(run.Session.Id)) await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>A project switch: announce it right away, before the user's next message.</summary>
    public void OnProjectChanged(string sessionId)
    {
        try { Announce(sessionId); }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Project notice for {Session} failed", sessionId); }
    }

    /// <summary>Re-reads the session under a per-session lock (the event and the hook can race) and appends a notice if needed.</summary>
    internal bool Announce(string sessionId)
    {
        lock (_gates.GetOrAdd(sessionId, _ => new object()))
        {
            var session = ctx.Sessions.GetSession(sessionId);
            if (session is null) return false;
            var project = session.ProjectId is null ? null : ctx.Sessions.GetProject(session.ProjectId);
            var cwd = ctx.Sessions.GetCwd(session);
            var last = Last(ctx.Sessions.GetContextMessages(sessionId));
            if (!Needed(last, cwd, project?.Id)) return false;
            var notice = ChatMessage.NoticeText(Text(last, cwd, project), Kind);
            notice.Meta!["projectId"] = project?.Id;
            notice.Meta["cwd"] = cwd;
            notice.Meta["setup"] = true;  // the chat's setting: at the first turn the model reads it before the first message
            ctx.Sessions.AppendMessage(sessionId, notice);
            return true;
        }
    }

    internal static string Text(ChatMessage? last, string cwd, ProjectInfo? project)
    {
        if (last is null)
            return project is null
                ? $"Working directory: {cwd} (no project: the default workspace). Relative paths resolve against it."
                : $"Working directory: {cwd} (project \"{project.Name}\"). Relative paths resolve against it.";
        if (project is null) return $"The session left its project: the working directory is now the default workspace, {cwd}.";
        if (last.MetaString("projectId") == project.Id) return $"Project \"{project.Name}\" moved: the working directory is now {cwd}.";
        return $"The session moved to project \"{project.Name}\": the working directory is now {cwd}.";
    }

    private static ChatMessage? Last(IReadOnlyList<ChatMessage> context) =>
        context.LastOrDefault(m => m.Role == MessageRole.Notice && m.MetaString("kind") == Kind);

    private static bool Needed(ChatMessage? last, string cwd, string? projectId) =>
        last is null || last.MetaString("projectId") != projectId || !SamePath(last.MetaString("cwd"), cwd);

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        static string Norm(string p) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); } catch { return p; } }
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
