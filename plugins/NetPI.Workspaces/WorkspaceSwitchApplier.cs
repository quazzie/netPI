using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Workspaces;

/// <summary>
/// Applies a workspace switch that was requested while tools were running, at the boundary between model calls.
/// <para>
/// A switch asked for mid-batch (<c>workspace action switch</c>, or a binding changed from the UI) must not take effect
/// inside that batch: a tool call already resolved its path against the old root, and a <c>write</c> that is about to
/// run would land in the tree the switch just left. So the request is parked, and this hook takes it before the next
/// model call — after the batch's tools have all finished, so nothing is holding a path resolved against the old root.
/// </para>
/// <para>
/// It is deliberately the only place that binds during a run. Everything else (the UI's own switch, a spawn's
/// provisioning) writes the reference directly and publishes <c>session.workspace</c>; this one defers, and says in the
/// notice that the switch happened, so the model and the UI learn the same root at the same moment.
/// </para>
/// </summary>
internal sealed class WorkspaceSwitchApplier(IPluginContext ctx, WorkspaceResolver resolver) : IAgentHook
{
    /// <summary>Before everything: the root a turn resolves against must be settled before any hook reads it.</summary>
    public int Order => -500;

    /// <summary>Parked switches, keyed by session: the workspace id, or null for "back to the project folder".</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Pending = new(StringComparer.Ordinal);

    /// <summary>Ask for a switch that takes effect at the next safe boundary.</summary>
    public static void Request(string sessionId, string? workspaceId) => Pending[sessionId] = workspaceId;

    /// <summary>What is parked for a session, for a display that wants to say so.</summary>
    public static string? PendingFor(string sessionId) => Pending.TryGetValue(sessionId, out var v) ? v : null;

    public static void Clear(string sessionId) => Pending.TryRemove(sessionId, out _);

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var sessionId = turn.Run.Session.Id;
        if (!Pending.TryRemove(sessionId, out var workspaceId)) return;   // nothing parked: the common path
        try
        {
            Apply(sessionId, workspaceId);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Workspace switch for {Session} failed", sessionId);
            // The switch is dropped and the run continues on the root it already had, which is the one its tools have
            // been resolving against: a failed switch must not strand the turn between two roots.
        }
        await turn.ReloadMessagesAsync().ConfigureAwait(false);
    }

    /// <summary>Bind now (the safe boundary has passed), or throw with the reason.</summary>
    public void Apply(string sessionId, string? workspaceId)
    {
        var store = ctx.Services.Get<IWorkspaceStore>() ?? throw new InvalidOperationException("The workspace store is not available.");
        if (workspaceId is null)
        {
            store.SetSessionWorkspace(sessionId, null);
            resolver.ForgetAll();
            return;
        }
        var target = store.GetWorkspace(workspaceId) ?? throw new WorkspaceUnavailableException(
            $"Workspace {workspaceId} no longer exists; the session keeps the workspace it was in.");
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
        resolver.Validate(session, target);   // missing / other repository: refused, not silently swapped
        store.SetSessionWorkspace(sessionId, workspaceId);
        resolver.Forget(workspaceId);
    }
}