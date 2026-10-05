using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Coordinator;

/// <summary>
/// What an outside coordinator (another agent harness driving NetPI workers through the RPC/CLI) needs beyond the
/// single calls: one-call dispatch (<c>sessions.dispatch</c>) and a run's result (<c>runs.result</c>). Built from the
/// other plugins' RPC methods only, so it owns no state and changes nothing a worker may do; it removes the
/// coordinator's glue: a six-call dispatch validated late (a wrong agent key failed after the session was made and bound)
/// and transcripts parsed by hand for the final report. See idea-equiw4 and docs/PROTOCOL.md.
/// </summary>
[NetPiPlugin("netpi.coordinator", Name = "Coordinator", Description = "One-call dispatch and run results for external coordinators", Order = 90)]
public sealed class CoordinatorPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var c = new Coordinator(context);
        context.Rpc.Register("sessions.dispatch", (req, token) => c.DispatchAsync(req, token),
            "Start a worker in one call, validated first: { project, title?, workspace?: name | id | { name|id, create?, base? }, agent?, profile?, " +
            "text? | textFile?, send? } → { sessionId, title, project, workspace, agent, profile, sent, status }. Nothing is left behind when a step fails. " +
            "{{workspacePath}}, {{branch}}, {{workspaceName}}, {{sessionId}}, {{projectPath}} in the text are filled in");
        context.Rpc.RegisterReadOnly("runs.result", (req, token) => c.ResultAsync(req, token),
            "A chat's last run in one answer: { sessionId | runId } → { sessionId, title, status, startedAt, finishedAt, durationMs, turns, toolCalls, " +
            "error, queued, report (the run's last assistant text), lastTool, todo: { done, total, current, items }, commits (made on its workspace since the run started), workspace }");
        return Task.CompletedTask;
    }
}

internal sealed class Coordinator(IPluginContext ctx)
{
    /// <summary>The largest brief a dispatch reads from <c>textFile</c>.</summary>
    public const int MaxTextFileBytes = 1024 * 1024;

    private async Task<JsonNode?> Call(string method, object parameters, CancellationToken ct) =>
        NetPiJson.ToNode(await ctx.Rpc.InvokeAsync(method, parameters, ct).ConfigureAwait(false));

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Flag(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // ------------------------------------------------------------------ sessions.dispatch

    public async Task<object?> DispatchAsync(RpcRequest req, CancellationToken ct)
    {
        // ---- read and validate everything before anything is made
        var text = req.Str("text");
        if (text is null && req.Str("textFile") is { Length: > 0 } file)
        {
            if (!Path.IsPathRooted(file)) throw new RpcException("bad_request", $"textFile must be an absolute path: {file}");
            if (!File.Exists(file)) throw new RpcException("bad_request", $"textFile not found: {file}");
            if (new FileInfo(file).Length > MaxTextFileBytes) throw new RpcException("bad_request", $"textFile is over {MaxTextFileBytes / 1024} KB: {file}");
            text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        var send = req.Bool("send") ?? !string.IsNullOrWhiteSpace(text);
        if (send && string.IsNullOrWhiteSpace(text)) throw new RpcException("bad_request", "Nothing to send: give text or textFile (or send: false to only set the chat up).");

        // The core's own concepts (projects, sessions) come from the store; the other plugins' through their RPC.
        JsonObject? project = null;
        if ((req.Str("project") ?? req.Str("projectId")) is { Length: > 0 } projectArg)
        {
            var projects = new JsonArray([.. ctx.Sessions.ListProjects().Select(p => (JsonNode?)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["path"] = p.Path })]);
            project = Pick(projects, projectArg, "id", "name")
                ?? throw new RpcException("bad_request", $"No project '{projectArg}'. Projects: {Names(projects, "name")}.");
        }

        string? agent = null;
        if (req.Str("agent") is { Length: > 0 } agentArg) agent = await ResolveAgentAsync(agentArg, ct).ConfigureAwait(false);

        string? profile = null;
        if (req.Str("profile") is { Length: > 0 } profileArg && !profileArg.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var list = (await Call("profiles.list", new { }, ct).ConfigureAwait(false))?["profiles"] as JsonArray ?? [];
            profile = Str(Pick(list, profileArg, "id", "name"), "id")
                ?? throw new RpcException("bad_request", $"No profile '{profileArg}'. Profiles: {Names(list, "id")}.");
        }

        var (wsKey, wsCreate, wsBase) = WorkspaceArg(req.Prop("workspace"));
        JsonObject? workspace = null;
        if (wsKey is not null)
        {
            if (project is null) throw new RpcException("bad_request", "A workspace belongs to a project: give project as well.");
            var list = await Call("workspaces.list", new { projectId = Str(project, "id") }, ct).ConfigureAwait(false) as JsonArray ?? [];
            workspace = Pick(list, wsKey, "id", "name");
            if (workspace is null && !wsCreate)
                throw new RpcException("bad_request", $"No workspace '{wsKey}' in {Str(project, "name")}. Workspaces: {Names(list, "name")} (or create: true).");
            if (workspace is not null && BusyIn(Str(workspace, "id")!) is { } busy)
                throw new RpcException("workspace_busy", $"Workspace {Str(workspace, "name")} is in use by a running chat ({busy}): wait until it is idle.");
        }

        // ---- make it, undoing everything when a step fails
        string? sessionId = null;
        string? createdWorkspace = null;
        try
        {
            sessionId = ctx.Sessions.CreateSession(new SessionInfo { Title = req.Str("title") ?? "", ProjectId = Str(project, "id") }).Id;
            if (wsKey is not null && workspace is null)
            {
                workspace = await Call("workspaces.create", new { projectId = Str(project, "id"), name = wsKey, ownerSessionId = sessionId, isolated = true, @base = wsBase }, ct).ConfigureAwait(false) as JsonObject;
                createdWorkspace = Str(workspace, "id");
            }
            if (workspace is not null) await Call("sessions.setWorkspace", new { id = sessionId, workspaceId = Str(workspace, "id") }, ct).ConfigureAwait(false);
            if (agent is not null) await Call("agents.use", new { sessionId, agent }, ct).ConfigureAwait(false);
            if (profile is not null) await Call("profiles.apply", new { sessionId, profile }, ct).ConfigureAwait(false);
            JsonNode? status = null;
            if (send) status = await Call("agent.send", new { sessionId, text = Fill(text!, workspace, project, sessionId), mode = "auto" }, ct).ConfigureAwait(false);
            return new
            {
                sessionId,
                title = ctx.Sessions.GetSession(sessionId)?.Title,
                project = project is null ? null : new { id = Str(project, "id"), name = Str(project, "name"), path = Str(project, "path") },
                workspace = workspace is null ? null : new { id = Str(workspace, "id"), name = Str(workspace, "name"), path = Str(workspace, "path"), branch = Str(workspace, "branch"), created = createdWorkspace is not null },
                agent,
                profile,
                sent = send,
                status = Str(status, "status"),
            };
        }
        catch (Exception ex) when (sessionId is not null)
        {
            if (ex is not OperationCanceledException) ctx.Logger.LogInformation("Dispatch failed after the chat was made; undoing it: {Error}", ex.Message);
            try { ctx.Sessions.DeleteSession(sessionId); }
            catch (Exception undo) { ctx.Logger.LogWarning(undo, "Could not delete the half-made chat {Session}", sessionId); }
            if (createdWorkspace is not null)
            {
                try { await Call("workspaces.delete", new { id = createdWorkspace }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception undo) { ctx.Logger.LogWarning(undo, "Could not remove the workspace {Workspace} made for it", createdWorkspace); }
            }
            throw;
        }
    }

    /// <summary>
    /// An agent key as the user set it up, or <c>any</c>. Matching ignores case, and a part of a key that names exactly one
    /// agent is that agent ("bunny" → space-bunny-alpha). One that is switched off or cannot take work is refused now, not
    /// when its run starts.
    /// </summary>
    private async Task<string> ResolveAgentAsync(string arg, CancellationToken ct)
    {
        if (arg.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any";
        var agents = (await Call("agents.list", new { }, ct).ConfigureAwait(false) as JsonArray ?? [])
            .OfType<JsonObject>().Where(a => Flag(a, "configured")).ToList();
        var exact = agents.FirstOrDefault(a => string.Equals(Str(a, "key"), arg, StringComparison.OrdinalIgnoreCase));
        var partial = agents.Where(a => Str(a, "key")?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true).ToList();
        var match = exact ?? (partial.Count == 1 ? partial[0] : null);
        if (match is null)
            throw new RpcException("bad_request", partial.Count > 1
                ? $"Agent '{arg}' could be {string.Join(" or ", partial.Select(a => Str(a, "key")))}: give the whole key."
                : $"No agent '{arg}'. Agents: {string.Join(", ", agents.Select(a => Str(a, "key")))}, or any.");
        var key = Str(match, "key")!;
        if (Flag(match, "disabled")) throw new RpcException("bad_request", $"Agent {key} is switched off.");
        if (match["available"] is JsonValue av && av.TryGetValue<bool>(out var available) && !available)
            throw new RpcException("bad_request", $"Agent {key} cannot take work now ({Str(match, "unavailable") ?? "unavailable"}).");
        return key;
    }

    /// <summary><c>workspace</c>: a name or id, or <c>{ name | id, create?, base? }</c>.</summary>
    private static (string? Key, bool Create, string? Base) WorkspaceArg(JsonElement? p)
    {
        if (p is not { } e || e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return (null, false, null);
        if (e.ValueKind == JsonValueKind.String) return (e.GetString() is { Length: > 0 } s ? s : null, false, null);
        if (e.ValueKind != JsonValueKind.Object) throw new RpcException("bad_request", "workspace is a name, an id, or { name | id, create?, base? }.");
        var a = new ToolArgs(e);
        var key = a.Str("id") ?? a.Str("name") ?? throw new RpcException("bad_request", "workspace needs a name or an id.");
        return (key, a.Bool("create") ?? false, a.Str("base"));
    }

    /// <summary>The running chat bound to a workspace, if any: a dispatch into it would share the checkout with a live worker.</summary>
    private string? BusyIn(string workspaceId)
    {
        var rt = ctx.Services.Get<IAgentRuntime>();
        if (rt is null) return null;
        foreach (var s in ctx.Sessions.ListSessions(new SessionQuery { Limit = 2000 }))
        {
            if (s.Meta?["workspaceId"] is not JsonValue v || !v.TryGetValue<string>(out var id) || id != workspaceId) continue;
            if (rt.GetBySession(s.Id) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded }) return $"{s.Title} ({s.Id})";
        }
        return null;
    }

    private static JsonObject? Pick(JsonArray list, string key, params string[] fields) =>
        list.OfType<JsonObject>().FirstOrDefault(o => fields.Any(f => string.Equals(Str(o, f), key, StringComparison.OrdinalIgnoreCase)));

    private static string Names(JsonArray list, string field)
    {
        var names = list.OfType<JsonObject>().Select(o => Str(o, field)).Where(n => n is not null).ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    private static string Fill(string text, JsonNode? workspace, JsonNode? project, string sessionId) => text
        .Replace("{{workspacePath}}", Str(workspace, "path") ?? Str(project, "path") ?? "", StringComparison.Ordinal)
        .Replace("{{branch}}", Str(workspace, "branch") ?? "", StringComparison.Ordinal)
        .Replace("{{workspaceName}}", Str(workspace, "name") ?? "", StringComparison.Ordinal)
        .Replace("{{projectPath}}", Str(project, "path") ?? "", StringComparison.Ordinal)
        .Replace("{{sessionId}}", sessionId, StringComparison.Ordinal);

    // ------------------------------------------------------------------ runs.result

    /// <summary>How many of the newest messages a result reads to find the run's own.</summary>
    public const int MessagesRead = 400;

    public async Task<object?> ResultAsync(RpcRequest req, CancellationToken ct)
    {
        var rt = ctx.Services.Get<IAgentRuntime>();
        var sessionId = req.Str("sessionId");
        var info = sessionId is not null ? rt?.GetBySession(sessionId)
            : req.Str("runId") is { } runId ? rt?.Get(runId) ?? throw new RpcException("not_found", $"No run {runId}.")
            : throw new RpcException("bad_request", "Give sessionId or runId.");
        sessionId ??= info!.SessionId;
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}.");

        // The run's own messages: from the moment it started (its input is written then), newest page only.
        var start = info?.StartedAt;
        var messages = ctx.Sessions.GetMessages(sessionId, null, MessagesRead)
            .Where(m => start is null || m.CreatedAt >= start.Value - TimeSpan.FromSeconds(2)).OrderBy(m => m.Seq).ToList();
        var report = messages.LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))?.Text;
        var lastCall = messages.SelectMany(m => m.ToolCalls).LastOrDefault();

        var todoItems = (session.Meta?["todo"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => new { text = Str(t, "text") ?? "", status = Str(t, "status") ?? "pending" }).ToList();

        object? workspace = null;
        if (session.Meta?["workspaceId"] is JsonValue wv && wv.TryGetValue<string>(out var wsId))
        {
            try
            {
                var w = await Call("workspaces.get", new { id = wsId }, ct).ConfigureAwait(false);
                workspace = new { id = wsId, name = Str(w, "name"), path = Str(w, "path"), branch = Str(w, "branch") };
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException) { workspace = new { id = wsId }; }
        }

        var commits = new List<object>();
        if (start is not null)
        {
            try
            {
                var history = await Call("files.commits", new { sessionId, limit = 50 }, ct).ConfigureAwait(false);
                foreach (var c in history?["commits"] as JsonArray ?? [])
                {
                    if (!DateTimeOffset.TryParse(Str(c, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at < start.Value - TimeSpan.FromSeconds(2)) continue;
                    commits.Add(new { hash = Str(c, "hash"), @short = Str(c, "short"), subject = Str(c, "subject"), at = Str(c, "at") });
                }
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException) { /* no files plugin, or not a repository */ }
        }

        var end = info?.FinishedAt;
        return new
        {
            sessionId,
            title = session.Title,
            runId = info?.Id,
            status = info is null ? "none" : JsonNamingPolicy.CamelCase.ConvertName(info.Status.ToString()),
            activity = info?.Activity,
            runs = info?.Runs ?? 0,
            startedAt = start,
            finishedAt = end,
            durationMs = start is null ? (long?)null : (long)((end ?? DateTimeOffset.UtcNow) - start.Value).TotalMilliseconds,
            turns = info?.Turns ?? 0,
            toolCalls = info?.ToolCalls ?? 0,
            error = info?.Error,
            queued = info?.QueuedMessages ?? 0,
            report,
            lastTool = lastCall is null ? null : new { name = lastCall.Name, arguments = lastCall.Arguments.Length > 300 ? lastCall.Arguments[..300] + "…" : lastCall.Arguments },
            todo = todoItems.Count == 0 ? null : new
            {
                done = todoItems.Count(t => t.status == "done"),
                total = todoItems.Count,
                current = todoItems.FirstOrDefault(t => t.status == "in_progress")?.text ?? todoItems.FirstOrDefault(t => t.status != "done")?.text,
                items = todoItems,
            },
            commits,
            workspace,
        };
    }
}
