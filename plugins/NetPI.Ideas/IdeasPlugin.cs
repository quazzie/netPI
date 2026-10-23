using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The ideas backlog: the plugin's own collections in the store (the <c>netpi.ideas</c> scope), every idea carrying a
/// <c>project</c>. The agent tool <c>ideas</c> (list, get, add, update) stamps new ideas with the session's project by
/// default and reaches other projects through its <c>project</c> argument; the ideas.* RPC serves the "Ideas" tab
/// (which also deletes); <c>/idea</c> quick-adds. After the agent commits, a notice asks it to update the idea the commit
/// finished (<see cref="IdeaCommitNoticeHook"/>).
/// </summary>
[NetPiPlugin("netpi.ideas", Name = "Ideas", Description = "Backlog of ideas, research and plans (in NetPI's database, ideas carry a project)", Order = 80)]
public sealed class IdeasPlugin : INetPiPlugin
{
    /// <summary>The event a window listens for so it re-reads the backlog (published after a commit, never before).</summary>
    public const string ChangedEvent = "ideas.changed";

    /// <summary>The automatic refine drain (<c>ideas.autoRefine.*</c>), once the plugin has started.</summary>
    public IdeaAutoRefine? AutoRefine { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Rpc.Register(new RpcMethod("ideas.capabilities", "Availability of optional Ideas enhancements: → { embeddings } (meaning search)", ReadOnly: true, Params: []),
            (_, _) => Task.FromResult<object?>(new
        {
            embeddings = context.Services.Get<IEmbeddingService>() is { Model: not null, Available: true },
        }));
        RegisterSettings(context);

        var repo = IdeasRepository.Open(context.Data, context.Services.Get<IStorageAccess>(), context.Logger, context.Paths.Home);
        // Meaning search over the backlog (idea-61wg9p): an optional enhancement on the Embeddings plugin's service.
        var vectors = context.Track(new IdeaVectors(context, repo));
        var locator = new IdeasLocator(() => context.Sessions);
        var snapshots = new IdeaSnapshots(repo);

        // Every write announces itself once it has committed, so a second window re-reads the canonical state.
        var events = new IdeasEvents(context, repo);
        repo.OnChanged = reason =>
        {
            events.Changed(reason);
            vectors.Changed();
        };
        _ = Task.Run(() =>
        {
            try { IdeaImages.Sweep(context.Paths.Home, repo.All(), IdeaImages.SweepAge); }
            catch (Exception ex) { context.Logger.LogWarning(ex, "Sweeping the idea images failed"); }
        }, CancellationToken.None);
        vectors.Changed(); // the first pass embeds what is not indexed yet (nothing, without embeddings)

        context.Tools.Register(new IdeasTool(repo, locator, vectors));
        RegisterMeaningSearch(context, repo, vectors);

        var rpc = new IdeasRpc(repo, locator, snapshots, context.Paths.Home);
        rpc.Register(context.Rpc);
        new IdeaAttach(context, repo).Register(context.Rpc);
        var refine = new IdeaRefine(context, repo);
        refine.Register(context.Rpc);
        AutoRefine = context.Track(new IdeaAutoRefine(context, repo, refine));
        AutoRefine.Register(context.Rpc);
        AutoRefine.Start();
        RegisterCommitNotice(context, repo, vectors);
        RegisterUi(context);
        return Task.CompletedTask;
    }

    private void RegisterSettings(IPluginContext context) =>
        context.Services.Register(new SettingsSection
        {
            Id = "ideas", Title = "Ideas", Group = "Knowledge", Order = 40,
            Settings =
            [
                SettingInfo.Bool("ideas.tellAgentOnCommit", "Tell the agent to close its own idea on a commit", true,
                    "After the agent itself runs a successful git commit or git merge in the session's project, one notice asks it to mark the idea that commit finished (or to say that none of them is about it). Nothing happens when the project has no open idea.") with { Subgroup = "Commit notices", Keywords = ["git"] },
                SettingInfo.Int("ideas.commitNoticesPerRun", "Commit notices per run", IdeaCommitNoticeHook.DefaultMaxPerRun,
                    "How many of those notices one run may get, so a run that commits in a loop is asked a bounded number of times.", 0, 10) with { Subgroup = "Commit notices" },
                SettingInfo.Bool("ideas.semantic", "Meaning search", true,
                    "With an embedding model (the Embeddings plugin, embed.model): the backlog is embedded in the background, the commit notice names the open ideas closest to the commit, the ideas tool adds the closest ideas when a search finds no word match, and a new idea names the existing ones it resembles. Off, or without embeddings, the word search alone.") with { Subgroup = "Similar ideas", Keywords = ["embeddings", "duplicates"] },
                SettingInfo.Number("ideas.similarThreshold", "Similar idea threshold", IdeaVectors.DefaultSimilarThreshold,
                    "How close (cosine, card to card) an existing idea has to be before a new one is said to resemble it. 0.80 is about the top 7 % of nearest neighbours in a 231-idea backlog with bge-base; the known duplicate pair scored 0.807-0.824.", 0.5, 0.99) with { Subgroup = "Similar ideas", Advanced = true },
                SettingInfo.Number("ideas.noticeFloor", "Commit notice: similarity floor", IdeaCommitNoticeHook.DefaultNoticeFloor,
                    "The commit notice names an open idea only when it is at least this close to the commit message (embeddings).", 0, 0.99) with { Subgroup = "Commit notices", Advanced = true },
                SettingInfo.Int("ideas.commitNoticeDebounceSec", "Commit notice debounce", IdeaCommitNoticeHook.DefaultDebounceSec,
                    "Seconds a run waits after a commit notice before it may get the next one, so a burst of commits costs one notice instead of one each.", 0, 3600) with { Subgroup = "Commit notices", Advanced = true },
                SettingInfo.Bool("ideas.autoRefine.enabled", "Automatic refine", false,
                    "Your own agents refine newly filed ideas in the background (the same task as the Refine button), so each becomes a clear, checkable piece of work. Only the agents listed below run it; with none listed nothing runs. An idea is refined once: after a manual refine too.") with { Subgroup = "Automatic refine" },
                SettingInfo.List("ideas.autoRefine.agents", "Automatic refine: agents", [],
                    "The agents that may run it, taken in turn. \"Any free agent\" adds every agent whose model costs nothing, while it can take work. Empty: nothing runs. 'any' is not accepted on purpose: it could pick a paid agent.", "nothing runs")
                    with { Type = "agents", Subgroup = "Automatic refine", Keywords = ["free", "agent"] },
                SettingInfo.Int("ideas.autoRefine.maxConcurrent", "Automatic refine: at once", IdeaAutoRefine.DefaultMaxConcurrent,
                    "How many automatic refines may run at the same time. They queue behind your own chats (low priority).", 1, 4) with { Subgroup = "Automatic refine" },
                SettingInfo.Int("ideas.autoRefine.delayMinutes", "Automatic refine: delay", IdeaAutoRefine.DefaultDelayMinutes,
                    "An idea is refined only after you have not created or edited it for this long, so an idea you are still writing is left alone. 0: at once.", 0, 10080, "min") with { Subgroup = "Automatic refine" },
                SettingInfo.Bool("ideas.autoRefine.archiveWhenDone", "Automatic refine: archive the chat", true,
                    "The finished chat is archived; it stays linked on the idea's card.") with { Subgroup = "Automatic refine" },
            ],
        });

    private static void RegisterMeaningSearch(IPluginContext context, IdeasRepository repo, IdeaVectors vectors)
    {
        context.Rpc.Register(new RpcMethod("ideas.similar", "Ideas closest in meaning (embeddings): { id | text, limit? (5), status? (active), projectId? } → { available, ideas: [{ id, title, status, score }] }; available false when there are no embeddings", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("id"), RpcParam.Opt("projectId"), RpcParam.Opt("status"), RpcParam.Opt("text")]),
            async (request, token) =>
        {
            var limit = Math.Clamp(request.Int("limit") ?? 5, 1, 50);
            var id = request.Str("id");
            var source = id is { Length: > 0 } ? repo.Find(id)?.Doc ?? throw new RpcException("not_found", $"Idea {id} not found") : null;
            var text = source is not null ? IdeaVectors.Card(source) : request.Str("text") ?? throw new RpcException("bad_request", "Supply id or text");
            var status = request.Str("status") ?? "active";
            var candidates = repo.All()
                .Where(i => IdeaOps.Str(i["id"]) != IdeaOps.Str(source?["id"]) && IdeaOps.Matches(i, status, null, null))
                .Where(i => request.Str("projectId") is not { Length: > 0 } p || IdeaOps.MatchesProject(i, p, includeUnbound: true))
                .ToList();
            var near = await vectors.NearestAsync(text, candidates, limit, token,
                source is null ? EmbeddingKind.Query : EmbeddingKind.Document, cardsOnly: source is not null).ConfigureAwait(false);
            return new JsonObject
            {
                ["available"] = near is not null,
                ["ideas"] = new JsonArray((near ?? []).Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = IdeaOps.Str(s.Idea["id"]), ["title"] = IdeaOps.Str(s.Idea["title"]), ["status"] = IdeaOps.Str(s.Idea["status"]) ?? "open",
                    ["score"] = s.Score,
                }).ToArray()),
            };
        });
        context.Rpc.Register(new RpcMethod("ideas.reindex", "Bring the backlog's embeddings in step now (normally done in the background after every change): { } → { enabled, model?, indexed?, unchanged?, removed? }", ReadOnly: false, Params: []),
            async (_, token) =>
        {
            try { return await vectors.ReindexAsync(token).ConfigureAwait(false); }
            catch (EmbeddingException ex) { throw new RpcException(ex.Code, ex.Message); }
        });
    }

    /// <summary>
    /// After the agent itself commits, a notice asks it to update the idea the commit finished. With embeddings it names
    /// the open ideas closest to the commit message, above <c>ideas.noticeFloor</c>: below it an idea would be named for
    /// no better reason than being the least far.
    /// </summary>
    private static void RegisterCommitNotice(IPluginContext context, IdeasRepository repo, IdeaVectors vectors) =>
        context.Services.Register<IAgentHook>(new IdeaCommitNoticeHook(
            () => context.Settings,
            projectId => repo.OpenIdeas(projectId),
            async (text, projectId, ct) =>
            {
                var floor = Math.Clamp(context.Settings.GetOr("ideas.noticeFloor", IdeaCommitNoticeHook.DefaultNoticeFloor), 0, 0.99);
                var near = (await vectors.NearestAsync(text, repo.OpenIdeas(projectId), IdeaCommitNoticeHook.MaxNamedIdeas, ct).ConfigureAwait(false))?
                    .Where(s => s.Score >= floor).ToList();
                return near is { Count: > 0 } ? near : null;
            }));

    private void RegisterUi(IPluginContext context)
    {
        context.Ui.AddTab(new UiTabInfo { Id = "ideas", Title = "Ideas", Panel = UiPanel.Right, Icon = "idea", Order = 20, Module = "ui.js" });
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "idea", Description = "Add an idea to the backlog", ArgsHint = "<title>", Rpc = "ideas.quickAdd",
        });
    }
}

/// <summary>
/// The plugin's announcements, published after a commit: <c>ideas.changed</c> for the backlog (every window re-reads the
/// canonical list). It is a notification, not a guarantee: a window that misses one gets the same state from the next
/// read, and a reconnecting window always does.
/// </summary>
public sealed class IdeasEvents(IPluginContext ctx, IdeasRepository repo)
{
    private readonly IPluginContext _ctx = ctx;
    private readonly IdeasRepository _repo = repo;

    /// <summary>The backlog changed.</summary>
    public void Changed(string? reason = null)
    {
        var data = _repo.Storage().DeepClone().AsObject();
        if (reason is { Length: > 0 }) data["reason"] = reason;
        _ctx.Events.Publish(IdeasPlugin.ChangedEvent, data);
    }
}

/// <summary>ideas.* RPC handlers (see docs/PLUGIN-IDEAS.md).</summary>
public sealed class IdeasRpc(IdeasRepository repo, IdeasLocator locator, IdeaSnapshots snapshots, string home)
{
    private readonly IdeasRepository _repo = repo;
    private readonly IdeasLocator _locator = locator;
    private readonly IdeaSnapshots _snapshots = snapshots;
    private readonly string _home = home;

    /// <summary>How many ideas the welcome screen is offered when it asks for none.</summary>
    public const int DefaultPickLimit = 5;

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register(new RpcMethod("ideas.list", "The ideas backlog: { projectId? (an id or name; global: the unbound ones), limit? } → { storage: { backend, database, scope, schemaVersion }, ideas: [...] }", ReadOnly: true, Params: [RpcParam.Opt("projectId", RpcParamType.String, "a project's id or name; global for the unbound ideas"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            List);
        rpc.Register(new RpcMethod("ideas.picks", "Ideas to offer where a session is about to start (the welcome screen): { projectId?, limit? } → { picks: [{ id, title, summary, status, priority, projectId?, projectName?, updatedAt }] }. " +
            "Lean on purpose (no sections, images or commits) so a window can read it on every start: the target project's own " +
            $"open/planned ideas first, then the global ones, at most {IdeasRpc.DefaultPickLimit} unless a limit is asked for", ReadOnly: true, Params: [RpcParam.Opt("projectId"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            Picks);
        rpc.Register(new RpcMethod("ideas.get", "{ id } → idea (with its revision)", ReadOnly: true, Params: [RpcParam.Req("id")]),
            Get);
        rpc.Register(new RpcMethod("ideas.add", "{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections?, images? }, prepend? } → idea; stamped with projectId (a project id or name, \"global\" for unbound), else the session's project", ReadOnly: false, Params: [RpcParam.Req("idea", RpcParamType.Object, "{ title, summary?, status?, priority?, tags?, sections?, images? }"), RpcParam.Opt("sessionId"), RpcParam.Opt("projectId", RpcParamType.String, "a project's id or name; global for none"), RpcParam.Opt("prepend", RpcParamType.Boolean)]),
            Add);
        rpc.Register(new RpcMethod("ideas.addImage", "Store an image for an idea and return its reference: { data (base64), mediaType, name? } → { path, name, mediaType, bytes }. " +
            $"The file lands in {IdeaImages.Dir} under the home and the idea keeps the reference; at most {IdeaImages.MaxPerIdea} per idea, {IdeaImages.MaxBytes / (1024 * 1024)} MB each", ReadOnly: false, Params: [RpcParam.Req("data", RpcParamType.String, "base64"), RpcParam.Req("mediaType"), RpcParam.Opt("name")]),
            Attach);
        rpc.Register(new RpcMethod("ideas.removeImage", "Delete a stored idea image: { path } → true (only files this host wrote)", ReadOnly: false, Params: [RpcParam.Req("path")]),
            Detach);
        rpc.Register(new RpcMethod("ideas.image", "Read a stored idea image back for display: { path } → { path, name, mediaType, bytes, data (base64) }. Only files under " +
            $"{IdeaImages.Dir}; the card fetches one when it opens, so a backlog of ideas carries no image bytes", ReadOnly: true, Params: [RpcParam.Req("path")]),
            Image);
        rpc.Register(new RpcMethod("ideas.update", "{ id, patch, expectedRevision?, expectedUpdatedAt? } → idea; patch.project: a project id/name or { id, name? } rebinds, null (or \"global\") unbinds; " +
            "expectedRevision: refused with \"conflict\" when the idea changed since it was read (expectedUpdatedAt is the older, second-precision form of the same check)", ReadOnly: false, Params: [RpcParam.Req("id"), RpcParam.Req("patch", RpcParamType.Object, "{ status?, addSections?, ... }"), RpcParam.Opt("expectedRevision", RpcParamType.Integer), RpcParam.Opt("expectedUpdatedAt")]),
            Update);
        rpc.Register(new RpcMethod("ideas.delete", "{ id } → true", ReadOnly: false, Params: [RpcParam.Req("id")]),
            Delete);
        rpc.Register(new RpcMethod("ideas.reorder", "{ ids: string[] } → true", ReadOnly: false, Params: [RpcParam.Req("ids", RpcParamType.Array)]),
            Reorder);
        rpc.Register(new RpcMethod("ideas.toPrompt", "{ id } → markdown prompt text", ReadOnly: true, Params: [RpcParam.Req("id")]),
            ToPrompt);
        rpc.Register(new RpcMethod("ideas.quickAdd", "/idea command: { sessionId, args } → status text", ReadOnly: false, Params: [RpcParam.Opt("sessionId"), RpcParam.Opt("args", RpcParamType.String, "/idea's text: the title"), RpcParam.Opt("projectId")]),
            QuickAdd);
        rpc.Register(new RpcMethod("ideas.export", "A portable snapshot of the backlog as JSON: { path?, json? } → { file?, json, validated? } — a versioned document with stable ids, the user's order and unknown fields; nothing is written to disk unless a path is given", ReadOnly: false, Params: [RpcParam.Opt("path")]),
            Export);
        rpc.Register(new RpcMethod("ideas.import", "Take a snapshot in: { json | path, mode?: \"merge\" (default) | \"replace\" | \"validate\" } → { ideas, cards, conflicts, imported? } — merge never overwrites what is already here", ReadOnly: false, Params: [RpcParam.Opt("json", RpcParamType.Object), RpcParam.Opt("path"), RpcParam.Opt("mode", RpcParamType.String, "merge (default) or replace")]),
            Import);
    }

    /// <summary>The repository's exceptions as RPC codes: a storage failure or a bad input is the caller's answer, not a crash.</summary>
    internal static async Task<object?> Guard(Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (IdeaInputException ex) { throw new RpcException("bad_request", ex.Message); }
        catch (IdeasConflictException ex) { throw new RpcException("conflict", ex.Message); }
        catch (RpcException) { throw; }
        catch (IOException ex) { throw new RpcException("io_error", ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new RpcException("io_error", ex.Message); }
    }

    /// <summary>The stamp of a new idea: an explicit projectId (id, name or "global"), else the session's project.</summary>
    private ProjectInfo? ResolveStamp(RpcRequest req)
    {
        var sessionId = req.Str("sessionId");
        var reference = req.Str("projectId");
        if (!string.IsNullOrWhiteSpace(reference))
        {
            _locator.ProjectOfSession(sessionId); // validate the session even when the project is given explicitly
            return _locator.ResolveRef(reference);
        }
        return _locator.ProjectOfSession(sessionId);
    }

    public Task<object?> List(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var storage = _repo.Storage();
        // projectId (an id or a name; "global" for the unbound ideas) and limit narrow the list: a script passed them for a
        // long time and got every project's ideas back, which a declared method now refuses rather than ignores (idea-yvcy8b)
        IEnumerable<JsonObject> ideas = _repo.All();
        if (req.Str("projectId") is { Length: > 0 } project)
        {
            var global = project.Equals("global", StringComparison.OrdinalIgnoreCase);
            var resolved = global ? null : _locator.ResolveRef(project) ?? throw new RpcException("not_found", $"No project '{project}'.");
            ideas = ideas.Where(i => i["project"]?["id"]?.GetValue<string>() is { } id ? !global && id == resolved!.Id : global);
        }
        if (req.Int("limit") is { } limit) ideas = ideas.Take(Math.Max(0, limit));
        return Task.FromResult<object?>(new JsonObject
        {
            ["storage"] = storage,
            ["ideas"] = new JsonArray(ideas.Select(i => (JsonNode?)i).ToArray()),
        });
    });

    public Task<object?> Picks(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var projectId = req.Str("projectId"); // an id, as the welcome screen has it; an unknown one simply matches nothing
        var limit = Math.Clamp(req.Int("limit") ?? DefaultPickLimit, 1, 24);
        return Task.FromResult<object?>(new JsonObject
        {
            ["picks"] = new JsonArray(IdeaOps.Picks(_repo.All(), projectId, limit).Select(i => (JsonNode?)i).ToArray()),
        });
    });

    public Task<object?> Get(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var id = req.Required("id");
        return Task.FromResult<object?>(_repo.Find(id)?.Doc ?? throw new RpcException("not_found", $"Idea {id} not found"));
    });

    public Task<object?> Add(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var input = ObjectParam(req, "idea");
        var stamp = ResolveStamp(req);
        var sessionId = req.Str("sessionId");
        var created = IdeaOps.CreateIdea(input, _repo.TakenIds(), "user", sessionId, keepExtraFields: true);
        IdeaOps.SetProject(created, stamp?.Id, stamp?.Name);
        var idea = _repo.Add(created, prepend: req.Bool("prepend") == true);
        return Task.FromResult<object?>(idea.Doc);
    });

    public Task<object?> Update(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var id = req.Required("id");
        var patch = ObjectParam(req, "patch");
        _locator.NormalizeProject(patch); // a bare project id/name in the patch → { id, name } (unknown: an error)
        // What the editor had when it opened the card. Something else wrote the idea since (another window, the agent)
        // and the change is refused instead of silently overwriting it. expectedRevision is the check; the
        // expectedUpdatedAt that came with it compares a second-precision timestamp and is kept for older callers.
        var expectedRevision = req.Prop("expectedRevision") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var rev) ? rev : (long?)null;
        var (idea, _, _) = _repo.Update(id, patch, fromUi: true, expectedRevision: expectedRevision, expectedUpdatedAt: req.Str("expectedUpdatedAt"));
        return Task.FromResult<object?>(idea);
    });

    public Task<object?> Delete(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var id = req.Required("id");
        var found = _repo.Find(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
        // The idea's images belong to it: deleting the idea takes the files with it, so nothing is left behind —
        // and only once the idea itself is gone, so a delete that does not land leaves its images with it.
        if (!_repo.Delete(id)) throw new RpcException("not_found", $"Idea {id} not found");
        IdeaImages.DeleteAll(_home, found.Doc);
        IdeaImages.Sweep(_home, _repo.All(), IdeaImages.SweepAge);   // files an attach left behind when its idea never landed
        return Task.FromResult<object?>(true);
    });

    public Task<object?> Attach(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var data = req.Required("data");
        var mediaType = req.Required("mediaType");
        return Task.FromResult<object?>(IdeaImages.Attach(_home, data, mediaType, req.Str("name")));
    });

    public Task<object?> Detach(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        IdeaImages.Delete(_home, req.Required("path"));
        return Task.FromResult<object?>(true);
    });

    public Task<object?> Image(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var rel = req.Required("path");
        var file = IdeaImages.Absolute(_home, rel) ?? throw new RpcException("bad_request", "That is not a stored idea image.");
        if (!File.Exists(file)) throw new RpcException("not_found", "The image is gone from disk.");
        return Task.FromResult<object?>(new JsonObject
        {
            ["path"] = rel,
            ["mediaType"] = IdeaImages.TypeFor(file),
            ["bytes"] = new FileInfo(file).Length,
            ["data"] = Convert.ToBase64String(File.ReadAllBytes(file)),
        });
    });

    public Task<object?> Reorder(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ids = req.Prop("ids") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : throw new RpcException("bad_request", "Missing array parameter 'ids'");
        _repo.Reorder(ids);
        return Task.FromResult<object?>(true);
    });

    public Task<object?> ToPrompt(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var id = req.Required("id");
        var idea = _repo.Find(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
        var sb = new System.Text.StringBuilder(IdeaOps.ToPrompt(idea.Doc));
        IdeaImages.AppendTo(sb, _home, idea.Doc);
        return Task.FromResult<object?>(sb.ToString().TrimEnd());
    });

    public Task<object?> QuickAdd(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        ct.ThrowIfCancellationRequested();
        var title = req.Str("args")?.Trim();
        if (string.IsNullOrEmpty(title)) throw new RpcException("bad_request", "Usage: /idea <title>");
        var stamp = ResolveStamp(req);
        var sessionId = req.Str("sessionId");
        var created = IdeaOps.CreateIdea(new JsonObject { ["title"] = title }, _repo.TakenIds(), "user", sessionId);
        IdeaOps.SetProject(created, stamp?.Id, stamp?.Name);
        var idea = _repo.Add(created);
        return Task.FromResult<object?>(new JsonObject
        {
            ["id"] = IdeaOps.Str(idea.Doc["id"]),
            ["text"] = $"Added {IdeaOps.Str(idea.Doc["id"])}: {title} (project {IdeaOps.ProjectLabel(idea.Doc)})",
        });
    });

    public Task<object?> Export(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var snapshot = _snapshots.Export();
        var path = req.Str("path");
        var answer = new JsonObject { ["json"] = snapshot };
        if (path is { Length: > 0 }) answer["file"] = await _snapshots.WriteToFileAsync(snapshot, path, ct).ConfigureAwait(false);
        return answer;
    });

    public Task<object?> Import(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        var snapshot = req.Prop("json") is { ValueKind: JsonValueKind.Object } json
            ? JsonObject.Create(json.Clone()) as JsonObject ?? []
            : ReadSnapshot(req.Required("path"));
        var mode = (req.Str("mode") ?? "merge").Trim().ToLowerInvariant();
        if (mode == "validate") return Task.FromResult<object?>(_snapshots.ValidateSnapshot(snapshot));
        var report = _snapshots.ImportSnapshot(snapshot, mode, _home);
        return Task.FromResult<object?>(report);
    });

    private static JsonObject ReadSnapshot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new RpcException("not_found", $"{full} does not exist.");
        try
        {
            return JsonNode.Parse(File.ReadAllText(full), documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                   ?? throw new RpcException("bad_request", $"{full} must contain a JSON object.");
        }
        catch (System.Text.Json.JsonException ex) { throw new RpcException("bad_request", $"{full} is not valid JSON ({ex.Message})."); }
    }

    private static JsonObject ObjectParam(RpcRequest req, string name) => req.Prop(name) is { ValueKind: JsonValueKind.Object } o
        ? JsonObject.Create(o.Clone()) as JsonObject ?? []
        : throw new RpcException("bad_request", $"Missing object parameter '{name}'");
}
