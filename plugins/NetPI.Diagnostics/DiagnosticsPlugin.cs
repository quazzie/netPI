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
        context.Rpc.Register(new RpcMethod("diag.capabilities", "Plugin base registrations and optional capability availability; registry presence does not prove endpoint health", ReadOnly: true, Params: []),
            (_, _) => Task.FromResult<object?>(new
        {
            plugins = context.Services.Get<IPluginManager>()?.List().Select(p => new { p.Id, p.State, p.Error,
                methods = context.Rpc.List().Where(m => m.PluginId == p.Id).Select(m => m.Method).ToArray(),
                tools = context.Tools.Registrations.Where(t => t.PluginId == p.Id).Select(t => t.Tool.Definition.Name).ToArray() }).ToArray(),
            features = new[]
            {
                new { feature = "execution", available = context.Services.Get<IAgentRuntime>() is not null, reason = "Requires an executor capability" },
                new { feature = "scheduling", available = context.Services.Get<IAgentScheduler>() is not null, reason = "Requires a scheduler capability" },
                new { feature = "decisions", available = DecisionCapabilities.Available(context.Services), reason = "Requires the Decide plugin and a reachable endpoint" },
                new { feature = "gitHistory", available = context.Rpc.Exists("files.commits"), reason = "Requires the Files plugin and a repository" },
                new { feature = "contextPreview", available = context.Rpc.Exists("context.preview"), reason = "Requires a context preview provider" },
            },
        }));
        context.Rpc.Register(new RpcMethod("diag.overview", "Start here: app, process, plugins, models, agents with holders and waiters, active runs, model calls, running tools and processes, problems, the other diag methods", ReadOnly: true, Params: []),
            async (_, rpcCt) => await inspect.OverviewAsync(rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.problems", "What looks wrong now, worst first → { severity: error|warn|info, area, message, hint }[]", ReadOnly: true, Params: []),
            async (_, rpcCt) => await inspect.ProblemsAsync(rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.calls", "Model calls, newest first (running ones too): { limit? (50), sessionId?, runId?, agent?, errors?, running?, detail? } → { id, startedAt, state, model, purpose, agent, sessionId, runId, firstTokenMs, durationMs, attempts, tokens, stopReason, error }[]", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("sessionId"), RpcParam.Opt("runId"), RpcParam.Opt("agentId"), RpcParam.Opt("agent"), RpcParam.Opt("errors", RpcParamType.Boolean), RpcParam.Opt("running", RpcParamType.Boolean), RpcParam.Opt("detail", RpcParamType.Boolean)]),
            (req, _) => Task.FromResult<object?>(inspect.Calls(req)));
        context.Rpc.Register(new RpcMethod("diag.call", "One model call in detail: { id } → the diag.calls fields plus the request's size, the response, retries, notices and the error's type/status", ReadOnly: true, Params: [RpcParam.Opt("id"), RpcParam.Opt("correlationId")]),
            (req, _) => Task.FromResult<object?>(inspect.Call(req)));
        context.Rpc.Register(new RpcMethod("diag.tools", "Tool calls, newest first: { limit? (50), sessionId?, name?, errors?, running? } → { callId, name, sessionId, runId, startedAt, state, durationMs, arguments, result (preview) }[]", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("sessionId"), RpcParam.Opt("name"), RpcParam.Opt("errors", RpcParamType.Boolean), RpcParam.Opt("running", RpcParamType.Boolean)]),
            (req, _) => Task.FromResult<object?>(inspect.Tools(req)));
        context.Rpc.Register(new RpcMethod("diag.tool", "One tool call in full: { callId, sessionId? } → { callId, name, sessionId, state, durationMs, arguments (parsed), isError, result (the text the model got), images, details }", ReadOnly: true, Params: [RpcParam.Req("callId"), RpcParam.Opt("sessionId")]),
            (req, rpcCt) => Task.FromResult<object?>(inspect.Tool(req, rpcCt)));
        context.Rpc.Register(new RpcMethod("diag.journal", "The events that matter as a timeline, oldest first (no per-token events): { limit? (100), type? (prefix), sessionId?, sinceSeq? } → { seq, time, type, sessionId, source, data }[]", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("type", RpcParamType.String, "an event type or its prefix"), RpcParam.Opt("sessionId"), RpcParam.Opt("sinceSeq")]),
            (req, _) => Task.FromResult<object?>(inspect.Journal(req)));
        context.Rpc.Register(new RpcMethod("diag.run", "One run in depth: { sessionId | agentId } → { run, statusSince, session, slot, queue, children, calls, tools, journal, messages }", ReadOnly: true, Params: [RpcParam.Opt("sessionId"), RpcParam.Opt("agentId"), RpcParam.Opt("runId")]),
            async (req, rpcCt) => await inspect.RunAsync(req, rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.settings", "The settings document without secrets → { file, settings }", ReadOnly: true, Params: []),
            async (_, rpcCt) => await inspect.SettingsAsync(rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.logs", "Log entries, oldest first: { limit? (200, at most 2000), level? (debug|info|warn|error: at least), category?, contains?, sinceMinutes?, from?, to?, before? } → { time, level, category, message, exception }[]" +
            " — the newest `limit` matches. The live ring (this process's last 2000 lines) answers first; when it holds fewer matches than `limit`, or from/to/before (a date or time, local unless it has an offset; before is exclusive) names a window, the day files <home>/logs/netpi-YYYYMMDD.log do, so any day still on disk is reachable: page back by passing the first entry's time as `before`", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("level", RpcParamType.String, "debug, info, warn or error: at least"), RpcParam.Opt("category"), RpcParam.Opt("contains"), RpcParam.Opt("sinceMinutes", RpcParamType.Integer), RpcParam.Opt("from"), RpcParam.Opt("to"), RpcParam.Opt("before")]),
            async (req, rpcCt) => await inspect.LogsAsync(req, rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.search", "Find text in every stored session, newest first: { query?, regex?, scope? (all|text|tool-call|tool-result|tool), name? (a tool), errors? (failed tool results only), callId?, role? (user|assistant|tool|notice|summary), sessionId?, projectId?, includeArchived? (true), includeSubagents? (true), from?, to?, limit? (50, at most 500), cursor?, budgetSeconds? (20, at most 120) }" +
            " → { hits: [{ sessionId, title, seq, role, kind (text|tool-call|tool-result), toolName?, callId?, isError?, createdAt, snippet }], done, scanned: { sessions, messages, elapsedMs }, nextCursor?, note? }." +
            " Substring (or regex), case-insensitive. A tool, errors or a callId looks at tool parts only. Needs one of query, name, errors, callId, role. When the hit limit or the time budget ends the scan, done is false and nextCursor continues exactly there", ReadOnly: true,
            Params: [RpcParam.Opt("query"), RpcParam.Opt("regex", RpcParamType.Boolean), RpcParam.Opt("scope"), RpcParam.Opt("name"), RpcParam.Opt("errors", RpcParamType.Boolean), RpcParam.Opt("callId"), RpcParam.Opt("role"), RpcParam.Opt("sessionId"), RpcParam.Opt("projectId"), RpcParam.Opt("includeArchived", RpcParamType.Boolean), RpcParam.Opt("includeSubagents", RpcParamType.Boolean), RpcParam.Opt("from"), RpcParam.Opt("to"), RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("cursor"), RpcParam.Opt("budgetSeconds", RpcParamType.Integer)]),
            (req, rpcCt) => Task.FromResult<object?>(inspect.Search(req, rpcCt)));
        context.Rpc.Register(new RpcMethod("diag.errors", "What went wrong in one session, newest first: { sessionId, limit? (10) } → { sessionId, title, notices: [{ seq, time, text }] (the chat's error notices), failures: [{ name, time, bytes, model, requestId, error }] (saved failed requests: diag.failure), failedTools (since the app started) }", ReadOnly: true, Params: [RpcParam.Req("sessionId"), RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(inspect.Errors(req)));
        context.Rpc.Register(new RpcMethod("diag.failures", "Failed requests the providers saved (logs/failed-requests), newest first: { limit? (20) } → { name, time, bytes, provider, model, sessionId, transport, requestId, responseId, error }[]", ReadOnly: true, Params: [RpcParam.Opt("limit", RpcParamType.Integer)]),
            (req, _) => Task.FromResult<object?>(inspect.Failures(req)));
        context.Rpc.Register(new RpcMethod("diag.failure", "One saved failed request with its body: { name, offset?, limit? (or maxChars, default 200000), summary? } → { name, bytes, offset, returned, truncated, nextOffset?, content, shape? }" +
            " — offset/limit page through a body too large to hand over whole; summary adds its shape (the ordered input[]/messages[] kinds, no payload)", ReadOnly: true, Params: [RpcParam.Req("name"), RpcParam.Opt("offset", RpcParamType.Integer), RpcParam.Opt("limit", RpcParamType.Integer), RpcParam.Opt("maxChars", RpcParamType.Integer), RpcParam.Opt("summary", RpcParamType.Boolean)]),
            (req, _) => Task.FromResult<object?>(inspect.Failure(req)));
        context.Rpc.Register(new RpcMethod("diag.toolsets", "A session's tools now and every change with its cause (the context plugin's context.toolsets): { sessionId } → { sessionId, tools, baseline, changes: [{ seq, time, added, removed, cause, plugins }], reloads }", ReadOnly: true, Params: [RpcParam.Req("sessionId")]),
            async (req, rpcCt) => await inspect.ToolSetsAsync(req.Required("sessionId"), rpcCt).ConfigureAwait(false));

        var diag = new DiagnosticsService(context);
        context.Tools.Register(new DiagTool(context));
        context.Rpc.Register(new RpcMethod("diag.snapshot", "Diagnostics overview → { plugins, tools, rpc, events, logs, runtime, time }", ReadOnly: true, Params: [RpcParam.Opt("events", RpcParamType.Integer)]),
            async (req, rpcCt) => await diag.SnapshotAsync(req.Int("events") ?? 200, rpcCt).ConfigureAwait(false));
        context.Rpc.Register(new RpcMethod("diag.event", "Full data of a recent bus event: { seq } → { seq, type, sessionId, time, source, ui, data }", ReadOnly: true, Params: [RpcParam.Req("seq")]),
            (req, _) => Task.FromResult<object?>(diag.Event(ParseSeq(req))));
        context.Rpc.Register(new RpcMethod("diag.reload", "/reload command: { args?: pluginId } → status text (no id = all plugins; the diag tool cannot call this)", ReadOnly: false, Params: [RpcParam.Opt("args", RpcParamType.String, "/reload's text: a plugin id, or nothing for all"), RpcParam.Opt("id"), RpcParam.Opt("sessionId", RpcParamType.String, "the chat a slash command came from (unused)")]),
            async (req, rpcCt) => await diag.ReloadAsync(req.Str("args") ?? req.Str("id"), rpcCt).ConfigureAwait(false));

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
            // Background: reloading ourselves disposes this RPC handler; do it last. The plugin's stopping token ends the work
            // when the plugin goes away, and a fault of the task itself is observed and logged.
            Observe(Task.Run(async () =>
            {
                try { await pm.RescanAsync(ctx.Stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Plugin rescan failed"); }
                foreach (var p in plugins)
                {
                    if (p == self) continue;
                    try { await pm.ReloadAsync(p.Id, ctx.Stopping).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { logger.LogWarning(ex, "Reloading {Plugin} failed", p.Id); }
                }
                if (self is not null)
                {
                    try { await pm.ReloadAsync(self.Id, ctx.Stopping).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { logger.LogWarning(ex, "Reloading {Plugin} failed", self.Id); }
                }
            }, ctx.Stopping), logger);
            return $"Reloading {plugins.Count} plugins…";
        }

        var target = FindPlugin(pm.List(), arg);
        if (target.Id == ctx.PluginId)
        {
            // The same, for a single plugin: the stopping token ends it with the plugin, and a fault is observed and logged.
            Observe(Task.Run(async () =>
            {
                try { await pm.ReloadAsync(target.Id, ctx.Stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ctx.Logger.LogWarning(ex, "Reloading {Plugin} failed", target.Id); }
            }, ctx.Stopping), ctx.Logger);
            return $"Reloading {target.Name} ({target.Id})…";
        }
        await pm.ReloadAsync(target.Id, ct).ConfigureAwait(false);
        var after = pm.List().FirstOrDefault(p => p.Id == target.Id);
        return after is { State: "failed" }
            ? $"Reloaded {target.Id}, but it failed to start: {after.Error}"
            : $"Reloaded {target.Name} ({target.Id})";
    }

    /// <summary>A background reload task the RPC has already answered: a fault of the task itself (not of one reload inside it) is
    /// logged, not lost.</summary>
    private static void Observe(Task task, ILogger logger)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is not null) logger.LogWarning(t.Exception, "The plugin reload failed");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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
