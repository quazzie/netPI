using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Workspaces;

/// <summary>
/// <c>workspace</c>: what this session works in, what else exists, and — at a safe boundary — a switch to another one.
/// A switch is <em>not</em> applied in the middle of a tool batch: it is recorded and taken at the start of the next
/// model call, so a tool that already resolved a path against the old root cannot land a write in the old tree after the
/// root changed under it.
/// </summary>
internal sealed class WorkspaceTool(IPluginContext ctx, WorkspaceResolver resolver) : IAgentTool
{
    public const string Name = "workspace";

    /// <summary>Ask for a switch; the applier takes it at the next safe boundary (between model calls).</summary>
    internal static void Request(string sessionId, string? workspaceId) => WorkspaceSwitchApplier.Request(sessionId, workspaceId);

    /// <summary>A switch waiting for a safe boundary, if any.</summary>
    internal static string? PendingFor(string sessionId) => WorkspaceSwitchApplier.PendingFor(sessionId);

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name,
        Label = "Workspace",
        Category = "general",
        SummaryArg = "action",
        Description = "The checkout this session works in: info (where you are), list (workspaces of the project), switch {id} (at the next safe boundary).",
        Help =
            "info: where relative paths resolve — the workspace root, its branch, and whether it is your own checkout.\n" +
            "list: the project's workspaces with their branches and owners.\n" +
            "switch {id}: work in another workspace from the next model call on. The switch is applied at a safe boundary, " +
            "after the tools of the current batch have finished, and a target that does not exist or belongs to another " +
            "repository is refused rather than ignored.\n" +
            "A workspace you got with agent_spawn is yours: commit your work on its branch and tell the parent the branch name, " +
            "so it can be merged. Your workspace is not the project's folder: a write that resolves to another checkout of " +
            "the same repository is refused.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("info", "list", "switch") },
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Workspace id or name (for switch)" },
            },
        },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var action = new ToolArgs(args).Str("action")?.Trim() ?? "info";
        var session = ctx.Services.Get<ISessionStore>()?.GetSession(context.SessionId);
        return Task.FromResult(action switch
        {
            "list" => List(session),
            "switch" => Switch(context, args, session),
            _ => Info(context, session),
        });
    }

    private ToolResult Info(ToolContext context, SessionInfo? session)
    {
        var binding = context.Workspace();
        var project = context.Project;
        var lines = new List<string>();
        if (binding is null)
        {
            lines.Add($"This session is not bound to a workspace: it works in the project folder ({context.Cwd}).");
            if (project is not null) lines.Add($"Project: \"{project.Name}\" ({project.Path}).");
            lines.Add("Another writer in another checkout of the same repository is not protected here; ask for one with agent_spawn (isolated: true) or switch to a workspace.");
        }
        else
        {
            lines.Add($"Workspace {binding.WorkspaceId} ({binding.Kind}): {binding.Describe()}");
            if (binding.BaseCommit is { Length: > 0 } c) lines.Add($"Started from {c[..Math.Min(8, c.Length)]}.");
            if (binding.Isolated) lines.Add("It is your own checkout: writes outside it that point at another checkout of the same repository are refused, and a background process started here keeps it.");
            else lines.Add("It is not your own checkout: writes here reach the files other workers of this project see.");
            if (binding.OwnerSessionId is { Length: > 0 } owner && owner != context.SessionId)
                lines.Add($"Owned by another worker ({owner}); you share its checkout.");
        }
        if (project is not null) lines.Add($"Project: \"{project.Name}\" ({project.Path}).");
        var pending = PendingFor(context.SessionId);
        if (pending is not null) lines.Add("A workspace switch is pending and applies before the next model call.");
        return ToolResult.Ok(string.Join("\n", lines), new JsonObject
        {
            ["workspaceId"] = binding?.WorkspaceId,
            ["root"] = binding?.Root ?? context.Cwd,
            ["branch"] = binding?.Branch,
            ["isolated"] = binding?.Isolated ?? false,
            ["pending"] = pending is null ? null : JsonValue.Create(pending),
        });
    }

    private ToolResult List(SessionInfo? session)
    {
        if (session?.ProjectId is not { Length: > 0 } projectId)
            return ToolResult.Error("This session has no project, so it has no workspaces. Attach a project first (the user can do that in the UI).");
        var store = ctx.Services.Get<IWorkspaceStore>();
        if (store is null) return ToolResult.Error("The workspace store is not available.");
        var all = store.ListWorkspaces(projectId);
        if (all.Count == 0)
            return ToolResult.Ok($"No workspaces for project \"{ctx.Sessions.GetProject(projectId)?.Name}\": this session works in the project's folder.");
        var sb = new System.Text.StringBuilder($"Workspaces of \"{ctx.Sessions.GetProject(projectId)?.Name}\":");
        foreach (var w in all)
        {
            sb.Append("\n- ").Append(w.Id).Append(' ').Append(w.Name);
            sb.Append(w.Path);
            if (w.Branch is { Length: > 0 } b) sb.Append(" (branch ").Append(b).Append(')');
            if (w.OwnerSessionId is { Length: > 0 } o) sb.Append(" · owner ").Append(o);
            if (w.Managed) sb.Append(" · created by NetPI");
            if (!Directory.Exists(w.Path)) sb.Append(" · MISSING on disk");
        }
        return ToolResult.Ok(sb.ToString(), new JsonObject { ["workspaces"] = new JsonArray([.. all.Select(w => (JsonNode)new JsonObject
        {
            ["workspaceId"] = w.Id, ["name"] = w.Name, ["path"] = w.Path, ["branch"] = w.Branch, ["kind"] = w.Kind,
        })]) });
    }

    private ToolResult Switch(ToolContext context, JsonElement args, SessionInfo? session)
    {
        var id = new ToolArgs(args).Str("id", "workspaceId", "workspace", "name")?.Trim();
        if (session is null) return ToolResult.Error("This session no longer exists.");
        if (id is null or "")
        {
            // No id: unbind, which is the explicit way back to the project's folder.
            Request(session.Id, null);
            return ToolResult.Ok(
                "The session will work in the project's folder again from the next model call on. Anything already written stays where it is.");
        }
        var store = ctx.Services.Get<IWorkspaceStore>();
        var target = store?.ListWorkspaces().FirstOrDefault(w =>
            string.Equals(w.Id, id, StringComparison.Ordinal) || string.Equals(w.Name, id, StringComparison.OrdinalIgnoreCase));
        if (target is null) return ToolResult.Error($"No workspace \"{id}\". Call workspace with action list to see them.");
        if (!Directory.Exists(target.Path))
            return ToolResult.Error($"Workspace \"{target.Name}\" ({target.Path}) does not exist on disk; switching to it would leave the session with no working directory.");
        if (session.ProjectId is { Length: > 0 } projectId && target.ProjectId is { Length: > 0 } tid && tid != projectId)
            return ToolResult.Error($"Workspace \"{target.Name}\" belongs to another project than this one.");
        try
        {
            resolver.Validate(session, target);
        }
        catch (WorkspaceUnavailableException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        Request(session.Id, target.Id);
        return ToolResult.Ok(
            $"The session will work in \"{target.Name}\" ({Describe(target)}) from the next model call on. " +
            "The switch happens at a safe boundary: the tools of the current batch finish first.", Details(target));
    }

    /// <summary>The workspace one line: its root and branch.</summary>
    internal static string Describe(WorkspaceInfo w) =>
        w.Branch is { Length: > 0 } b ? $"{w.Path} (branch {b})" : w.Path;

    private static object Details(WorkspaceInfo w) => new
    {
        workspaceId = w.Id, name = w.Name, path = w.Path, branch = w.Branch, kind = w.Kind, isolated = w.Kind == "worktree",
    };
}