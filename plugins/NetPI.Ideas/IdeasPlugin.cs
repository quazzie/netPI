using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>
/// Backlog of unimplemented ideas, research and plans (<c>.netpi/ideas.json</c> per project, else
/// <c>~/.netpi/ideas.json</c>): the agent tool <c>ideas</c> (list, get, add, update), ideas.* RPC for the "Ideas" tab
/// (which also deletes), and the /idea command. Setting: <c>ideas.fileName</c> (default "ideas.json").
/// Event: <c>ideas.changed { file }</c>.
/// </summary>
[NetPiPlugin("netpi.ideas", Name = "Ideas", Description = "Backlog of ideas, research and plans for the user and agents", Order = 80)]
public sealed class IdeasPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "ideas", Title = "Ideas", Group = "Tools", Order = 60,
            Settings =
            [
                SettingInfo.Str("ideas.fileName", "Ideas file", "ideas.json", "In the project's .netpi folder; ~/.netpi/ideas.json for sessions without a project."),
            ],
        });
        var store = context.Track(new IdeasStore(context.Events, context.Logger));
        var settings = context.Settings;
        var locator = new IdeasLocator(() => context.Sessions, context.Paths, () => settings);

        context.Tools.Register(new IdeasTool(store, locator));

        var rpc = new IdeasRpc(store, locator);
        rpc.Register(context.Rpc);

        context.Ui.AddTab(new UiTabInfo { Id = "ideas", Title = "Ideas", Panel = UiPanel.Right, Icon = "idea", Order = 20, Module = "ui.js" });
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "idea", Description = "Add an idea to the backlog", ArgsHint = "<title>", Rpc = "ideas.quickAdd",
        });

        // Start watching the global file right away so external edits are noticed.
        try { store.Watch(locator.Global().File); } catch { }
        return Task.CompletedTask;
    }
}

/// <summary>ideas.* RPC handlers (see docs/PLUGIN-IDEAS.md).</summary>
public sealed class IdeasRpc(IdeasStore store, IdeasLocator locator)
{
    public void Register(IRpcRegistry rpc)
    {
        rpc.Register("ideas.list", List, "Ideas of a project/session: { sessionId?, projectId? } → { file, scope, projectId?, projectName?, exists, ideas }");
        rpc.Register("ideas.get", Get, "{ sessionId?, projectId?, id } → idea");
        rpc.Register("ideas.add", Add, "{ sessionId?, projectId?, idea: { title, summary?, status?, priority?, tags?, sections? } } → idea");
        rpc.Register("ideas.update", Update, "{ sessionId?, projectId?, id, patch } → idea");
        rpc.Register("ideas.delete", Delete, "{ sessionId?, projectId?, id } → true");
        rpc.Register("ideas.reorder", Reorder, "{ sessionId?, projectId?, ids: string[] } → true");
        rpc.Register("ideas.toPrompt", ToPrompt, "{ sessionId?, projectId?, id } → markdown prompt text");
        rpc.Register("ideas.quickAdd", QuickAdd, "/idea command: { sessionId, args } → status text");
    }

    private IdeasLocation Locate(RpcRequest req) => locator.Resolve(req.Str("sessionId"), req.Str("projectId"));

    private static JsonObject ObjectParam(RpcRequest req, string name)
    {
        var p = req.Prop(name);
        if (p is not { ValueKind: JsonValueKind.Object } v) throw new RpcException("bad_request", $"Missing object parameter '{name}'");
        return JsonObject.Create(v.Clone())!;
    }

    private static async Task<object?> Guard(Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (IdeaInputException ex) { throw new RpcException("bad_request", ex.Message); }
        catch (IdeasFileException ex) { throw new RpcException("invalid_file", ex.Message); }
        catch (IOException ex) { throw new RpcException("io_error", ex.Message); }
        catch (UnauthorizedAccessException ex) { throw new RpcException("io_error", ex.Message); }
    }

    private static void EnsureWritable(IdeasLocation loc)
    {
        if (loc.Scope == "project" && !Directory.Exists(loc.ProjectDir))
            throw new RpcException("not_found", $"The project folder {loc.ProjectDir} does not exist.");
    }

    public Task<object?> List(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        return await store.ReadAsync(loc.File, f =>
        {
            var result = new JsonObject
            {
                ["file"] = loc.File,
                ["fileName"] = loc.FileName,
                ["scope"] = loc.Scope,
                ["exists"] = f.Exists,
            };
            if (loc.ProjectId is not null) result["projectId"] = loc.ProjectId;
            if (loc.ProjectName is not null) result["projectName"] = loc.ProjectName;
            result["ideas"] = f.Ideas.DeepClone();
            return (object?)result;
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Get(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        var id = req.Required("id");
        return await store.ReadAsync(loc.File, f =>
            (object?)(IdeaOps.Find(f.Ideas, id)?.DeepClone() ?? throw new RpcException("not_found", $"Idea {id} not found")), ct).ConfigureAwait(false);
    });

    public Task<object?> Add(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        EnsureWritable(loc);
        var input = ObjectParam(req, "idea");
        var sessionId = req.Str("sessionId");
        return await store.UpdateAsync(loc.File, f =>
        {
            var idea = IdeaOps.CreateIdea(input, f.Ideas, "user", sessionId, keepExtraFields: true);
            if (req.Bool("prepend") == true) f.Ideas.Insert(0, idea); else f.Ideas.Add(idea);
            return (object?)idea.DeepClone();
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Update(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        EnsureWritable(loc);
        var id = req.Required("id");
        var patch = ObjectParam(req, "patch");
        return await store.UpdateAsync(loc.File, f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            IdeaOps.ApplyPatch(idea, patch, fromUi: true);
            return (object?)idea.DeepClone();
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Delete(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        EnsureWritable(loc);
        var id = req.Required("id");
        return await store.UpdateAsync(loc.File, f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            f.Ideas.Remove(idea);
            return (object?)true;
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> Reorder(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var loc = Locate(req);
        EnsureWritable(loc);
        var ids = req.Prop("ids") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : throw new RpcException("bad_request", "Missing array parameter 'ids'");
        return await store.UpdateAsync(loc.File, f =>
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
        var loc = Locate(req);
        var id = req.Required("id");
        return await store.ReadAsync(loc.File, f =>
        {
            var idea = IdeaOps.Find(f.Ideas, id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            return (object?)IdeaOps.ToPrompt(idea, loc.Shown);
        }, ct).ConfigureAwait(false);
    });

    public Task<object?> QuickAdd(RpcRequest req, CancellationToken ct) => Guard(async () =>
    {
        var title = req.Str("args")?.Trim();
        if (string.IsNullOrEmpty(title)) throw new RpcException("bad_request", "Usage: /idea <title>");
        var loc = Locate(req);
        EnsureWritable(loc);
        var sessionId = req.Str("sessionId");
        var idea = await store.UpdateAsync(loc.File, f =>
        {
            var created = IdeaOps.CreateIdea(new JsonObject { ["title"] = title }, f.Ideas, "user", sessionId);
            f.Ideas.Add(created);
            return (JsonObject)created.DeepClone();
        }, ct).ConfigureAwait(false);
        var where = loc.Scope == "project" ? $"project {loc.ProjectName}" : "global backlog";
        return (object?)$"Idea added ({where}): {IdeaOps.Str(idea["title"])} ({IdeaOps.Str(idea["id"])})";
    });
}
