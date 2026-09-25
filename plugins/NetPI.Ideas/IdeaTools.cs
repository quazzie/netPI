using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>Where a session's/project's ideas live.</summary>
public sealed record IdeasLocation(string File, string Scope, string? ProjectId, string? ProjectName, string? ProjectDir = null)
{
    public string FileName => Path.GetFileName(File);

    /// <summary>The file as prompts name it: relative to the project (<c>.netpi/ideas.json</c>), else the full path.</summary>
    public string Shown => Scope == "project" && ProjectDir is not null ? Path.GetRelativePath(ProjectDir, File).Replace('\\', '/') : File;
}

/// <summary>
/// Resolves the ideas file: <c>.netpi/ideas.json</c> in the project folder, else <c>~/.netpi/ideas.json</c> (name:
/// setting <c>ideas.fileName</c>). A file that an earlier version kept in the project folder itself moves into
/// <c>.netpi/</c> the first time the project's ideas are used.
/// </summary>
public sealed class IdeasLocator(Func<ISessionStore?> sessions, NetPiPaths paths, Func<ISettings?> settings)
{
    /// <summary>The project folder the file lives in, like <c>.netpi/skills</c>.</summary>
    public const string Folder = ".netpi";

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
        var dir = IdeasStore.Normalize(project.Path);
        var name = FileName();
        var file = IdeasStore.Normalize(Path.Combine(dir, Folder, name));
        MoveFromRoot(Path.Combine(dir, name), file);
        return new(file, "project", project.Id, project.Name, dir);
    }

    /// <summary>The old place, the project folder itself, into <c>.netpi/</c>; not when a file is there already.</summary>
    internal static void MoveFromRoot(string old, string file)
    {
        try
        {
            if (!File.Exists(old) || File.Exists(file)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Move(old, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // another call moved it, or it is locked: next time
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

/// <summary>
/// The agent's tool: <c>ideas</c> with an action (list, get, add, update), one schema in every request instead of
/// five. Deleting is left to the user, in the Ideas tab.
/// </summary>
public sealed class IdeasTool(IdeasStore store, IdeasLocator locator) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "ideas",
        Label = "Ideas",
        Category = "ideas",
        SummaryArg = "action",
        Description =
            "The project's backlog of ideas, research and plans (.netpi/ideas.json; the user sees it in the Ideas tab). " +
            "Actions: list (the open ideas: id, status, priority, title, summary, tags), get (one in full, with its section ids), " +
            "add (title, summary, priority, tags, sections), update (the id and what changes: status, fields, addSections, " +
            "updateSections, removeSectionIds). Deleting is up to the user.",
        PromptGuidelines =
        [
            "Record research and plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), " +
            "and look at the open ideas (action list) before larger work. When you finish the work an idea describes, set it to done (action update).",
        ],
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = Enum(["list", "get", "add", "update"], "list: the open ideas; get: one in full; add: a new one; update: change one"),
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "The idea (idea-…), for get and update" },
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short, specific title" },
                ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences: what and why" },
                ["status"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "update: open, parked, planned, in-progress, done or rejected. list: a filter, also active (the default: not done or rejected) or all",
                },
                ["priority"] = Enum(IdeaOps.Priorities, "Default medium"),
                ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["tag"] = new JsonObject { ["type"] = "string", ["description"] = "list: only ideas with this tag" },
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "list: words that must all appear in the title, summary, tags or sections" },
                ["sections"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = SectionSchema(withId: false),
                    ["description"] = "add: the details (research, plan, requirements, design, decision, blocker, links, todo or note)",
                },
                ["addSections"] = new JsonObject { ["type"] = "array", ["items"] = SectionSchema(withId: false) },
                ["updateSections"] = new JsonObject { ["type"] = "array", ["items"] = SectionSchema(withId: true) },
                ["removeSectionIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            },
            ["required"] = new JsonArray("action"),
        },
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            var loc = locator.ForTool(context);
            if (loc.Scope == "project" && !Directory.Exists(loc.ProjectDir))
                return ToolResult.Error($"The project folder {loc.ProjectDir} does not exist.");
            var a = Args(args);
            return Action(a) switch
            {
                "list" => await ListAsync(loc, a, ct).ConfigureAwait(false),
                "get" => await GetAsync(loc, a, ct).ConfigureAwait(false),
                "add" => await AddAsync(context, loc, a, ct).ConfigureAwait(false),
                "update" => await UpdateAsync(context, loc, a, ct).ConfigureAwait(false),
                "delete" => ToolResult.Error("Deleting an idea is up to the user, in the Ideas tab. Set it to done or rejected instead (action update)."),
                var other => ToolResult.Error($"Unknown action \"{other}\": use list, get, add or update."),
            };
        }
        catch (IdeaInputException ex) { return ToolResult.Error(ex.Message); }
        catch (IdeasFileException ex) { return ToolResult.Error(ex.Message); }
        catch (IOException ex) { return ToolResult.Error("Could not access the ideas file: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return ToolResult.Error("Could not access the ideas file: " + ex.Message); }
    }

    /// <summary>
    /// The action, leniently: synonyms (create, show, edit, search…); close / done / complete are an update to done; without
    /// an action, what the arguments suggest (an id with changes: update, an id alone: get, a title: add, else list).
    /// </summary>
    internal static string Action(JsonObject args)
    {
        var raw = IdeaOps.Str(args, "action", "op", "operation", "command", "mode")?.Trim().ToLowerInvariant();
        switch (raw)
        {
            case "list" or "search" or "find" or "ls" or "open" or "all":
                return "list";
            case "get" or "show" or "read" or "view" or "details":
                return "get";
            case "add" or "create" or "new" or "record" or "insert":
                return "add";
            case "update" or "edit" or "set" or "change" or "modify" or "patch":
                return "update";
            case "close" or "done" or "complete" or "finish" or "resolve":
                if (IdeaOps.Str(args, "status") is null) args["status"] = "done";
                return "update";
            case "remove" or "delete" or "rm" or "drop":
                return "delete";
            case null or "":
                break;
            default:
                return raw;
        }
        var hasId = IdeaOps.Str(args, "id", "ideaId") is { Length: > 0 };
        string[] changes = ["title", "summary", "status", "priority", "tags", "addSections", "add_sections", "updateSections", "update_sections", "removeSectionIds", "remove_section_ids"];
        if (hasId) return changes.Any(args.ContainsKey) ? "update" : "get";
        return IdeaOps.Str(args, "title") is { Length: > 0 } ? "add" : "list";
    }

    private async Task<ToolResult> AddAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var idea = await store.UpdateAsync(loc.File, f =>
        {
            var created = IdeaOps.CreateIdea(args, f.Ideas, "agent:" + context.AgentId, context.SessionId);
            f.Ideas.Add(created);
            return (JsonObject)created.DeepClone();
        }, ct).ConfigureAwait(false);
        var n = (idea["sections"] as JsonArray)?.Count ?? 0;
        return ToolResult.Ok($"Added idea {IdeaOps.Str(idea["id"])}: {IdeaOps.Str(idea["title"])}" +
                             (n > 0 ? $" ({n} section{(n == 1 ? "" : "s")})" : "") + $" to {loc.File}", Details(loc, idea));
    }

    private async Task<ToolResult> ListAsync(IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var status = IdeaOps.Str(args, "status");
        var tag = IdeaOps.Str(args, "tag");
        var query = IdeaOps.Str(args, "query", "search");
        var (lines, total, hidden) = await store.ReadAsync(loc.File, f =>
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

    private async Task<ToolResult> GetAsync(IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var id = RequireId(args);
        var idea = await store.ReadAsync(loc.File, f => IdeaOps.Find(f.Ideas, id)?.DeepClone() as JsonObject, ct).ConfigureAwait(false)
                   ?? throw NotFound(id);
        return ToolResult.Ok(IdeaOps.RenderMarkdown(idea), Details(loc, idea));
    }

    private async Task<ToolResult> UpdateAsync(ToolContext context, IdeasLocation loc, JsonObject args, CancellationToken ct)
    {
        var id = RequireId(args);
        var patch = (JsonObject)args.DeepClone();
        foreach (var k in new[] { "id", "ideaId", "action", "op", "operation", "command", "mode" }) patch.Remove(k);
        var (idea, changes) = await store.UpdateAsync(loc.File, f =>
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

    private static string RequireId(JsonObject args) =>
        IdeaOps.Str(args, "id", "ideaId") is { Length: > 0 } id ? id.Trim() : throw new IdeaInputException("Missing 'id'. The list action shows the ids.");

    private static JsonObject Details(IdeasLocation loc, JsonObject? idea) => new()
    {
        ["file"] = loc.File, ["scope"] = loc.Scope, ["idea"] = idea?.DeepClone(),
    };

    private static IdeaInputException NotFound(string id) => new($"No idea with id '{id}'. The list action shows the ids.");

    private static JsonObject SectionSchema(bool withId) => new()
    {
        ["type"] = "object",
        ["properties"] = withId
            ? new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Section id (sec-…) from the get action" },
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

    private static JsonObject Enum(string[] values, string description) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()), ["description"] = description,
    };
}
