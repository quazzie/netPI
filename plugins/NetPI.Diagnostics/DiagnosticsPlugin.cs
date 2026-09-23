using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Diagnostics;

/// <summary>
/// "Diagnostics" tab: <c>diag.snapshot</c> (plugins, tools, RPC methods, recent events, logs, runtime), <c>diag.event</c>
/// (full payload of one recent event) and the <c>/reload [pluginId]</c> command (<c>diag.reload</c>).
/// </summary>
[NetPiPlugin("netpi.diagnostics", Name = "Diagnostics", Description = "Plugins, tools, RPC, events and logs; /reload", Order = 90)]
public sealed class DiagnosticsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var diag = new DiagnosticsService(context);
        context.Rpc.Register("diag.snapshot", async (req, rpcCt) => await diag.SnapshotAsync(req.Int("events") ?? 200, rpcCt).ConfigureAwait(false),
            "Diagnostics overview → { plugins, tools, rpc, events, logs, runtime, time }");
        context.Rpc.Register("diag.event", (req, _) => Task.FromResult<object?>(diag.Event(ParseSeq(req))),
            "Full data of a recent bus event: { seq } → { seq, type, sessionId, time, source, ui, data }");
        context.Rpc.Register("diag.reload", async (req, rpcCt) => await diag.ReloadAsync(req.Str("args") ?? req.Str("id"), rpcCt).ConfigureAwait(false),
            "/reload command: { args?: pluginId } → status text (no id = all plugins)");

        context.Ui.AddTab(new UiTabInfo { Id = "diagnostics", Title = "Diagnostics", Panel = UiPanel.Right, Icon = "bug", Order = 90, Module = "ui.js" });
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "reload", Description = "Hot-reload a plugin (or all plugins)", ArgsHint = "[pluginId]", Rpc = "diag.reload",
        });
        return Task.CompletedTask;
    }

    private static long ParseSeq(RpcRequest req) =>
        long.TryParse(req.Required("seq"), out var seq) ? seq : throw new RpcException("bad_request", "'seq' must be a number");
}

public sealed class DiagnosticsService(IPluginContext ctx)
{
    private static readonly DateTimeOffset ProcessStart = GetProcessStart();

    public async Task<JsonObject> SnapshotAsync(int maxEvents, CancellationToken ct)
    {
        var snapshot = new JsonObject
        {
            ["plugins"] = Safe(() => ctx.Services.Get<IPluginManager>() is { } pm ? NetPiJson.ToNode(pm.List()) : null),
            ["tools"] = await ToolsAsync(ct).ConfigureAwait(false),
            ["rpc"] = Safe(() => new JsonArray(ctx.Rpc.List()
                .OrderBy(m => m.Method, StringComparer.Ordinal)
                .Select(m => (JsonNode?)new JsonObject { ["method"] = m.Method, ["description"] = m.Description, ["pluginId"] = m.PluginId })
                .ToArray())),
            ["events"] = Safe(() => new JsonArray(ctx.Events.Recent(Math.Clamp(maxEvents, 1, 5000))
                .Select(e => (JsonNode?)new JsonObject
                {
                    ["seq"] = e.Seq, ["type"] = e.Type, ["sessionId"] = e.SessionId, ["time"] = e.Time.ToString("O"), ["source"] = e.Source,
                })
                .ToArray())),
            ["logs"] = await OptionalRpcAsync("logs.recent", new JsonObject { ["max"] = 200 }, ct).ConfigureAwait(false),
            ["runtime"] = Safe(Runtime),
            ["time"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        return snapshot;
    }

    private async Task<JsonNode?> ToolsAsync(CancellationToken ct)
    {
        if (ctx.Rpc.Exists("tools.list")) return await OptionalRpcAsync("tools.list", null, ct).ConfigureAwait(false);
        return Safe(() =>
        {
            var arr = new JsonArray();
            foreach (var reg in ctx.Tools.Registrations)
            {
                var d = reg.Tool.Definition;
                arr.Add(new JsonObject
                {
                    ["name"] = d.Name, ["label"] = d.Label, ["description"] = d.Description, ["category"] = d.Category,
                    ["readOnly"] = d.ReadOnly, ["pluginId"] = reg.PluginId, ["priority"] = reg.Priority,
                    ["active"] = ReferenceEquals(ctx.Tools.Get(d.Name), reg.Tool),
                });
            }
            return arr;
        });
    }

    private async Task<JsonNode?> OptionalRpcAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists(method)) return null;
        try { return NetPiJson.ToNode(await ctx.Rpc.InvokeAsync(method, parameters, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
    }

    private JsonNode? Safe(Func<JsonNode?> f)
    {
        try { return f(); }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "diag.snapshot part failed");
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    private static JsonNode Runtime()
    {
        using var p = Process.GetCurrentProcess();
        return new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["workingSetMb"] = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            ["gcHeapMb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            ["threads"] = p.Threads.Count,
            ["uptimeSeconds"] = (long)(DateTimeOffset.UtcNow - ProcessStart).TotalSeconds,
        };
    }

    private static DateTimeOffset GetProcessStart()
    {
        try { using var p = Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); }
        catch { return DateTimeOffset.UtcNow; }
    }

    public JsonObject Event(long seq)
    {
        var e = ctx.Events.Recent(100_000).FirstOrDefault(x => x.Seq == seq)
                ?? throw new RpcException("not_found", $"Event {seq} is no longer in the recent-events buffer");
        JsonNode? data;
        try { data = NetPiJson.ToNode(e.Data); }
        catch (Exception ex) { data = JsonValue.Create($"<{e.Data?.GetType().Name}: not serializable: {ex.Message}>"); }
        return new JsonObject
        {
            ["seq"] = e.Seq, ["type"] = e.Type, ["sessionId"] = e.SessionId, ["time"] = e.Time.ToString("O"),
            ["source"] = e.Source, ["ui"] = e.Ui, ["data"] = data,
        };
    }

    public async Task<object?> ReloadAsync(string? arg, CancellationToken ct)
    {
        var pm = ctx.Services.Get<IPluginManager>() ?? throw new RpcException("unavailable", "The plugin manager is not available.");
        arg = arg?.Trim();
        if (string.IsNullOrEmpty(arg) || arg is "all" or "*")
        {
            var plugins = pm.List().Where(p => p.Enabled && p.State != "disabled").OrderBy(p => p.Order).ThenBy(p => p.Id).ToList();
            var self = plugins.FirstOrDefault(p => p.Id == ctx.PluginId);
            var logger = ctx.Logger;
            // Background: reloading ourselves disposes this RPC handler; do it last.
            _ = Task.Run(async () =>
            {
                try { await pm.RescanAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { logger.LogWarning(ex, "Plugin rescan failed"); }
                foreach (var p in plugins)
                {
                    if (p == self) continue;
                    try { await pm.ReloadAsync(p.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Reloading {Plugin} failed", p.Id); }
                }
                if (self is not null)
                {
                    try { await pm.ReloadAsync(self.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Reloading {Plugin} failed", self.Id); }
                }
            }, CancellationToken.None);
            return $"Reloading {plugins.Count} plugins…";
        }

        var target = FindPlugin(pm.List(), arg);
        if (target.Id == ctx.PluginId)
        {
            _ = Task.Run(() => pm.ReloadAsync(target.Id, CancellationToken.None), CancellationToken.None);
            return $"Reloading {target.Name} ({target.Id})…";
        }
        await pm.ReloadAsync(target.Id, ct).ConfigureAwait(false);
        var after = pm.List().FirstOrDefault(p => p.Id == target.Id);
        return after is { State: "failed" }
            ? $"Reloaded {target.Id}, but it failed to start: {after.Error}"
            : $"Reloaded {target.Name} ({target.Id})";
    }

    /// <summary>Exact id, case-insensitive id or name, id suffix ("retry" → "netpi.retry"), or a unique substring.</summary>
    public static PluginInfo FindPlugin(IReadOnlyList<PluginInfo> plugins, string arg)
    {
        var hit = plugins.FirstOrDefault(p => p.Id == arg)
                  ?? plugins.FirstOrDefault(p => string.Equals(p.Id, arg, StringComparison.OrdinalIgnoreCase))
                  ?? plugins.FirstOrDefault(p => string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        var suffix = plugins.Where(p => p.Id.EndsWith("." + arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (suffix.Count == 1) return suffix[0];
        var partial = plugins.Where(p => p.Id.Contains(arg, StringComparison.OrdinalIgnoreCase)
                                         || p.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count > 1)
            throw new RpcException("ambiguous", $"'{arg}' matches several plugins: {string.Join(", ", partial.Select(p => p.Id))}");
        throw new RpcException("not_found", $"No plugin '{arg}'. Known: {string.Join(", ", plugins.Select(p => p.Id))}");
    }
}
