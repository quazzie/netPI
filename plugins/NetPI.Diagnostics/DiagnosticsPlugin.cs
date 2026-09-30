using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Diagnostics;

/// <summary>
/// The way to inspect the running app (docs/DEBUGGING.md), for people (the Diagnostics tab), for agents (the read-only
/// <c>diag</c> tool) and for debugging scripts (the <c>diag.*</c> RPCs over HTTP, e.g. <c>node scripts/netpi.mjs
/// diag.overview</c>): an overview, the problems it can see, every model call (a model middleware: start, first token,
/// end, retries, tokens, errors), every tool call, a journal of the events that matter, one run in depth, the settings
/// without secrets, filtered logs, the failed requests the providers saved and a session's tool-set changes. Also
/// <c>diag.snapshot</c> and <c>diag.event</c> for the tab, and <c>/reload</c>.
/// </summary>
[NetPiPlugin("netpi.diagnostics", Name = "Diagnostics", Description = "Inspect the running app: overview, problems, model and tool calls, events, runs, logs, settings, failed requests; /reload", Order = 90)]
public sealed class DiagnosticsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var recorder = new Recorder(context);
        context.Services.Register(recorder.Middleware);
        context.Events.Subscribe("*", recorder.OnEvent);
        var reloads = new Reloads(context);
        context.Events.Subscribe(EventTypes.PluginsReloaded, reloads.OnEvent);
        var inspect = new Inspector(context, recorder, reloads);
        context.Rpc.RegisterReadOnly("diag.overview", async (_, rpcCt) => await inspect.OverviewAsync(rpcCt).ConfigureAwait(false),
            "Start here: app, process, plugins, models, agents with holders and waiters, active runs, model calls, running tools and processes, problems, the other diag methods");
        context.Rpc.RegisterReadOnly("diag.problems", async (_, rpcCt) => await inspect.ProblemsAsync(rpcCt).ConfigureAwait(false),
            "What looks wrong now, worst first → { severity: error|warn|info, area, message, hint }[]");
        context.Rpc.RegisterReadOnly("diag.calls", (req, _) => Task.FromResult<object?>(inspect.Calls(req)),
            "Model calls, newest first (running ones too): { limit? (50), sessionId?, runId?, agent?, errors?, running?, detail? } → { id, startedAt, state, model, purpose, agent, sessionId, runId, firstTokenMs, durationMs, attempts, tokens, stopReason, error }[]");
        context.Rpc.RegisterReadOnly("diag.call", (req, _) => Task.FromResult<object?>(inspect.Call(req)),
            "One model call in detail: { id } → the diag.calls fields plus the request's size, the response, retries, notices and the error's type/status");
        context.Rpc.RegisterReadOnly("diag.tools", (req, _) => Task.FromResult<object?>(inspect.Tools(req)),
            "Tool calls, newest first: { limit? (50), sessionId?, name?, errors?, running? } → { callId, name, sessionId, runId, startedAt, state, durationMs, arguments, result (preview) }[]");
        context.Rpc.RegisterReadOnly("diag.tool", (req, _) => Task.FromResult<object?>(inspect.Tool(req)),
            "One tool call in full: { callId, sessionId? } → { callId, name, sessionId, state, durationMs, arguments (parsed), isError, result (the text the model got), images, details }");
        context.Rpc.RegisterReadOnly("diag.journal", (req, _) => Task.FromResult<object?>(inspect.Journal(req)),
            "The events that matter as a timeline, oldest first (no per-token events): { limit? (100), type? (prefix), sessionId?, sinceSeq? } → { seq, time, type, sessionId, source, data }[]");
        context.Rpc.RegisterReadOnly("diag.run", async (req, rpcCt) => await inspect.RunAsync(req, rpcCt).ConfigureAwait(false),
            "One run in depth: { sessionId | agentId } → { run, statusSince, session, slot, queue, children, calls, tools, journal, messages }");
        context.Rpc.RegisterReadOnly("diag.settings", async (_, rpcCt) => await inspect.SettingsAsync(rpcCt).ConfigureAwait(false),
            "The settings document without secrets → { file, settings }");
        context.Rpc.RegisterReadOnly("diag.logs", async (req, rpcCt) => await inspect.LogsAsync(req, rpcCt).ConfigureAwait(false),
            "Log entries, oldest first: { limit? (200), level? (debug|info|warn|error: at least), category?, contains?, sinceMinutes? } → { time, level, category, message, exception }[]");
        context.Rpc.RegisterReadOnly("diag.failures", (req, _) => Task.FromResult<object?>(inspect.Failures(req)),
            "Failed requests the providers saved (logs/failed-requests), newest first: { limit? (20) } → { name, time, bytes, provider, model, sessionId, transport, requestId, responseId, error }[]");
        context.Rpc.RegisterReadOnly("diag.failure", (req, _) => Task.FromResult<object?>(inspect.Failure(req)),
            "One saved failed request with its body: { name, maxChars? (200000) } → { name, bytes, truncated, content }");
        context.Rpc.RegisterReadOnly("diag.toolsets", async (req, rpcCt) => await inspect.ToolSetsAsync(req.Required("sessionId"), rpcCt).ConfigureAwait(false),
            "A session's tools now and every change with its cause (the context plugin's context.toolsets): { sessionId } → { sessionId, tools, baseline, changes: [{ seq, time, added, removed, cause, plugins }], reloads }");

        var diag = new DiagnosticsService(context);
        context.Tools.Register(new DiagTool(context));
        context.Rpc.RegisterReadOnly("diag.snapshot", async (req, rpcCt) => await diag.SnapshotAsync(req.Int("events") ?? 200, rpcCt).ConfigureAwait(false),
            "Diagnostics overview → { plugins, tools, rpc, events, logs, runtime, time }");
        context.Rpc.RegisterReadOnly("diag.event", (req, _) => Task.FromResult<object?>(diag.Event(ParseSeq(req))),
            "Full data of a recent bus event: { seq } → { seq, type, sessionId, time, source, ui, data }");
        context.Rpc.Register("diag.reload", async (req, rpcCt) => await diag.ReloadAsync(req.Str("args") ?? req.Str("id"), rpcCt).ConfigureAwait(false),
            "/reload command: { args?: pluginId } → status text (no id = all plugins; the diag tool cannot call this)");

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
