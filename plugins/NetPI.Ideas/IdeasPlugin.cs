using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The ideas backlog: one global file (<c>~/.netpi/ideas.json</c>), every idea carrying a <c>project</c> property.
/// The agent tool <c>ideas</c> (list, get, add, update) stamps new ideas with the session's project by default and
/// reaches other projects through its <c>project</c> argument; the ideas.* RPC serves the "Ideas" tab (which also
/// deletes); <c>/idea</c> quick-adds. At start, the per-project files of earlier versions are merged into the global
/// one. Setting: <c>ideas.fileName</c> (default "ideas.json"). Event: <c>ideas.changed { file }</c>.
/// </summary>
[NetPiPlugin("netpi.ideas", Name = "Ideas", Description = "Backlog of ideas, research and plans (one global file, ideas carry a project)", Order = 80)]
public sealed class IdeasPlugin : INetPiPlugin
{
    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "ideas", Title = "Ideas", Group = "Tools", Order = 60,
            Settings =
            [
                SettingInfo.Str("ideas.fileName", "Ideas file", "ideas.json", "The global ideas file in ~/.netpi (the single backlog of all projects)."),
                SettingInfo.Bool("ideas.recall", "Suggest a matching idea", true,
                    "While the first message of a chat is typed, a decision looks for the open idea it continues and offers to add it to the chat (needs the Decide plugin)."),
                SettingInfo.Number("ideas.recallThreshold", "Suggestion threshold", IdeaRecall.DefaultThreshold,
                    "The decision's probability an idea needs before it is suggested. 0.8 gave no false suggestion on 56 unrelated messages (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Bool("ideas.saveCheck", "Offer unsaved plans when a chat closes", true,
                    "When a chat tab is closed, the model says whether it leaves a plan nobody built or wrote down. A new plan gets a card to save or discard; work on an open idea is attached to it instead."),
                SettingInfo.Number("ideas.attachThreshold", "Attach threshold", IdeaSaveCheck.DefaultAttachThreshold,
                    "The probability a closed chat has to be about an open idea before the chat is attached to it. 0.8 was right on 5 of 6 (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Str("ideas.model", "Model for the idea checks", IdeaRecall.DefaultModel,
                    "The decision model (through the Decide plugin's server) and the model that drafts the save check. qwen3.8-27b, the NInfer chat model, was measured."),
                SettingInfo.Bool("ideas.closeOnCommit", "Notice when a commit finishes an idea", true,
                    "Every project with a git repository is watched. A commit is recorded on the open idea it works on, and when a commit may have finished one you get a card to mark it done (needs the Files and Decide plugins)."),
                SettingInfo.Number("ideas.linkThreshold", "Link threshold", IdeaCommitCheck.DefaultLinkThreshold,
                    "The probability a commit has to be about an open idea before the commit is recorded on it. 0.7 linked no wrong idea in 187 commits (docs/DECISION-MODELS.md).", 0.3, 0.99),
                SettingInfo.Number("ideas.doneThreshold", "Finished threshold", IdeaCommitCheck.DefaultDoneThreshold,
                    "The probability an idea has to be finished before you are offered. 0.8 offered 4 of 5 finished ideas and nothing that was only advanced.", 0.3, 0.99),
            ],
        });
        var store = context.Track(new IdeasStore(context.Events, context.Logger));
        var settings = context.Settings;
        var locator = new IdeasLocator(() => context.Sessions, context.Paths, () => settings);
        await MigratePerProjectFiles(context, store, locator, ct);

        context.Tools.Register(new IdeasTool(store, locator));

        var rpc = new IdeasRpc(store, locator);
        rpc.Register(context.Rpc);
        new IdeaRecall(context, store, locator).Register(context.Rpc);
        var saveCheck = new IdeaSaveCheck(context, store, locator);
        saveCheck.Register(context.Rpc);
        // An answer that was interrupted between the journal and the backlog is finished before anything else asks for
        // the cards: the card is either saved (once) or still there, never neither.
        try { await saveCheck.RecoverAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { context.Logger.LogWarning("Ideas: the interrupted card answers could not be finished: {Message}", ex.Message); }
        // Phase 3: watch the projects' repositories. Started after the tab and the file watcher, and it never blocks
        // the start: a project that cannot be watched is retried on the next rescan.
        var commitCheck = context.Track(new IdeaCommitCheck(context, store, locator, saveCheck));
        _ = commitCheck.StartAsync();

        context.Ui.AddTab(new UiTabInfo { Id = "ideas", Title = "Ideas", Panel = UiPanel.Right, Icon = "idea", Order = 20, Module = "ui.js" });
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "idea", Description = "Add an idea to the backlog", ArgsHint = "<title>", Rpc = "ideas.quickAdd",
        });

        // Start watching the (single) file right away so external edits are noticed.
        try { store.Watch(locator.GlobalFile()); } catch { }
    }

    /// <summary>
    /// One-time move of the per-project files of earlier versions (<c>.netpi/ideas.json</c>, and the legacy
    /// <c>ideas.json</c> in the project folder) into the single global file: the ideas are appended, stamped with
    /// <c>project</c> and given a new id when they collide with one already there. While the global file cannot be read
    /// or written the migration is skipped and retried on the next start.
    /// <para>
    /// Each import is written to <c>ideas-migration.json</c> (path + content hash + the ids it produced) <b>before</b>
    /// the source is deleted, and the source is deleted only after the import was read back from the file that was
    /// written. So an interrupted run — a crash between the write and the delete, a delete that fails because the file
    /// is open, a power cut — re-imports nothing: the next start sees its own receipt and only finishes the delete.
    /// </para>
    /// </summary>
    private static async Task MigratePerProjectFiles(IPluginContext ctx, IdeasStore store, IdeasLocator locator, CancellationToken ct)
    {
        var file = locator.GlobalFile();
        var name = locator.FileName();
        var receiptFile = Path.Combine(Path.GetDirectoryName(file)!, MigrationReceipt.FileName);
        var receipt = MigrationReceipt.Read(receiptFile);

        foreach (var p in ctx.Sessions.ListProjects().Where(p => !string.IsNullOrWhiteSpace(p.Path)))
        {
            var sources = new[]
            {
                Path.Combine(p.Path, ".netpi", name),
                Path.Combine(p.Path, name), // the legacy place, the project folder itself
            }.Where(File.Exists).Distinct().ToList();
            if (sources.Count == 0) continue;

            // Already imported (the delete did not happen last time): nothing to merge again, only to clean up.
            var fresh = new List<(string Path, byte[] Bytes, string Hash)>();
            foreach (var source in sources)
            {
                try
                {
                    var bytes = File.ReadAllBytes(source);
                    var hash = MigrationReceipt.HashOf(bytes);
                    if (receipt.Has(source, hash)) continue;
                    fresh.Add((source, bytes, hash));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ctx.Logger.LogWarning("Ideas migration for project {Project} skipped: {Message}", p.Name, ex.Message);
                }
            }
            if (fresh.Count == 0)
            {
                foreach (var source in sources) TryDelete(ctx, source);
                continue;
            }

            try
            {
                var imported = await store.UpdateAsync(file, f =>
                {
                    var known = new HashSet<string?>(IdeaOps.All(f.Ideas).Select(i => IdeaOps.Str(i["id"])), StringComparer.OrdinalIgnoreCase);
                    var ids = new List<string>();
                    foreach (var (source, bytes, _) in fresh)
                    {
                        var doc = IdeasStore.Parse(source, bytes);
                        foreach (var node in doc.Ideas.OfType<JsonObject>())
                        {
                            var idea = (JsonObject)node.DeepClone(); // detach from the source document
                            if (IdeaOps.ProjectOf(idea) is null) IdeaOps.SetProject(idea, p.Id, p.Name);
                            if (IdeaOps.Str(idea["id"]) is not { } id || !known.Add(id))
                            {
                                var freshId = IdeaOps.NewId("idea-", known, 6);
                                idea["id"] = freshId;
                                known.Add(freshId);
                            }
                            f.Ideas.Add(idea);
                            ids.Add(IdeaOps.Str(idea["id"])!);
                        }
                    }
                    return ids;
                });
                // The import is only finished when it is really in the file that was written; the sources stay otherwise.
                var stored = await store.ReadAsync(file, f => IdeaOps.All(f.Ideas).Select(i => IdeaOps.Str(i["id"])).ToHashSet(StringComparer.OrdinalIgnoreCase));
                if (imported.Any(id => !stored.Contains(id)))
                {
                    ctx.Logger.LogWarning("Ideas migration for project {Project}: the merged ideas are not all in {File}; the sources are kept.", p.Name, file);
                    continue;
                }
                foreach (var (source, _, hash) in fresh) receipt.Remember(source, hash, imported);
                await receipt.SaveAsync(receiptFile, ct).ConfigureAwait(false);
                ctx.Logger.LogInformation("Merged {Count} idea(s) from project {Project} into {File}", imported.Count, p.Name, file);
                foreach (var (source, _, _) in fresh) TryDelete(ctx, source);
            }
            catch (IdeasFileException ex)
            {
                ctx.Logger.LogWarning("Ideas migration for project {Project} skipped until the global file is fixed: {Message}", p.Name, ex.Message);
                break; // the global file is broken: nothing can be merged
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.Logger.LogWarning("Ideas migration for project {Project} skipped: {Message}", p.Name, ex.Message);
            }
        }
    }

    /// <summary>A source that cannot be deleted stays; the receipt means the next start deletes it instead of importing it again.</summary>
    private static void TryDelete(IPluginContext ctx, string source)
    {
        try { File.Delete(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ctx.Logger.LogWarning("Ideas: the imported file {Source} could not be deleted ({Message}); it is finished at the next start.", source, ex.Message); }
    }

}

/// <summary>ideas.* RPC handlers (see docs/PLUGIN-IDEAS.md).</summary>
public sealed class IdeasRpc(IdeasStore store, IdeasLocator locator)
{
    private readonly IdeasStore _store = store;
    private readonly IdeasLocator _locator = locator;

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.list", List, "The ideas backlog (the single global file): { } → { file, fileName, exists, ideas }");
        rpc.Register("ideas.get", Get, "{ id } → idea");
        rpc.Register("ideas.add", Add, "{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections? }, prepend? } → idea; stamped with projectId (a project id or name, \"global\" for unbound), else the session's project");
        rpc.Register("ideas.update", Update,
            "{ id, patch, expectedUpdatedAt? } → idea; patch.project: a project id/name or { id, name? } rebinds, null (or \"global\") unbinds; " +
            "expectedUpdatedAt: refused with \"conflict\" when the idea changed since it was read (a stale window or editor)");
        rpc.Register("ideas.delete", Delete, "{ id } → true");
        rpc.Register("ideas.reorder", Reorder, "{ ids: string[] } → true");
        rpc.Register("ideas.toPrompt", ToPrompt, "{ id } → markdown prompt text");
        rpc.Register("ideas.quickAdd", QuickAdd, "/idea command: { sessionId, args } → status text");
    }

    private static async Task<object?> Guard(Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (IdeaInputException ex) { throw new RpcException("bad_request", ex.Message); }
        catch (IdeasFileException ex) { throw new RpcException("invalid_file", ex.Message); }
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

    public Task<object?> List(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var file = _locator.GlobalFile();
        return await _store.ReadAsync(file, f => (object?)new JsonObject
        {
            ["file"] = file,
            ["fileName"] = Path.GetFileName(file),
            ["exists"] = f.Exists,
            ["ideas"] = f.Ideas.DeepClone(),
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Get(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var id = req.Required("id");
        return await _store.ReadAsync(_locator.GlobalFile(), f =>
            (object?)(IdeaOps.Find(f.Ideas, id)?.DeepClone() ?? throw new RpcException("not_found", $"Idea {id} not found")), ct).ConfigureAwait(false);
    });

    public Task<object?> Add(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var input = ObjectParam(req, "idea");
        var stamp = ResolveStamp(req);
        var sessionId = req.Str("sessionId");
        return await _store.UpdateAsync(_locator.GlobalFile(), f =>
        {
            var idea = IdeaOps.CreateIdea(input, f.Ideas, "user", sessionId, keepExtraFields: true);
            IdeaOps.SetProject(idea, stamp?.Id, stamp?.Name);
            if (req.Bool("prepend") == true) f.Ideas.Insert(0, idea); else f.Ideas.Add(idea);
            return (object?)idea.DeepClone();
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Update(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var id = req.Required("id");
        var patch = ObjectParam(req, "patch");
        _locator.NormalizeProject(patch); // a bare project id/name in the patch → { id, name } (unknown: an error)
        // Optional: what the editor had when it opened the card. Something else wrote the idea since (another window,
        // the agent, a file edit) and the change is refused instead of silently overwriting it — a lock cannot see an
        // editor that does not take it, this can.
        var expected = req.Str("expectedUpdatedAt");
        return await _store.UpdateAsync(_locator.GlobalFile(), f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            if (expected is { Length: > 0 } && IdeaOps.Str(idea["updatedAt"]) != expected)
                throw new RpcException("conflict", $"Idea {id} changed since {expected} (it is now {IdeaOps.Str(idea["updatedAt"]) ?? "untouched"}). Reload it and apply the change again.");
            IdeaOps.ApplyPatch(idea, patch, fromUi: true);
            return (object?)idea.DeepClone();
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Delete(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var id = req.Required("id");
        return await _store.UpdateAsync(_locator.GlobalFile(), f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            f.Ideas.Remove(idea);
            return (object?)true;
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Reorder(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var ids = req.Prop("ids") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : throw new RpcException("bad_request", "Missing array parameter 'ids'");
        return await _store.UpdateAsync(_locator.GlobalFile(), f =>
        {
            // Listed ideas first in the given order, then the rest in their current order.
            var ordered = new List<JsonNode?>();
            foreach (var id in ids)
                if (IdeaOps.Find(f.Ideas, id) is { } idea && !ordered.Contains(idea)) ordered.Add(idea);
            foreach (var n in f.Ideas) if (!ordered.Contains(n)) ordered.Add(n);
            f.Ideas.Clear();
            foreach (var n in ordered) f.Ideas.Add(n);
            return (object?)true;
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> ToPrompt(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var id = req.Required("id");
        return await _store.ReadAsync(_locator.GlobalFile(), f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            return (object?)IdeaOps.ToPrompt(idea, _locator.Shown());
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> QuickAdd(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var title = req.Str("args")?.Trim();
        if (string.IsNullOrEmpty(title)) throw new RpcException("bad_request", "Usage: /idea <title>");
        var stamp = ResolveStamp(req);
        var sessionId = req.Str("sessionId");
        var idea = await _store.UpdateAsync(_locator.GlobalFile(), f =>
        {
            var created = IdeaOps.CreateIdea(new JsonObject { ["title"] = title }, f.Ideas, "user", sessionId);
            IdeaOps.SetProject(created, stamp?.Id, stamp?.Name);
            f.Ideas.Add(created);
            return (JsonObject)created.DeepClone();
        }, ct).ConfigureAwait(false);
        var where = IdeaOps.ProjectOf(idea) is null ? "global backlog" : $"project {IdeaOps.ProjectLabel(idea)}";
        return (object?)$"Idea added ({where}): {IdeaOps.Str(idea["title"])} ({IdeaOps.Str(idea["id"])})";
    });

    private static JsonObject ObjectParam(RpcRequest req, string name)
    {
        var p = req.Prop(name);
        if (p is not { ValueKind: JsonValueKind.Object } v) throw new RpcException("bad_request", $"Missing object parameter '{name}'");
        return JsonObject.Create(v.Clone())!;
    }
}
