using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The ideas backlog: the plugin's own collections in the store (the <c>netpi.ideas</c> scope), every idea carrying a
/// <c>project</c>. The agent tool <c>ideas</c> (list, get, add, update) stamps new ideas with the session's project by
/// default and reaches other projects through its <c>project</c> argument; the ideas.* RPC serves the "Ideas" tab
/// (which also deletes); <c>/idea</c> quick-adds. A card the checks leave for the user is answered in one transaction,
/// and the answer is remembered so a retry or a second window cannot save the same idea twice.
/// <para>
/// The JSON files of the earlier versions (<c>ideas.json</c>, <c>ideas-pending.json</c>, <c>ideas-migration.json</c>)
/// are neither read nor written by this build any more: <c>ideas.fileName</c> is the name that file had, kept so an
/// older UI still has a hint.
/// </para>
/// </summary>
[NetPiPlugin("netpi.ideas", Name = "Ideas", Description = "Backlog of ideas, research and plans (in NetPI's database, ideas carry a project)", Order = 80)]
public sealed class IdeasPlugin : INetPiPlugin
{
    /// <summary>The event a window listens for so it re-reads the backlog (published after a commit, never before).</summary>
    public const string ChangedEvent = "ideas.changed";

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var work = new IdeaWork(context);
        context.Services.Register<IBackgroundWork>(work);
        context.Rpc.RegisterReadOnly("ideas.work", (_, _) => Task.FromResult<object?>(work.Snapshot()), "Background idea checks: waits, running work and recent outcomes with reasons");
        context.Rpc.RegisterReadOnly("ideas.capabilities", (_, _) => Task.FromResult<object?>(new
        {
            decisions = DecisionCapabilities.Available(context.Services, context.Rpc, "decide.decision"),
            history = context.Services.Get<IGitHistory>() is not null || context.Rpc.Exists("files.commits"),
            models = context.Models.Cached.Count > 0,
        }), "Availability of optional Ideas enhancements, independently of backlog storage");
        context.Services.Register(new SettingsSection
        {
            Id = "ideas", Title = "Ideas", Group = "Tools", Order = 60,
            Settings =
            [
                SettingInfo.Str("ideas.fileName", "Legacy ideas file name", "ideas.json",
                    "The name the ideas file had before the backlog moved into the store. It is reported by ideas.list and ideas.changed so an older UI still has a hint. The backlog is not written there any more."),
                SettingInfo.Bool("ideas.recall", "Suggest a matching idea", true,
                    "While the first message of a chat is typed, a decision looks for the open idea it continues and offers to add it to the chat (needs the Decide plugin)."),
                SettingInfo.Number("ideas.recallThreshold", "Suggestion threshold", IdeaRecall.DefaultThreshold,
                    "The decision's probability an idea needs before it is suggested. 0.8 gave no false suggestion on 56 unrelated messages (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Bool("ideas.saveCheck", "Offer unsaved plans when a chat closes", true,
                    "When a chat tab is closed, the model says whether it leaves a plan nobody built or wrote down. A new plan gets a card to save or discard; work on an open idea is attached to it instead."),
                SettingInfo.Bool("ideas.verify", "Verify automatic proposals", true, "A read-only low-priority worker verifies proposals before they are shown or applied. Disabling this stops automatic proposals."),
                SettingInfo.Str("ideas.verifyModel", "Verifier model", "", "Empty uses ideas.model. The verifier yields to higher-priority queued work after its provider acknowledges cancellation."),
                SettingInfo.Bool("ideas.applyVerifiedUpdates", "Apply verified completion updates", true, "Mark an idea done after independent verification, only if its revision is unchanged and its project is idle."),
                SettingInfo.Number("ideas.attachThreshold", "Attach threshold", IdeaSaveCheck.DefaultAttachThreshold,
                    "The probability a closed chat has to be about an open idea before the chat is attached to it. 0.8 was right on 5 of 6 (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Str("ideas.model", "Model for the idea checks", IdeaRecall.DefaultModel,
                    "The decision model (through the Decide plugin's server) and the model that drafts the save check. qwen3.8-27b, the NInfer chat model, was measured."),
                SettingInfo.Bool("ideas.allowPaidModel", "Let the automatic checks use a paid model", false,
                    "Off: a check that would run on a cloud model is skipped, because an invoice for a background check is never what you meant. On: the same model as above, local or not."),
                SettingInfo.Int("ideas.checkWaitSeconds", "Check queue wait", IdeaAdmission.DefaultWaitSeconds,
                    "How long a background check (the save check, the commit sweep) waits for a slot on the model before it is dropped instead of run without one. A drop is never silent: the save check's mark stays retryable, the commit sweep tries that commit again, and the log says what was dropped and why.", 1, 300),
                SettingInfo.Int("ideas.commitRetrySeconds", "Commit check retry", IdeaCommitCheck.DefaultRetrySeconds,
                    "How long a repository whose commit check failed waits before the check is tried again. The wait doubles with every failed attempt and caps at an hour; after five failed attempts in a row the commit is recorded unread (ideas.unread) and the cursor moves past it, so later commits are read.", 0, 86400),
                SettingInfo.Bool("ideas.closeOnCommit", "Notice when a commit finishes an idea", true,
                    "Every project with a git repository is watched. A commit is recorded on the open idea it works on, and when a commit may have finished one you get a card to mark it done (needs the Files and Decide plugins)."),
                SettingInfo.Number("ideas.linkThreshold", "Link threshold", IdeaCommitCheck.DefaultLinkThreshold,
                    "The probability a commit has to be about an open idea before the commit is recorded on it. 0.7 linked no wrong idea in 187 commits (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Number("ideas.doneThreshold", "Finished threshold", IdeaCommitCheck.DefaultDoneThreshold,
                    "The probability an idea has to be finished before you are offered. 0.8 offered 4 of 5 finished ideas and nothing that was only advanced.", 0.3, 0.99),
                SettingInfo.Bool("ideas.tellAgentOnCommit", "Tell the agent to close its own idea on a commit", true,
                    "After the agent itself runs a successful git commit or git merge in the session's project, one notice asks it to mark the idea that commit finished (or to say that none of them is about it). Nothing happens when the project has no open idea. The cards above stay: they are what catches a commit made outside any chat."),
                SettingInfo.Int("ideas.commitNoticesPerRun", "Commit notices per run", IdeaCommitNoticeHook.DefaultMaxPerRun,
                    "How many of those notices one run may get, so a run that commits in a loop is asked a bounded number of times.", 0, 10),
            ],
        });

        var repo = IdeasRepository.Open(context.Data, context.Services.Get<IStorageAccess>(), context.Logger, context.Paths.Home);
        var locator = new IdeasLocator(() => context.Sessions, context.Paths, () => context.Settings);
        var snapshots = new IdeaSnapshots(repo);

        // A claim nobody owns any more (NetPI was stopped mid-check) is retryable again, before anything asks.
        try { repo.RecoverExpiredClaims(); } catch (Exception ex) { context.Logger.LogDebug("Ideas: the check claims could not be recovered: {Message}", ex.Message); }

        // Every write announces itself once it has committed, so a second window re-reads the canonical state.
        var events = new IdeasEvents(context, repo, locator);
        repo.OnChanged = events.Changed;

        // The commits the sweep could not decide after its bound of attempts (idea-kooctc): the cursor has moved past
        // them, and the record is what is re-read on demand.
        context.Rpc.RegisterReadOnly("ideas.unread", (_, _) => Task.FromResult<object?>(
            new JsonArray(repo.Unread().Select(u => (JsonNode)u).ToArray())),
            "The commits the commit sweep could not decide after its bound of attempts: { } → [{ repo, hash, subject, tries, error, at }]. The cursor has moved past them, so later commits are read; the record is what is re-read on demand");

        context.Tools.Register(new IdeasTool(repo, locator));

        var rpc = new IdeasRpc(repo, locator, events, snapshots, context.Paths.Home);
        rpc.Register(context.Rpc);
        context.Rpc.Register("ideas.verifyUpdate", async (request, token) =>
        {
            var id = request.Required("id");
            var current = repo.Find(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            var body = NetPiJson.ToNode(request.Params) as JsonObject ?? new();
            if (body["expectedRevision"]?.GetValue<long>() != current.Revision) throw new IdeasConflictException("Read the current idea and supply its expectedRevision");
            var patch = body["patch"] as JsonObject ?? throw new RpcException("bad_request", "Supply a patch");
            var evidence = request.Required("evidence");
            var projectId = current.Doc["project"]?["id"]?.GetValue<string>();
            if (IdeaRuns.ProjectBusy(context, projectId)) return new { applied = false, reason = "Project has active work" };
            var verdict = await new IdeaVerifier(context).VerifyAsync("Idea:\n" + IdeaOps.RenderMarkdown(current.Doc) + "\nProposed update:\n" + patch.ToJsonString(), evidence, null, projectId, token).ConfigureAwait(false);
            if (!verdict.Verified || IdeaRuns.ProjectBusy(context, projectId)) return new { applied = false, reason = verdict.Verified ? "Project became busy" : verdict.Reason };
            var changed = repo.Update(id, patch, fromUi: true, expectedRevision: current.Revision);
            return new { applied = true, reason = verdict.Reason, idea = changed.Doc };
        }, "Verify a proposed update against evidence and apply only to the captured revision: {id,expectedRevision,patch,evidence}; conflicting or unverifiable updates remain unapplied");
        new IdeaRecall(context, repo, locator).Register(context.Rpc);
        var saveCheck = new IdeaSaveCheck(context, repo);
        saveCheck.Register(context.Rpc);
        // Phase 3: watch the projects' repositories. Started after the tab, and it never blocks the start: a project
        // that cannot be watched is retried on the next rescan.
        var commitCheck = context.Track(new IdeaCommitCheck(context, repo, saveCheck));
        _ = commitCheck.StartAsync();

        // The other half of "close on commit": the check above watches repositories, this one listens to the run that
        // made the commit. It is a notice, so the agent (which knows what it just committed) updates the idea itself.
        context.Services.Register<IAgentHook>(new IdeaCommitNoticeHook(
            () => context.Settings,
            projectId => repo.OpenIdeas(projectId)));

        context.Ui.AddTab(new UiTabInfo { Id = "ideas", Title = "Ideas", Panel = UiPanel.Right, Icon = "idea", Order = 20, Module = "ui.js" });
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "idea", Description = "Add an idea to the backlog", ArgsHint = "<title>", Rpc = "ideas.quickAdd",
        });
    }
}

/// <summary>
/// The plugin's announcements, published after a commit: <c>ideas.changed</c> for the backlog (every window re-reads the
/// canonical list) and the two card events the composer and the tab already listen for. It is a notification, not a
/// guarantee: a window that misses one gets the same state from the next read, and a reconnecting window always does.
/// </summary>
public sealed class IdeasEvents(IPluginContext ctx, IdeasRepository repo, IdeasLocator locator)
{
    private readonly IPluginContext _ctx = ctx;
    private readonly IdeasRepository _repo = repo;
    private readonly IdeasLocator _locator = locator;

    /// <summary>The backlog changed. <c>file</c> is kept for older listeners: the legacy file name, no longer a live file.</summary>
    public void Changed(string? reason = null)
    {
        var data = _repo.Storage().DeepClone().AsObject();
        data["file"] = _locator.FileName();
        if (reason is { Length: > 0 }) data["reason"] = reason;
        _ctx.Events.Publish(IdeasPlugin.ChangedEvent, data);
    }
}

/// <summary>ideas.* RPC handlers (see docs/PLUGIN-IDEAS.md).</summary>
public sealed class IdeasRpc(IdeasRepository repo, IdeasLocator locator, IdeasEvents events, IdeaSnapshots snapshots, string home)
{
    private readonly IdeasRepository _repo = repo;
    private readonly IdeasLocator _locator = locator;
    private readonly IdeasEvents _events = events;
    private readonly IdeaSnapshots _snapshots = snapshots;
    private readonly string _home = home;

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.list", List, "The ideas backlog: { } → { storage: { backend, database, scope, schemaVersion }, ideas: [...], file?, fileName? (legacy hints) }");
        rpc.Register("ideas.get", Get, "{ id } → idea (with its revision)");
        rpc.Register("ideas.add", Add, "{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections?, images? }, prepend? } → idea; stamped with projectId (a project id or name, \"global\" for unbound), else the session's project");
        rpc.Register("ideas.addImage", Attach,
            "Store an image for an idea and return its reference: { data (base64), mediaType, name? } → { path, name, mediaType, bytes }. " +
            $"The file lands in {IdeaImages.Dir} under the home and the idea keeps the reference; at most {IdeaImages.MaxPerIdea} per idea, {IdeaImages.MaxBytes / (1024 * 1024)} MB each");
        rpc.Register("ideas.removeImage", Detach, "Delete a stored idea image: { path } → true (only files this host wrote)");
        rpc.Register("ideas.image", Image,
            "Read a stored idea image back for display: { path } → { path, name, mediaType, bytes, data (base64) }. Only files under " +
            $"{IdeaImages.Dir}; the card fetches one when it opens, so a backlog of ideas carries no image bytes");
        rpc.Register("ideas.update", Update,
            "{ id, patch, expectedRevision?, expectedUpdatedAt? } → idea; patch.project: a project id/name or { id, name? } rebinds, null (or \"global\") unbinds; " +
            "expectedRevision: refused with \"conflict\" when the idea changed since it was read (expectedUpdatedAt is the older, second-precision form of the same check)");
        rpc.Register("ideas.delete", Delete, "{ id } → true");
        rpc.Register("ideas.reorder", Reorder, "{ ids: string[] } → true");
        rpc.Register("ideas.toPrompt", ToPrompt, "{ id } → markdown prompt text");
        rpc.Register("ideas.quickAdd", QuickAdd, "/idea command: { sessionId, args } → status text");
        rpc.Register("ideas.export", Export,
            "A portable snapshot of the backlog as JSON: { path?, json? } → { file?, json, validated? } — a versioned document with stable ids, the user's order and unknown fields; nothing is written to disk unless a path is given");
        rpc.Register("ideas.import", Import,
            "Take a snapshot in: { json | path, mode?: \"merge\" (default) | \"replace\" | \"validate\" } → { ideas, cards, conflicts, imported? } — merge never overwrites what is already here");
    }

    private static async Task<object?> Guard(Func<Task<object?>> body)
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
        return Task.FromResult<object?>(new JsonObject
        {
            ["storage"] = storage,
            ["ideas"] = new JsonArray(_repo.All().Select(i => (JsonNode?)i).ToArray()),
            // Legacy hints, kept so an older UI still has something to show: the file is not written any more.
            ["file"] = _locator.LegacyFile(),
            ["fileName"] = _locator.FileName(),
            ["exists"] = _repo.Count() > 0,
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
        // The idea's images belong to it: deleting the idea takes the files with it, so nothing is left behind.
        if (_repo.Find(id) is { } found) IdeaImages.DeleteAll(_home, found.Doc);
        if (!_repo.Delete(id)) throw new RpcException("not_found", $"Idea {req.Str("id")} not found");
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
        var sb = new System.Text.StringBuilder(IdeaOps.ToPrompt(idea.Doc, "the ideas backlog"));
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
        if (path is { Length: > 0 }) answer["file"] = await _snapshots.ExportToFileAsync(path, ct).ConfigureAwait(false);
        return answer;
    });

    public Task<object?> Import(RpcRequest req, CancellationToken ct) => Guard(() =>
    {
        var snapshot = req.Prop("json") is { ValueKind: JsonValueKind.Object } json
            ? JsonObject.Create(json.Clone()) as JsonObject ?? []
            : ReadSnapshot(req.Required("path"));
        var mode = (req.Str("mode") ?? "merge").Trim().ToLowerInvariant();
        if (mode == "validate") return Task.FromResult<object?>(_snapshots.ValidateSnapshot(snapshot));
        var report = _snapshots.ImportSnapshot(snapshot, mode);
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
