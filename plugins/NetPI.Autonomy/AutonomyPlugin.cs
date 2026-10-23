using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Autonomy;

/// <summary>
/// Autonomy: chats nobody watches keep working as the owner, and the owner approves almost nothing in the moment. Three parts,
/// none of them in the core:
/// <list type="bullet">
/// <item><b>Standing grants</b> (<see cref="Grants"/>, the <see cref="IToolGrants"/> the guardrails ask): an ask rule allowed for
/// good for one origin ("your chats", "webhook Checkmk"); in a chat started by a schedule or a webhook, or a subagent, an ask with
/// no grant is refused and kept for the owner to grant, instead of waiting for nobody.</item>
/// <item><b>A journal with undo</b> (<see cref="AutonomyHook"/>): every change a schedule's or webhook's chat makes, with a copy of
/// each file it writes; looked up in the tab when wanted, never pushed.</item>
/// <item><b>Brakes</b>: a pause switch, an hourly cap per origin (the one thing that tells the owner unprompted:
/// <c>autonomy.braked</c>), and what a chat that read outside text may never do (<c>autonomy.taintedBlocked</c>).</item>
/// </list>
/// Disabled, nothing of it runs: the guardrails ask as they did, and the data waits in the plugin's collections.
/// </summary>
[NetPiPlugin("netpi.autonomy", Name = "Autonomy", Description = "Standing grants instead of questions for chats nobody watches, a journal of what they changed with undo, and brakes", Order = 68)]
public sealed class AutonomyPlugin : INetPiPlugin
{
    private readonly TimeProvider _time;
    private CancellationTokenSource? _stop;
    private Task? _sweeper;

    public AutonomyPlugin() : this(TimeProvider.System) { }
    internal AutonomyPlugin(TimeProvider time) => _time = time;

    internal AutonomyStore? Store { get; private set; }

    public Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Services.Register(new SettingsSection
        {
            Id = "autonomy", Title = "Autonomy", Group = "Tools", Order = 6,
            Help = "For chats started by a schedule or a webhook (and subagents): what they may do without asking, what they changed, and the brakes. The Autonomy tab shows it.",
            Settings =
            [
                SettingInfo.Bool("autonomy.paused", "Pause unattended changes", false,
                    "Chats started by a schedule or a webhook may still read, but every change is refused until this is off."),
                SettingInfo.Choice("autonomy.unattendedAsks", "When a guardrail would ask", "refuse", ["refuse", "wait"],
                    "refuse: in a chat nobody watches, a guardrail's question is refused and listed in the Autonomy tab, where you can allow it for that origin from then on. wait: it waits for your answer, as before (a subagent's is refused either way)."),
                SettingInfo.Int("autonomy.maxChangesPerHour", "Changes per hour, per origin", 120,
                    "Chats for one schedule or webhook that make more changes than this in an hour are stopped at the next one, and you get one notification. 0 switches it off.", 0, 100000),
                SettingInfo.Int("autonomy.keepDays", "Keep the journal for", 30, "Journal rows, refusals and file copies older than this are dropped.", 1, 365, "days"),
                SettingInfo.Int("autonomy.grantUnusedDays", "Drop grants unused for", 60, "A grant that let nothing through for this long is dropped. 0 keeps them.", 0, 3650, "days"),
                SettingInfo.Int("autonomy.maxCheckpointKb", "Largest file copy", 4096, "A file larger than this is changed without a copy (no undo; the journal says so).", 0, 65536, "KB"),
                SettingInfo.List("autonomy.externalTools", "Tools that bring outside text", Options.DefaultExternalTools,
                    "After one of these, a schedule's chat counts as having read outside text (a webhook's chat does from the start). plugin:<id> names every tool of a plugin."),
                SettingInfo.List("autonomy.taintedBlocked", "Never after outside text", Options.DefaultTaintedBlocked,
                    "What a schedule's or webhook's chat that read outside text may not do, whatever is granted: a tool, or tool: action, action. So a page or an alert's text cannot make it act as you in the browser or on the desktop."),
            ],
        });
        var options = new Options(ctx.Settings);
        var chats = new Chats(ctx);
        var store = Store = new AutonomyStore(ctx.Data, _time);
        var grants = new Grants(ctx, chats, store, options);
        ctx.Services.Register<IToolGrants>(grants);
        ctx.Services.Register<IAgentHook>(new AutonomyHook(ctx, chats, store, options, _time));
        Register(ctx, store, grants, options);
        ctx.Ui.AddTab(new UiTabInfo
        {
            Id = "autonomy", Title = "Autonomy", Panel = UiPanel.Right, Order = 60, Module = "ui.js",
            Icon = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3l7 3v5c0 4.5-3 8-7 10-4-2-7-5.5-7-10V6z'/><path d='M9 12l2 2 4-4'/></svg>",
        });
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        _sweeper = SweepAsync(ctx, store, options, _stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stop?.Cancel();
        if (_sweeper is { } s) await s.ConfigureAwait(false);
        _stop?.Dispose();
    }

    private static void Register(IPluginContext ctx, AutonomyStore store, Grants grants, Options options)
    {
        ctx.Rpc.Register(new RpcMethod("autonomy.state", "The Autonomy tab's header: { paused, changesLastHour, grants, refusals }", ReadOnly: true),
            (_, _) => Task.FromResult<object?>(new JsonObject
            {
                ["paused"] = options.Paused,
                ["changesLastHour"] = store.ChangesSince(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3_600_000),
                ["grants"] = store.Grants().Count,
                ["refusals"] = store.Refusals(1000).Count(r => r["grantedMs"] is null),
            }));
        ctx.Rpc.Register(new RpcMethod("autonomy.journal",
                "What chats started by a schedule or a webhook changed, newest first: { origin?, sessionId?, beforeMs?, limit? (100, max 500) } → " +
                "[{ id, atMs, at, origin, label, sessionId, title, tool, subject, error, checkpoint?: { id, path, existed, afterHash, undoneAt? } | { files: [...] }, note? }]",
                ReadOnly: true, Params: [RpcParam.Opt("origin"), RpcParam.Opt("sessionId"), RpcParam.Opt("beforeMs", RpcParamType.Integer), RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.Journal(req.Str("origin"), req.Str("sessionId"), Long(req, "beforeMs"),
                Math.Clamp(req.Int("limit") ?? 100, 1, 500)).Select(r => (JsonNode)r)])));
        ctx.Rpc.Register(new RpcMethod("autonomy.refusals",
                "What chats nobody watches were refused, newest first: { limit? (100) } → [{ id, at, origin, label, sessionId, title, tool, why: ask | tainted, kind?, rule?, subject, grantable, grantedMs? }]",
                ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(new JsonArray([.. store.Refusals(Math.Clamp(req.Int("limit") ?? 100, 1, 500)).Select(r => (JsonNode)r)])));
        ctx.Rpc.Register(new RpcMethod("autonomy.grants", "The standing grants: [{ id, origin, label, rule, tool, kind, createdAt, lastUsedMs?, uses }]", ReadOnly: true),
            (_, _) => Task.FromResult<object?>(new JsonArray([.. store.Grants().Select(g => (JsonNode)g)])));
        ctx.Rpc.Register(new RpcMethod("autonomy.grant", "Allow a refused guardrail question for its origin from now on: { refusalId } → the grant", ReadOnly: false,
                Params: [RpcParam.Req("refusalId")]),
            (req, _) => Task.FromResult<object?>(grants.GrantRefusal(req.Required("refusalId"))));
        ctx.Rpc.Register(new RpcMethod("autonomy.revoke", "Remove a standing grant: { id } → true when it existed", ReadOnly: false, Params: [RpcParam.Req("id")]),
            (req, _) =>
            {
                var removed = store.Revoke(req.Required("id"));
                ctx.Events.Publish("autonomy.changed", new JsonObject { ["what"] = "grant" });
                return Task.FromResult<object?>(removed);
            });
        ctx.Rpc.Register(new RpcMethod("autonomy.pause", "Pause or resume changes by chats started by a schedule or a webhook (setting autonomy.paused): { paused } → paused", ReadOnly: false,
                Params: [RpcParam.Req("paused", RpcParamType.Boolean)]),
            (req, _) =>
            {
                var paused = req.Bool("paused") ?? false;
                ctx.Settings.Set("autonomy.paused", JsonValue.Create(paused));
                ctx.Events.Publish("autonomy.changed", new JsonObject { ["what"] = "paused" });
                return Task.FromResult<object?>(paused);
            });
        ctx.Rpc.Register(new RpcMethod("autonomy.undo",
                "Put back the files a journal row changed, as they were before: { id, force? } → { restored: [path] }. Refused (conflict) when a file changed again since, unless force",
                ReadOnly: false, Params: [RpcParam.Req("id"), RpcParam.Opt("force", RpcParamType.Boolean)]),
            (req, _) =>
            {
                var restored = Undo(store, req.Required("id"), req.Bool("force") ?? false);
                ctx.Logger.LogInformation("Autonomy: undid journal row {Id} ({Files})", req.Required("id"), string.Join(", ", restored));
                ctx.Events.Publish("autonomy.changed", new JsonObject { ["what"] = "journal" });
                return Task.FromResult<object?>(new JsonObject { ["restored"] = new JsonArray([.. restored.Select(p => (JsonNode)p)]) });
            });
    }

    private static long? Long(RpcRequest req, string name) =>
        req.Prop(name) is { ValueKind: System.Text.Json.JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    /// <summary>
    /// The files of a journal row as they were before the change: rewritten, or deleted when the change created them. A file that
    /// changed again since (its hash is not the one recorded after the change) is refused unless forced, so an undo never
    /// silently throws away later work.
    /// </summary>
    internal static List<string> Undo(AutonomyStore store, string rowId, bool force)
    {
        var row = store.Row(rowId) ?? throw new RpcException("not_found", "No journal row with that id (it may have been dropped after its keep time).");
        if (row["checkpoint"] is not JsonObject cp) throw new RpcException("bad_request", "This change kept no copy of a file: it cannot be undone from here.");
        var files = cp["files"] is JsonArray many ? [.. many.OfType<JsonObject>()] : new List<JsonObject> { cp };
        if (files.Any(f => f["undoneAt"] is not null)) throw new RpcException("conflict", "This change was undone already.");
        var copies = files.Select(f => (File: f, Copy: store.Checkpoint((string)f["id"]!))).ToList();
        if (copies.FirstOrDefault(c => c.Copy is null) is { File: not null } gone)
            throw new RpcException("not_found", $"The copy of {gone.File["path"]} is gone (dropped after its keep time).");
        if (!force)
            foreach (var (f, _) in copies)
                if (AutonomyHook.Hash((string)f["path"]!) != (string?)f["afterHash"])
                    throw new RpcException("conflict", $"{f["path"]} changed again after this change; undo with force to put it back anyway.");
        var restored = new List<string>();
        foreach (var (f, copy) in copies)
        {
            var path = (string)f["path"]!;
            if (copy!["existed"]?.GetValue<bool>() == true)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Convert.FromBase64String((string?)copy["content"] ?? ""));
            }
            else if (File.Exists(path)) File.Delete(path);
            f["undoneAt"] = DateTimeOffset.UtcNow.ToString("O");
            restored.Add(path);
        }
        store.PutRow(row);
        return restored;
    }

    /// <summary>Retention, at start and every six hours.</summary>
    private async Task SweepAsync(IPluginContext ctx, AutonomyStore store, Options options, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var (rows, refusals, copies, grants) = store.Prune(options.KeepDays, options.GrantUnusedDays);
                    if (rows + refusals + copies + grants > 0)
                        ctx.Logger.LogInformation("Autonomy: dropped {Rows} journal rows, {Refusals} refusals, {Copies} file copies and {Grants} unused grants", rows, refusals, copies, grants);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogWarning(ex, "Autonomy: retention sweep failed"); }
                await Task.Delay(TimeSpan.FromHours(6), _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
