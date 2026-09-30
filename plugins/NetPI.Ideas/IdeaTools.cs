using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// Where the ideas live: in the app's database, in the tables of the <c>netpi.ideas</c> plugin. The
/// <c>ideas.fileName</c> setting survives as the name of the file the JSON versions used — the one a cutover reads, and
/// the default name an export is written under — and no longer as a live file. A session's calls default to the ideas of
/// its project (the ideas with a matching <c>project</c>), and the agent's <c>project</c> argument reaches any project
/// or the unbound ("global") ones.
/// </summary>
public sealed class IdeasLocator(Func<ISessionStore?> sessions, NetPiPaths paths, Func<ISettings?> settings)
{
    /// <summary>The name the JSON backlog had (setting <c>ideas.fileName</c>, default "ideas.json").</summary>
    public string FileName()
    {
        string? name = null;
        try { name = settings()?.Get<string?>("ideas.fileName", null); } catch { }
        name = string.IsNullOrWhiteSpace(name) ? "ideas.json" : Path.GetFileName(name.Trim());
        return name.Length == 0 ? "ideas.json" : name;
    }

    /// <summary>Where that legacy file is, or would be. The backlog is not written there any more.</summary>
    public string LegacyFile() => Path.GetFullPath(Path.Combine(paths.Home, FileName()));

    /// <summary>The default name an export gets (the same one, so a round trip is obvious).</summary>
    public string DefaultExportName() => Path.GetFileNameWithoutExtension(FileName()) + "-export.json";

    /// <summary>The home the backlog's tables live in (the app's database is here).</summary>
    public string Home => paths.Home;

    /// <summary>The project a session's calls default to (a new idea's stamp, a list's slice); null when the session has no project. Unknown sessions are an error.</summary>
    public ProjectInfo? ProjectOfSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var store = sessions();
        var s = store?.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
        return s.ProjectId is { } pid ? store.GetProject(pid) : null;
    }

    /// <summary>The tool's default project: the call's project, else the session's, else null (unbound).</summary>
    public ProjectInfo? ProjectOfTool(ToolContext ctx)
    {
        if (ctx.Project is not null) return ctx.Project;
        try
        {
            var store = sessions();
            var s = store?.GetSession(ctx.SessionId);
            return s?.ProjectId is { } pid ? store!.GetProject(pid) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolve a project reference — a project id or a case-insensitive name; empty, "global" or "none" mean unbound.
    /// An unknown reference gives <see cref="IdeaInputException"/> listing the known projects.
    /// </summary>
    public ProjectInfo? ResolveRef(string? reference)
    {
        var v = (reference ?? "").Trim();
        if (v.Length == 0 || v is "global" or "none" or "unbound") return null;
        var store = sessions();
        if (store?.GetProject(v) is { } byId) return byId;
        var projects = store?.ListProjects() ?? [];
        if (projects.FirstOrDefault(p => string.Equals(p.Name, v, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        var known = projects.Select(p => p.Name).ToList();
        throw new IdeaInputException($"Unknown project '{v}'. Known projects: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
    }

    /// <summary>Resolve a patch's "project" value (a bare id/name string) to <c>{ id, name }</c> or null; objects and nulls pass through.</summary>
    public void NormalizeProject(JsonObject patch)
    {
        if (patch["project"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0)
        {
            var p = ResolveRef(s);
            patch["project"] = p is null ? null : new JsonObject { ["id"] = p.Id, ["name"] = p.Name };
        }
    }
}

/// <summary>
/// The agent's tool: <c>ideas</c> with an action (list, get, add, update), one schema in every request instead of
/// five. It works on the backlog in the app's database; new ideas are stamped with the session's project by default,
/// and the <c>project</c> argument (or the update's <c>project</c> patch field) changes that. Deleting is left to the
/// user, in the Ideas tab.
/// </summary>
public sealed class IdeasTool(IdeasRepository repo, IdeasLocator locator) : IAgentTool
{
    private readonly IdeasRepository _repo = repo;
    private readonly IdeasLocator _locator = locator;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "ideas",
        Label = "Ideas",
        Category = "ideas",
        SummaryArg = "action",
        Description = "The ideas backlog (the user sees it in the Ideas tab): list, get, add or update ideas.",
        Help =
            "The user's backlog of ideas, kept in NetPI's own database; ideas carry a project. Deleting is up to the user.\n" +
            "- list: the open ideas of the session's project plus the unbound \"global\" ones (id, status, priority, title, summary, " +
            "tags). Filters: status (open, parked, planned, in-progress, done, rejected; also active = not done or rejected, the " +
            "default, or all), tag, query (words that must all appear in the title, summary, tags or sections), project.\n" +
            "- get {id}: one idea in full, with its section ids (sec-…).\n" +
            "- add {title (short, specific), summary (one or two sentences: what and why), priority (default medium), tags, sections}: " +
            "sections are the details, each {kind, title, content (Markdown)}; kinds: research, plan, requirements, design, decision, " +
            "blocker, links, todo, note.\n" +
            "The user reads the Ideas tab as a list of titles (one line each, grouped by status), so the title has to carry the idea " +
            "on its own: short, specific, no filler. Everything else belongs in the summary or a section.\n" +
            "- update {id, and what changes}: status, title, summary, priority, tags, project, addSections, updateSections " +
            "({id, title?, content? (replaces), kind?}), removeSectionIds.\n" +
            "project: list: the session's project by default, \"all\" for every project, \"global\" for the unbound ones, or a " +
            "project id/name. add: the session's project by default, \"global\" (or empty) for unbound. update: reassigns the " +
            "idea; \"global\"/null unbinds it.",
        PromptGuidelines =
        [
            "Record research and plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), " +
            "and look at the open ideas (action list) before larger work. When you finish the work an idea describes, set it to done (action update). " +
            "Title the idea so it makes sense on its own on one line: the user scans titles, not summaries.",
        ],
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = Enum(["list", "get", "add", "update"]),
                ["id"] = Str(),
                ["project"] = Str(),
                ["title"] = Str(),
                ["summary"] = Str(),
                ["status"] = Str(),
                ["priority"] = Enum(IdeaOps.Priorities),
                ["tags"] = Strings(),
                ["tag"] = Str(),
                ["query"] = Str(),
                // The section shapes are in the schema itself: a model that never asks for the manual still sees them
                // (without them a local agent went looking for the ideas file by hand).
                ["sections"] = Sections("add: the idea's details"),
                ["addSections"] = Sections("update: new sections"),
                ["updateSections"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "update: change sections by id (get shows them as [sec-…]); content replaces the old text",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["id"] = Str(), ["title"] = Str(), ["content"] = Str(), ["kind"] = Enum(IdeaOps.Kinds) },
                        ["required"] = new JsonArray("id"),
                    },
                },
                ["removeSectionIds"] = Strings(),
            },
            ["required"] = new JsonArray("action"),
        },
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var a = Args(args);
            return Action(a) switch
            {
                "list" => await ListAsync(context, a, ct).ConfigureAwait(false),
                "get" => await GetAsync(a, ct).ConfigureAwait(false),
                "add" => await AddAsync(context, a, ct).ConfigureAwait(false),
                "update" => await UpdateAsync(context, a, ct).ConfigureAwait(false),
                "delete" => ToolResult.Error("Deleting an idea is up to the user, in the Ideas tab. Set it to done or rejected instead (action update)."),
                var other => ToolResult.Error($"Unknown action \"{other}\": use list, get, add or update."),
            };
        }
        catch (IdeaInputException ex) { return ToolResult.Error(ex.Message); }
        catch (IdeasConflictException ex) { return ToolResult.Error(ex.Message); }
        catch (RpcException ex) when (ex.Code == "not_found") { return ToolResult.Error(ex.Message); }
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
        string[] changes = ["title", "summary", "status", "priority", "tags", "project", "addSections", "add_sections", "updateSections", "update_sections", "removeSectionIds", "remove_section_ids"];
        if (hasId) return changes.Any(args.ContainsKey) ? "update" : "get";
        return IdeaOps.Str(args, "title") is { Length: > 0 } ? "add" : "list";
    }

    private Task<ToolResult> AddAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The project argument overrides the session's project (which is the default stamp).
        var stamp = IdeaOps.Has(args, "project") ? _locator.ResolveRef(IdeaOps.Str(args, "project")) : _locator.ProjectOfTool(context);
        var idea = _repo.Add(Build(context, args, stamp));
        var n = (idea.Doc["sections"] as JsonArray)?.Count ?? 0;
        return Task.FromResult(ToolResult.Ok($"Added idea {IdeaOps.Str(idea.Doc["id"])}: {IdeaOps.Str(idea.Doc["title"])}" +
            (n > 0 ? $" ({n} section{(n == 1 ? "" : "s")})" : "") +
            $" to the ideas backlog (project {IdeaOps.ProjectLabel(idea.Doc)})", Details(idea.Doc)));
    }

    /// <summary>The new idea, built the way every creator builds one (the tool is not special).</summary>
    private JsonObject Build(ToolContext context, JsonObject args, ProjectInfo? stamp)
    {
        var created = IdeaOps.CreateIdea(args, _repo.TakenIds(), "agent:" + context.AgentId, context.SessionId);
        IdeaOps.SetProject(created, stamp?.Id, stamp?.Name);
        return created;
    }

    private Task<ToolResult> ListAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = IdeaOps.Str(args, "status");
        var tag = IdeaOps.Str(args, "tag");
        var query = IdeaOps.Str(args, "query", "search");

        // The scope: no argument → the session's project plus the unbound ones (or only the unbound ones without a project);
        // "all" → everything; "global"/"none" → the unbound ones; a project id/name → that project only.
        var all = false;
        var unboundOnly = false;
        var includeUnbound = false;
        ProjectInfo? scopeInfo = null;
        var refStr = IdeaOps.Has(args, "project") ? IdeaOps.Str(args, "project")?.Trim() : null;
        if (refStr is null)
        {
            scopeInfo = _locator.ProjectOfTool(context);
            if (scopeInfo is null) unboundOnly = true; else includeUnbound = true;
        }
        else if (refStr is "all" or "*") all = true;
        else if (refStr is "global" or "none") unboundOnly = true;
        else
        {
            scopeInfo = _locator.ResolveRef(refStr); // unknown projects give an error listing the known ones
            if (scopeInfo is null) unboundOnly = true;
        }
        var scopeId = scopeInfo?.Id;

        // The whole backlog in scope (the "hidden" line below is about ideas that are done or rejected), not only the
        // open ones — the checks read the open ones, this is the user's list.
        var inScope = (all ? _repo.All()
                : _repo.All().Where(i => unboundOnly ? IdeaOps.ProjectOf(i) is null : IdeaOps.MatchesProject(i, scopeId, includeUnbound)))
            .ToList();
        var effective = string.IsNullOrWhiteSpace(status) ? "active" : status;
        var matched = inScope.Where(i => IdeaOps.Matches(i, effective, tag, query)).ToList();
        var hiddenCount = string.IsNullOrWhiteSpace(status)
            ? inScope.Count(i => IdeaOps.Matches(i, "all", tag, query)) - matched.Count
            : 0;
        string? Label(JsonObject i) => all ? IdeaOps.ProjectLabel(i) : (IdeaOps.ProjectOf(i) is null && includeUnbound ? "global" : null);
        var lines = matched.Select(i => IdeaOps.ListLine(i, Label(i))).ToList();
        var unboundShown = matched.Count(i => IdeaOps.ProjectOf(i) is null);

        var where = all ? "all projects" : scopeInfo is null ? "the global backlog" : $"project \"{scopeInfo.Name}\"";
        if (inScope.Count == 0 && unboundShown == 0)
            return Task.FromResult(ToolResult.Ok($"No ideas yet in {where}.", new JsonObject { ["project"] = scopeInfo?.Name, ["count"] = 0, ["total"] = 0 }));
        var head = lines.Count == 0
            ? $"No matching ideas in {where} ({inScope.Count} total)."
            : lines.Count == unboundShown && unboundShown > 0
                ? $"{lines.Count} global ideas:"
                : $"{lines.Count} idea{(lines.Count == 1 ? "" : "s")} in {where}{(includeUnbound && unboundShown > 0 ? $" ({unboundShown} global)" : "")}:";
        var text = head + (lines.Count > 0 ? "\n" + string.Join('\n', lines) : "");
        if (hiddenCount > 0) text += $"\n({hiddenCount} done/rejected hidden; pass status \"all\" to include them.)";
        return Task.FromResult(ToolResult.Ok(text, new JsonObject { ["project"] = scopeInfo?.Name, ["count"] = lines.Count, ["total"] = inScope.Count }));
    }

    private Task<ToolResult> GetAsync(JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = RequireId(args);
        var found = _repo.Find(id) ?? throw NotFound(id);
        var text = IdeaOps.RenderMarkdown(found.Doc);
        if ((found.Doc["sections"] as JsonArray)?.Count > 0)
            text += "\n(To change a section: action update with updateSections [{\"id\": \"sec-…\", \"content\": \"…\"}]; " +
                    "to add one: addSections [{\"kind\": \"note\", \"title\": \"…\", \"content\": \"…\"}].)\n";
        return Task.FromResult(ToolResult.Ok(text, Details(found.Doc)));
    }

    private Task<ToolResult> UpdateAsync(ToolContext context, JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = RequireId(args);
        var patch = (JsonObject)args.DeepClone();
        foreach (var k in new[] { "id", "ideaId", "action", "op", "operation", "command", "mode" }) patch.Remove(k);
        _locator.NormalizeProject(patch); // "project": a bare id/name → { id, name } or null (unknown projects are errors)
        var (idea, _, changes) = _repo.Update(id, patch, fromUi: false, context.SessionId);
        var text = changes.Count == 0
            ? $"No changes to {IdeaOps.Str(idea["id"])} (values were already set)."
            : $"Updated {IdeaOps.Str(idea["id"])} ({IdeaOps.Str(idea["title"])}): {string.Join(", ", changes)}.";
        return Task.FromResult(ToolResult.Ok(text, Details(idea)));
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

    /// <summary>
    /// UI data for the tool result: the idea as it is stored, carrying the revision (an editor submits the revision it
    /// read, and a stale one is a conflict rather than an overwrite).
    /// </summary>
    private static JsonObject Details(JsonObject? idea) => new()
    {
        ["project"] = idea is null ? null : idea["project"]?.DeepClone(),
        ["idea"] = idea,
    };

    private static IdeaInputException NotFound(string id) => new($"No idea with id '{id}'. The list action shows the ids.");

    private static JsonObject Enum(string[] values) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()),
    };

    private static JsonObject Str() => new() { ["type"] = "string" };

    private static JsonObject Sections(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["kind"] = Enum(IdeaOps.Kinds), ["title"] = Str(), ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Markdown" } },
        },
    };

    private static JsonObject Strings() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
}
