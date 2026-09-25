using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>Where a session's/project's ideas live.</summary>
public sealed record IdeasLocation(string File, string Scope, string? ProjectId, string? ProjectName)
{
    public string FileName => Path.GetFileName(File);
}

/// <summary>Resolves the ideas file: the project folder, else <c>~/.netpi/ideas.json</c> (name: setting <c>ideas.fileName</c>).</summary>
public sealed class IdeasLocator(Func<ISessionStore?> sessions, NetPiPaths paths, Func<ISettings?> settings)
{
    public string FileName()
    {
        string? name = null;
        try { name = settings()?.Get<string?>("ideas.fileName", null); } catch { }
        name = string.IsNullOrWhiteSpace(name) ? "ideas.json" : Path.GetFileName(name.Trim());
        return name.Length == 0 ? "ideas.json" : name;
    }

    public IdeasLocation Global() => new(IdeasStore.Normalize(Path.Combine(paths.Home, FileName())), "global", null, null);

    public IdeasLocation ForProject(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Path)) return Global();
        return new(IdeasStore.Normalize(Path.Combine(project.Path, FileName())), "project", project.Id, project.Name);
    }

    /// <summary>RPC resolution: projectId wins, then the session's project; unknown ids are errors.</summary>
    public IdeasLocation Resolve(string? sessionId, string? projectId)
    {
        var store = sessions();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var p = store?.GetProject(projectId) ?? throw new RpcException("not_found", $"Project {projectId} not found");
            return ForProject(p);
        }
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var s = store?.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
            return ForProject(s.ProjectId is { } pid ? store.GetProject(pid) : null);
        }
        return Global();
    }

    /// <summary>Tool resolution: the call's project, else the session's project, else the global file.</summary>
    public IdeasLocation ForTool(ToolContext ctx)
    {
        if (ctx.Project is not null) return ForProject(ctx.Project);
        try
        {
            var store = sessions();
            var s = store?.GetSession(ctx.SessionId);
            if (s?.ProjectId is { } pid) return ForProject(store!.GetProject(pid));
        }
        catch { }
        return Global();
    }
}

/// <summary>Shared plumbing for the idea_* tools.</summary>
public abstract class IdeaToolBase(IdeasStore store, IdeasLocator locator) : IAgentTool
{
    protected IdeasStore Store { get; } = store;
    protected IdeasLocator Locator { get; } = locator;

    public abstract ToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            var loc = Locator.ForTool(context);
            if (loc.Scope == "project" && !Directory.Exists(Path.GetDirectoryName(loc.File)))
                return ToolResult.Error($"The project folder {Path.GetDirectoryName(loc.File)} does not exist.");
            return await RunAsync(context, loc, Args(args), ct).ConfigureAwait(false);
        }
        catch (IdeaInputException ex) { return ToolResult.Error(ex.Message); }
        catch (IdeasFileException ex) { return ToolResult.Error(ex.Message); }
        catch (IOException ex) { return ToolResult.Error("Could not access the ideas file: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return ToolResult.Error("Could not access the ideas file: " + ex.Message); }
    }

    protected abstract Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct);

    /// <summary>Arguments as a JsonObject (a JSON string holding the object is unwrapped).</summary>
    public static JsonObject Args(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object) return JsonObject.Create(args.Clone()) ?? [];
        if (args.ValueKind == JsonValueKind.String)
        {
            try { if (JsonNode.Parse(args.GetString() ?? "") is JsonObject o) return o; } catch (JsonException) { }
        }
        return [];
    }

    protected static string RequireId(JsonObject args) =>
        IdeaOps.Str(args, "id", "ideaId") is { Length: > 0 } id ? id.Trim() : throw new IdeaInputException("Missing 'id'. Use idea_list to see the ids.");

    protected static JsonObject Details(IdeasLocation loc, JsonObject? idea) => new()
    {
        ["file"] = loc.File, ["scope"] = loc.Scope, ["idea"] = idea?.DeepClone(),
    };

    protected static IdeaInputException NotFound(string id) => new($"No idea with id '{id}'. Use idea_list to see the ids.");

    protected static JsonObject SectionSchema(bool withId) => new()
    {
        ["type"] = "object",
        ["properties"] = withId
            ? new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Section id (sec-…) from idea_get" },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Markdown; replaces the old content" },
                ["kind"] = KindSchema(),
            }
            : new JsonObject
            {
                ["kind"] = KindSchema(),
                ["title"] = new JsonObject { ["type"] = "string" },
                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Markdown" },
            },
        ["required"] = withId ? new JsonArray("id") : new JsonArray("content"),
    };

    private static JsonObject KindSchema() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(IdeaOps.Kinds.Select(k => (JsonNode?)k).ToArray()),
    };

    protected static JsonObject Enum(string[] values, string description) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()), ["description"] = description,
    };

    protected static JsonObject Tags() => new()
    {
        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
    };
}

public sealed class IdeaAddTool(IdeasStore store, IdeasLocator locator) : IdeaToolBase(store, locator)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "idea_add",
        Label = "Add idea",
        Category = "ideas",
        SummaryArg = "title",
        Description =
            "Record an idea, research result or plan in the project's ideas backlog (ideas.json) so it is not lost. " +
            "Put findings, plans and requirements into sections.",
        PromptGuidelines =
        [
            "Record research and plans that are deferred, out of scope or not feasible now with idea_add; check idea_list before larger work.",
        ],
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short, specific title" },
                ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences: what and why" },
                ["priority"] = Enum(IdeaOps.Priorities, "Default medium"),
                ["tags"] = Tags(),
                ["sections"] = new JsonObject { ["type"] = "array", ["items"] = SectionSchema(withId: false), ["description"] = "Details: research, plan, requirements, design, decision, blocker, links, todo or note" },
            },
            ["required"] = new JsonArray("title"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var idea = await Store.UpdateAsync(loc.File, f =>
        {
            var created = IdeaOps.CreateIdea(args, f.Ideas, "agent:" + context.AgentId, context.SessionId);
            f.Ideas.Add(created);
            return (JsonObject)created.DeepClone();
        }, ct).ConfigureAwait(false);
        var n = (idea["sections"] as JsonArray)?.Count ?? 0;
        return ToolResult.Ok($"Added idea {IdeaOps.Str(idea["id"])}: {IdeaOps.Str(idea["title"])}" +
                             (n > 0 ? $" ({n} section{(n == 1 ? "" : "s")})" : "") + $" to {loc.File}", Details(loc, idea));
    }
}

public sealed class IdeaListTool(IdeasStore store, IdeasLocator locator) : IdeaToolBase(store, locator)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "idea_list",
        Label = "Ideas",
        Category = "ideas",
        ReadOnly = true,
        SummaryArg = "query",
        Description = "List ideas in the project's ideas backlog (compact: id, status, priority, title, summary, tags). " +
                      "By default done/rejected ideas are hidden; pass status \"all\" to see everything.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["status"] = new JsonObject { ["type"] = "string", ["description"] = "open, parked, planned, in-progress, done, rejected, active (default) or all" },
                ["tag"] = new JsonObject { ["type"] = "string" },
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Words that must all appear in the title, summary, tags or sections" },
            },
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var status = IdeaOps.Str(args, "status");
        var tag = IdeaOps.Str(args, "tag");
        var query = IdeaOps.Str(args, "query", "search");
        var (lines, total, hidden) = await Store.ReadAsync(loc.File, f =>
        {
            var all = IdeaOps.All(f.Ideas).ToList();
            var effective = string.IsNullOrWhiteSpace(status) ? "active" : status;
            var matched = all.Where(i => IdeaOps.Matches(i, effective, tag, query)).ToList();
            var hiddenCount = string.IsNullOrWhiteSpace(status)
                ? all.Count(i => IdeaOps.Matches(i, "all", tag, query)) - matched.Count
                : 0;
            return (matched.Select(IdeaOps.ListLine).ToList(), all.Count, hiddenCount);
        }, ct).ConfigureAwait(false);

        var where = loc.Scope == "project" ? $"project \"{loc.ProjectName}\" ({loc.File})" : $"the global backlog ({loc.File})";
        if (total == 0) return ToolResult.Ok($"No ideas yet in {where}.");
        var head = lines.Count == 0 ? $"No matching ideas in {where} ({total} total)." : $"{lines.Count} idea{(lines.Count == 1 ? "" : "s")} in {where}:";
        var text = head + (lines.Count > 0 ? "\n" + string.Join('\n', lines) : "");
        if (hidden > 0) text += $"\n({hidden} done/rejected hidden; pass status \"all\" to include them.)";
        return ToolResult.Ok(text, new JsonObject { ["file"] = loc.File, ["scope"] = loc.Scope, ["count"] = lines.Count, ["total"] = total });
    }
}

public sealed class IdeaGetTool(IdeasStore store, IdeasLocator locator) : IdeaToolBase(store, locator)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "idea_get",
        Label = "Idea",
        Category = "ideas",
        ReadOnly = true,
        SummaryArg = "id",
        Description = "Show one idea from the ideas backlog in full (all sections as markdown, with section ids for idea_update).",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Idea id (idea-…)" } },
            ["required"] = new JsonArray("id"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var id = RequireId(args);
        var idea = await Store.ReadAsync(loc.File, f => IdeaOps.Find(f.Ideas, id)?.DeepClone() as JsonObject, ct).ConfigureAwait(false)
                   ?? throw NotFound(id);
        return ToolResult.Ok(IdeaOps.RenderMarkdown(idea), Details(loc, idea));
    }
}

public sealed class IdeaUpdateTool(IdeasStore store, IdeasLocator locator) : IdeaToolBase(store, locator)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "idea_update",
        Label = "Update idea",
        Category = "ideas",
        SummaryArg = "id",
        Description = "Update an idea in the ideas backlog: title, summary, status, priority, tags, and add/update/remove sections. " +
                      "Set status \"in-progress\" when you start implementing an idea and \"done\" when finished.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["title"] = new JsonObject { ["type"] = "string" },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["status"] = Enum(IdeaOps.Statuses, "New status"),
                ["priority"] = Enum(IdeaOps.Priorities, "New priority"),
                ["tags"] = Tags(),
                ["addSections"] = new JsonObject { ["type"] = "array", ["items"] = SectionSchema(withId: false) },
                ["updateSections"] = new JsonObject { ["type"] = "array", ["items"] = SectionSchema(withId: true) },
                ["removeSectionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            },
            ["required"] = new JsonArray("id"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var id = RequireId(args);
        var patch = (JsonObject)args.DeepClone();
        patch.Remove("id");
        var (idea, changes) = await Store.UpdateAsync(loc.File, f =>
        {
            var target = IdeaOps.Find(f.Ideas, id) ?? throw NotFound(id);
            var c = IdeaOps.ApplyPatch(target, patch, fromUi: false, context.SessionId);
            return ((JsonObject)target.DeepClone(), c);
        }, ct).ConfigureAwait(false);
        var text = changes.Count == 0
            ? $"No changes to {IdeaOps.Str(idea["id"])} (values were already set)."
            : $"Updated {IdeaOps.Str(idea["id"])} ({IdeaOps.Str(idea["title"])}): {string.Join(", ", changes)}.";
        return ToolResult.Ok(text, Details(loc, idea));
    }
}

public sealed class IdeaRemoveTool(IdeasStore store, IdeasLocator locator) : IdeaToolBase(store, locator)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "idea_remove",
        Label = "Remove idea",
        Category = "ideas",
        SummaryArg = "id",
        Description = "Delete an idea from the ideas backlog. Prefer idea_update with status \"done\" or \"rejected\" to keep the history; " +
                      "remove only duplicates or entries created by mistake.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("id"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var id = RequireId(args);
        var removed = await Store.UpdateAsync(loc.File, f =>
        {
            var target = IdeaOps.Find(f.Ideas, id) ?? throw NotFound(id);
            f.Ideas.Remove(target);
            return target;
        }, ct).ConfigureAwait(false);
        return ToolResult.Ok($"Removed {IdeaOps.Str(removed["id"])}: {IdeaOps.Str(removed["title"])}", Details(loc, removed));
    }
}
