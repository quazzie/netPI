using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Workspaces;

/// <summary>
/// "Retire when done": a worker cannot remove the workspace its own session is bound to (it is bound, and it runs), so it
/// asks for the removal and this class carries it out once nothing in the workspace runs any more. The request is a flag in
/// the workspace record's <c>meta.retireWhenDone</c> (<c>{ by, at, kept? }</c>; the record is this plugin's own data).
/// <para>When the last run of the sessions bound to a flagged workspace has ended (and none waits for a user's answer), those
/// sessions are unbound and the workspace is retired by the same rule as <c>workspaces.delete</c>: a dirty or unmerged one is
/// kept, the flag stays, the owner is told why (a notice in its chat) and <see cref="RetireEvent"/> says so.</para>
/// </summary>
internal sealed class WorkspaceRetirement(IPluginContext ctx, WorkspaceStore store, WorkspaceProvisioner provisioner)
{
    public const string MetaKey = "retireWhenDone";
    public const string NoticeKind = "workspace-retire";
    /// <summary><c>{ workspaceId, status: "requested"|"retired"|"kept", reason?, sessionId? }</c>.</summary>
    public const string RetireEvent = "workspace.retire";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>A directory is busy when a process the shell registry knows about is running in it.</summary>
    public static Func<string, bool> BusyIn(IPluginContext ctx) => path =>
    {
        try { return ctx.Services.Get<IWorkspaceProcesses>()?.IsBusyIn(path) == true; }
        catch { return false; }
    };

    public static bool IsFlagged(WorkspaceInfo w) => w.Meta?[MetaKey] is JsonObject;

    /// <summary>Flag a workspace. <paramref name="by"/> is the session that asked (its chat gets the notice when the removal is kept).</summary>
    public WorkspaceInfo Request(WorkspaceInfo workspace, string? by)
    {
        var flagged = store.UpdateWorkspace(workspace.Id, w =>
        {
            w.Meta ??= new JsonObject();
            w.Meta[MetaKey] = new JsonObject { ["by"] = by, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        });
        ctx.Events.Publish(RetireEvent, new { workspaceId = workspace.Id, status = "requested", sessionId = by });
        return flagged;
    }

    /// <summary>What would still keep the removal from happening, apart from the sessions that are about to end (<paramref name="ignoreSession"/>).</summary>
    public List<string> Problems(WorkspaceInfo workspace, string? ignoreSession)
    {
        HashSet<string>? ignore = ignoreSession is null ? null : [ignoreSession];
        var problems = provisioner.RetireProblems(workspace, BusyIn(ctx), ignore);
        // Another live session bound to it that is not the caller: reported by RetireProblems; a flag does not unbind others
        // silently unless every one of them is done, which TryRetireAsync decides.
        return problems;
    }

    /// <summary>The flagged workspaces a session's run end may have freed: the workspace the session is bound to.</summary>
    public async Task OnSessionIdleAsync(string sessionId)
    {
        var workspaceId = SessionWorkspace.Of(ctx.Sessions.GetSession(sessionId));
        if (workspaceId is null) return;
        var workspace = store.GetWorkspace(workspaceId);
        if (workspace is null || !IsFlagged(workspace)) return;
        await TryRetireAsync(workspaceId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Every flagged workspace once (the plugin starts after a run ended, or a restart happened).</summary>
    public async Task TryPendingAsync()
    {
        foreach (var w in store.ListWorkspaces().Where(IsFlagged).ToList())
        {
            try { await TryRetireAsync(w.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { ctx.Logger.LogWarning(ex, "Retiring the flagged workspace {Workspace} failed", w.Id); }
        }
    }

    /// <summary>
    /// Retire a flagged workspace when its sessions are done. Returns "retired", "waiting" (a bound session still runs or waits
    /// for the user) or "kept" (with the reason; the flag stays).
    /// </summary>
    public async Task<(string Status, string? Reason)> TryRetireAsync(string workspaceId, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(workspaceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var workspace = store.GetWorkspace(workspaceId);
            if (workspace is null) return ("retired", null);
            var bound = store.BoundSessions(workspaceId);
            var runtime = ctx.Services.Get<IAgentRuntime>();
            foreach (var sid in bound)
            {
                if (runtime?.GetBySession(sid) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded }) return ("waiting", null);
                if (runtime?.Waits(sid).Any(w => !w.Answered) == true) return ("waiting", null);
            }
            // Decided with the sessions as already unbound, so a refusal leaves their binding alone.
            var (ok, reason) = provisioner.CanRetire(workspace, BusyIn(ctx), bound.ToHashSet(StringComparer.Ordinal));
            if (!ok) return await KeepAsync(workspace, reason!).ConfigureAwait(false);
            foreach (var sid in bound)
            {
                try { store.SetSessionWorkspace(sid, null); }
                catch (KeyNotFoundException) { }
            }
            var (retired, error) = await provisioner.RetireAsync(workspace, BusyIn(ctx), ct).ConfigureAwait(false);
            if (!retired)
            {
                foreach (var sid in bound)
                {
                    try { store.SetSessionWorkspace(sid, workspaceId); }
                    catch (KeyNotFoundException) { }
                }
                return await KeepAsync(workspace, error ?? "the removal failed").ConfigureAwait(false);
            }
            ctx.Logger.LogInformation("Retired workspace {Name} ({Id}) after its work ended", workspace.Name, workspaceId);
            ctx.Events.Publish(RetireEvent, new { workspaceId, status = "retired", sessionId = FlagBy(workspace) });
            return ("retired", null);
        }
        finally { gate.Release(); }
    }

    private static string? FlagBy(WorkspaceInfo w) => (w.Meta?[MetaKey] as JsonObject)?["by"]?.GetValue<string>();

    /// <summary>Keep the workspace and flag: log it, announce it, and tell the owner once per distinct reason.</summary>
    private Task<(string, string?)> KeepAsync(WorkspaceInfo workspace, string reason)
    {
        var flag = workspace.Meta?[MetaKey] as JsonObject;
        var already = flag?["kept"]?.GetValue<string>() == reason;
        ctx.Logger.LogInformation("Workspace {Name} ({Id}) is kept although its retirement was requested: {Reason}", workspace.Name, workspace.Id, reason);
        var by = FlagBy(workspace);
        store.UpdateWorkspace(workspace.Id, w =>
        {
            if (w.Meta?[MetaKey] is JsonObject f) f["kept"] = reason;
        });
        ctx.Events.Publish(RetireEvent, new { workspaceId = workspace.Id, status = "kept", reason, sessionId = by });
        if (!already)
        {
            var owner = by is { Length: > 0 } && ctx.Sessions.GetSession(by) is not null ? by : workspace.OwnerSessionId;
            if (owner is { Length: > 0 } && ctx.Sessions.GetSession(owner) is not null)
            {
                var notice = ChatMessage.NoticeText(
                    $"Workspace {workspace.Name} was not removed although its retirement was requested: {reason} " +
                    "The request stays: it is tried again when a run ends; fix the cause (and call workspace with action retire again to be told what is left).",
                    NoticeKind);
                notice.Meta!["workspaceId"] = workspace.Id;
                try { ctx.Sessions.AppendMessage(owner, notice); }
                catch (Exception ex) { ctx.Logger.LogWarning(ex, "Notice about the kept workspace {Workspace} failed", workspace.Id); }
            }
        }
        return Task.FromResult(("kept", (string?)reason));
    }
}
