using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Diagnostics;

/// <summary>
/// The <c>diag.*</c> views of the running app for people and debugging agents: an overview, the problems it can see,
/// model and tool calls, the event journal, one run in depth, a session's tool-set changes, the settings without
/// secrets, filtered logs and the failed requests the providers saved. Everything is read-only.
/// </summary>
public sealed partial class Inspector(IPluginContext ctx, Recorder recorder, Reloads reloads)
{
    /// <summary>Thresholds of the problem checks.</summary>
    public static readonly TimeSpan QueuedLong = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SilentLong = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan NoFirstToken = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ToolLong = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    // ---------------------------------------------------------------- overview

    public async Task<JsonObject> OverviewAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var calls = recorder.Calls();
        var recent = calls.Where(c => now - c.StartedAt <= RecentWindow).ToList();
        var done = recent.Where(c => c.State != "running").ToList();
        var deferred = ctx.Services.Get<IPluginManager>();
        var o = new JsonObject
        {
            ["time"] = now.ToString("O"),
            ["app"] = await RpcAsync("app.info", null, ct).ConfigureAwait(false),
            ["process"] = ProcessFacts(),
            ["plugins"] = PluginFacts(),
            ["models"] = ModelFacts(),
            ["agents"] = AgentFacts(),
            ["physicalOwners"] = NetPiJson.ToNode(ctx.Services.Get<IResourceLeases>()?.Snapshot()),
            ["runs"] = new JsonArray([.. ActiveRuns().Select(r => (JsonNode?)r)]),
            ["calls"] = new JsonObject
            {
                ["running"] = new JsonArray([.. calls.Where(c => c.State == "running").Select(c => (JsonNode?)c.ToJson())]),
                ["last15m"] = new JsonObject
                {
                    ["count"] = recent.Count,
                    ["errors"] = done.Count(c => c.State == "error"),
                    ["medianFirstTokenMs"] = Median(done.Select(c => c.FirstTokenMs)),
                    ["medianDurationMs"] = Median(done.Select(c => c.DurationMs)),
                },
            },
            ["tools"] = new JsonObject { ["running"] = new JsonArray([.. recorder.Tools().Where(t => t.EndedAt is null).Select(t => (JsonNode?)t.ToJson())]) },
            ["processes"] = await ProcessesAsync(ct).ConfigureAwait(false),
            ["reloads"] = reloads.ToJson(),
            ["deferred"] = deferred is null || Quietly(deferred) is not { Count: > 0 } ids ? null : new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()),
            ["problems"] = await ProblemsAsync(ct).ConfigureAwait(false),
            ["more"] = new JsonArray([.. ctx.Rpc.List().Where(m => m.Method.StartsWith("diag.", StringComparison.Ordinal))
                .OrderBy(m => m.Method, StringComparer.Ordinal)
                .Select(m => (JsonNode?)$"{m.Method}: {m.Description}")]),
        };
        return o;
    }

    private static long? Median(IEnumerable<long?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
        return list.Count == 0 ? null : list[list.Count / 2];
    }

    /// <summary>The plugins whose reload <c>plugins.quiet</c> is holding back, or null when the host cannot say.</summary>
    private static IReadOnlyList<string>? Quietly(IPluginManager pm)
    {
        try { return pm.Deferred(); }
        catch (Exception) { return null; }
    }

    private JsonObject ProcessFacts()
    {
        using var p = Process.GetCurrentProcess();
        ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        return new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["uptimeSeconds"] = (long)(DateTimeOffset.UtcNow - p.StartTime.ToUniversalTime()).TotalSeconds,
            ["workingSetMb"] = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            ["gcHeapMb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            ["gcCollections"] = $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}",
            ["cpuSeconds"] = Math.Round(p.TotalProcessorTime.TotalSeconds, 1),
            ["threads"] = p.Threads.Count,
            ["threadPool"] = new JsonObject
            {
                ["threads"] = ThreadPool.ThreadCount,
                ["busyWorkers"] = maxWorkers - freeWorkers,
                ["pendingWorkItems"] = ThreadPool.PendingWorkItemCount,
            },
            ["framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        };
    }

    private JsonObject PluginFacts()
    {
        var list = ctx.Services.Get<IPluginManager>()?.List() ?? [];
        return new JsonObject
        {
            ["running"] = list.Count(p => p.State == "running"),
            ["failed"] = new JsonArray([.. list.Where(p => p.State == "failed").Select(p => (JsonNode?)new JsonObject { ["id"] = p.Id, ["error"] = p.Error })]),
            ["disabled"] = new JsonArray([.. list.Where(p => !p.Enabled || p.State == "disabled").Select(p => (JsonNode?)p.Id)]),
        };
    }

    private JsonObject ModelFacts()
    {
        var models = ctx.Models.Cached;
        return new JsonObject
        {
            ["defaultModel"] = ctx.Models.DefaultModelRef,
            ["providers"] = new JsonArray([.. models.GroupBy(m => m.Provider).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (JsonNode?)new JsonObject
            {
                ["provider"] = g.Key,
                ["models"] = g.Count(),
                ["local"] = g.Any(m => m.IsLocal),
                ["statuses"] = string.Join(", ", g.GroupBy(m => m.Status ?? "?").Select(s => $"{s.Count()} {s.Key}")),
                ["loaded"] = new JsonArray([.. g.Where(m => m.Status == "loaded").Select(m => (JsonNode?)m.Ref)]),
            })]),
        };
    }

    private JsonArray AgentFacts()
    {
        var slots = ctx.Services.Get<IAgentScheduler>()?.Snapshot() ?? [];
        return new JsonArray([.. slots.Select(s => (JsonNode?)new JsonObject
        {
            ["key"] = s.Key,
            ["agent"] = s.Configured,
            ["model"] = s.Model ?? (s.Models.Count == 1 ? s.Models[0] : null),
            ["busy"] = $"{s.Busy}/{s.Capacity}",
            ["queued"] = s.Queued,
            ["state"] = s.Disabled ? "off" : !s.Available ? "unavailable: " + s.Unavailable : s.Status,
            ["holders"] = new JsonArray([.. s.Owners.Select(h => (JsonNode?)$"{h.Label ?? h.AgentId} ({h.AgentId}, since {h.Since:HH:mm:ss})")]),
            ["waiters"] = new JsonArray([.. s.Waiters.Select(h => (JsonNode?)$"{h.Label ?? h.AgentId} ({h.AgentId}, since {h.Since:HH:mm:ss})")]),
        })]);
    }

    /// <summary>The runs that are busy, with how long they have been in their status (from the journal).</summary>
    private List<JsonObject> ActiveRuns()
    {
        var runtime = ctx.Services.Get<IAgentRuntime>();
        if (runtime is null) return [];
        var since = StatusSince();
        return runtime.List(false)
            .Where(a => a.Status is AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded)
            .Select(a => new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["sessionId"] = a.SessionId,
                ["parentAgentId"] = a.ParentAgentId,
                ["status"] = JsonNamingPolicy.CamelCase.ConvertName(a.Status.ToString()),
                ["activity"] = a.Activity,
                ["agent"] = a.Agent,
                ["model"] = a.Model,
                ["statusSince"] = since.TryGetValue(a.Id, out var t) ? t.ToString("O") : a.StartedAt?.ToString("O"),
                ["turns"] = a.Turns,
                ["toolCalls"] = a.ToolCalls,
                ["children"] = a.Children.Count,
            })
            .ToList();
    }

    /// <summary>When each run last changed status (the newest agent.status of it in the journal).</summary>
    private Dictionary<string, DateTimeOffset> StatusSince()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var last = new Dictionary<string, string?>(StringComparer.Ordinal);
        // oldest first: a run's time is the first event of its current status
        foreach (var e in recorder.Journal().Where(e => e.Type == EventTypes.AgentStatus).Reverse())
        {
            if (e.Summary is not JsonObject d || Recorder.Str(d, "id") is not { } id) continue;
            var status = Recorder.Str(d, "status");
            if (!last.TryGetValue(id, out var prev) || prev != status) map[id] = e.Time;
            last[id] = status;
        }
        return map;
    }

    private async Task<JsonNode?> ProcessesAsync(CancellationToken ct)
    {
        if (await RpcAsync("processes.list", null, ct).ConfigureAwait(false) is not JsonArray list) return null;
        return new JsonObject
        {
            ["running"] = new JsonArray([.. list.OfType<JsonObject>().Where(p => (string?)p["status"] == "running")
                .Select(p => (JsonNode?)$"{p["pid"]} {Recorder.Preview((string?)p["command"], 120)} (since {p["startedAt"]})")]),
            ["total"] = list.Count,
        };
    }

    // ---------------------------------------------------------------- problems

    /// <summary>What looks wrong now, worst first: { severity: error|warn|info, area, message, hint? }.</summary>
    public async Task<JsonArray> ProblemsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<(int Rank, JsonObject Problem)>();
        void Add(string severity, string area, string message, string? hint = null) =>
            list.Add((severity switch { "error" => 0, "warn" => 1, _ => 2 }, new JsonObject
            {
                ["severity"] = severity, ["area"] = area, ["message"] = message, ["hint"] = hint,
            }));

        // plugins that failed to start
        foreach (var p in ctx.Services.Get<IPluginManager>()?.List() ?? [])
            if (p.State == "failed") Add("error", "plugins", $"Plugin {p.Id} failed: {p.Error}", "diag.logs { contains: \"" + p.Id + "\" }; plugins.reload { id }");

        // a reload takes every running chat's tools away for a moment: say who did it, and what it cost
        foreach (var r in reloads.Recent(RecentWindow))
            Add("info", "plugins", $"{r.Summary} {Ago(now - r.Time)} ago — {r.Impact}.",
                r.Tools.Count == 0 ? "diag.reloads" : "diag.toolsets { sessionId } of a chat that holds one of those tools; diag.logs { contains: \"Reloading plugin\" }");

        // what plugins.quiet is holding back, if it is on
        if (ctx.Services.Get<IPluginManager>() is { } manager && Quietly(manager) is { Count: > 0 } held)
            Add("info", "plugins", $"plugins.quiet is on: {held.Count} plugin reload(s) are waiting ({string.Join(", ", held)}); the running versions keep serving.",
                "settings: plugins.quiet = false applies them; diag.overview .deferred");

        // providers whose models are all offline
        foreach (var g in ctx.Models.Cached.GroupBy(m => m.Provider))
            if (g.All(m => m.Status == "offline"))
                Add("warn", "models", $"Provider {g.Key} can't be reached: its {g.Count()} models are offline.", "diag.logs { contains: \"" + g.Key + "\" }");

        // agents: waiting on one that can't take work, runs queued long
        var slots = ctx.Services.Get<IAgentScheduler>()?.Snapshot() ?? [];
        foreach (var s in slots)
        {
            if (!s.Available && s.Waiters.Count > 0)
                Add("error", "agents", $"{s.Waiters.Count} run(s) wait on agent {s.Key}, which can't take work: {s.Unavailable}.");
            foreach (var w in s.Waiters.Where(w => now - w.Since > QueuedLong))
                Add("warn", "agents", $"{w.Label ?? w.AgentId} ({w.AgentId}) has waited {Ago(now - w.Since)} for {s.Key} ({s.Busy}/{s.Capacity} busy: {string.Join(", ", s.Owners.Select(o => o.Label ?? o.AgentId))}).",
                    "diag.run { agentId } of the holders: are they stuck?");
        }
        foreach (var s in slots.Where(s => s.Configured && !s.Available && !s.Disabled && s.Waiters.Count == 0))
            Add("info", "agents", $"Agent {s.Key} is inactive: {s.Unavailable}.");

        // runs that went quiet: running, but no event of their chat for a while
        var lastEvent = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var e in recorder.Journal())
            if (e.SessionId is { } sid && !lastEvent.ContainsKey(sid)) lastEvent[sid] = e.Time;
        foreach (var a in ctx.Services.Get<IAgentRuntime>()?.List(false) ?? [])
        {
            if (a.Status != AgentStatus.Running) continue;
            var last = lastEvent.TryGetValue(a.SessionId, out var t) ? t : a.StartedAt ?? recorder.StartedAt;
            if (now - last > SilentLong && now - recorder.StartedAt > SilentLong)
                Add("warn", "runs", $"{a.Name} ({a.Id}) has been running without any event for {Ago(now - last)} (activity: {a.Activity ?? "?"}).", "diag.run { agentId }");
        }

        // model calls: running without a first token, recent errors
        foreach (var c in recorder.Calls().Where(c => c.State == "running" && c.FirstTokenMs is null && now - c.StartedAt > NoFirstToken))
            Add("warn", "calls", $"Model call {c.Id} to {c.Provider}/{c.Model} has run {Ago(now - c.StartedAt)} without a first token.", "diag.call { id }");
        var errors = recorder.Calls().Where(c => c.State == "error" && now - c.StartedAt <= RecentWindow).ToList();
        if (errors.Count > 0)
            Add("warn", "calls", $"{errors.Count} model call(s) failed in the last {RecentWindow.TotalMinutes:0} min; last: {(string?)errors[0].ToJson()["error"]}", "diag.calls { errors: true }; diag.failures");

        // tools running long
        foreach (var t in recorder.Tools().Where(t => t.EndedAt is null && now - t.StartedAt > ToolLong))
            Add("warn", "tools", $"Tool {t.Name} ({t.CallId}) has run {Ago(now - t.StartedAt)}.", "diag.run { sessionId }; processes.list");

        // logs: errors and warnings lately
        if (await RpcAsync("logs.recent", new JsonObject { ["max"] = 2000 }, ct).ConfigureAwait(false) is JsonArray logs)
        {
            var bad = logs.OfType<JsonObject>().Where(l => IsLevel(l, "err", "crt") && Recent(l, now)).ToList();
            var warn = logs.OfType<JsonObject>().Where(l => IsLevel(l, "wrn") && Recent(l, now)).ToList();
            if (bad.Count > 0) Add("error", "logs", $"{bad.Count} error(s) logged in the last {RecentWindow.TotalMinutes:0} min; last: [{bad[^1]["category"]}] {Recorder.Preview((string?)bad[^1]["message"], 200)}", "diag.logs { level: \"error\" }");
            if (warn.Count > 0) Add("info", "logs", $"{warn.Count} warning(s) logged in the last {RecentWindow.TotalMinutes:0} min; last: [{warn[^1]["category"]}] {Recorder.Preview((string?)warn[^1]["message"], 200)}", "diag.logs { level: \"warn\" }");
        }

        // the thread pool falling behind
        if (ThreadPool.PendingWorkItemCount > 100)
            Add("warn", "process", $"{ThreadPool.PendingWorkItemCount} work items wait for the thread pool ({ThreadPool.ThreadCount} threads): something blocks threads.");

        // the budget
        if (await RpcAsync("budget.status", null, ct).ConfigureAwait(false) is JsonObject b)
        {
            if (b["exhausted"] is JsonValue ex && ex.TryGetValue<bool>(out var spent) && spent)
                Add("warn", "budget", $"The budget is spent ({b["spentUsd"]} of {b["monthlyUsd"] ?? b["dailyUsd"]} $): paid calls stop or ask.");
        }

        // failed requests saved lately
        var failures = FailureFiles().Where(f => now - f.LastWriteTimeUtc <= TimeSpan.FromHours(1)).ToList();
        if (failures.Count > 0)
            Add("info", "calls", $"{failures.Count} failed request(s) saved in the last hour; newest: {failures[0].Name}.", "diag.failures; diag.failure { name }");

        return new JsonArray([.. list.OrderBy(p => p.Rank).Select(p => (JsonNode?)p.Problem)]);
    }

    /// <summary>logs.recent levels: trc, dbg, inf, wrn, err, crt.</summary>
    private static bool IsLevel(JsonObject l, params string[] levels) => levels.Contains(((string?)l["level"] ?? "").ToLowerInvariant());

    private static bool Recent(JsonObject l, DateTimeOffset now) =>
        l["time"] is JsonValue v && DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) && now - t <= RecentWindow;

    internal static string Ago(TimeSpan d) =>
        d.TotalSeconds < 90 ? $"{d.TotalSeconds:0} s" : d.TotalMinutes < 90 ? $"{d.TotalMinutes:0} min" : $"{d.TotalHours:0.#} h";

    // ---------------------------------------------------------------- calls, tools, journal

    public JsonArray Calls(RpcRequest r)
    {
        var limit = Math.Clamp(r.Int("limit") ?? 50, 1, Recorder.CallCapacity);
        var sid = r.Str("sessionId");
        var run = r.Str("runId") ?? r.Str("agentId");
        var agent = r.Str("agent");
        var errors = r.Bool("errors") == true;
        var running = r.Bool("running") == true;
        return new JsonArray([.. recorder.Calls()
            .Where(c => (sid is null || c.SessionId == sid) && (run is null || c.RunId == run) && (agent is null || c.Agent == agent)
                        && (!errors || c.State == "error") && (!running || c.State == "running"))
            .Take(limit)
            .Select(c => (JsonNode?)c.ToJson(r.Bool("detail") == true))]);
    }

    public JsonObject Call(RpcRequest r)
    {
        if (r.Str("correlationId") is { } correlation)
            return recorder.Calls().FirstOrDefault(c => c.CorrelationId == correlation)?.ToJson(detail: true)
                ?? throw new RpcException("not_found", "The correlated call is unavailable in this Diagnostics instance (it may predate a reload).");
        var id = long.TryParse(r.Required("id"), out var n) ? n : throw new RpcException("bad_request", "'id' must be a number");
        return recorder.Call(id)?.ToJson(detail: true) ?? throw new RpcException("not_found", $"Call {id} is no longer in the call log (it keeps the last {Recorder.CallCapacity}).");
    }

    public JsonArray Tools(RpcRequest r)
    {
        var limit = Math.Clamp(r.Int("limit") ?? 50, 1, Recorder.ToolCapacity);
        var sid = r.Str("sessionId");
        var name = r.Str("name");
        var errors = r.Bool("errors") == true;
        var running = r.Bool("running") == true;
        var rows = recorder.Tools()
            .Where(t => (sid is null || t.SessionId == sid) && (name is null || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                        && (!errors || (t.EndedAt is not null && t.IsError)) && (!running || t.EndedAt is null))
            .Take(limit).ToList();
        var results = ToolParts(rows.Select(t => t.SessionId));
        return new JsonArray([.. rows.Select(t =>
        {
            var j = t.ToJson();
            if (results.TryGetValue(t.CallId, out var found) && found.Result is { } res) j["result"] = Recorder.Preview(res.Content, 300);
            return (JsonNode?)j;
        })]);
    }

    /// <summary>
    /// One tool call in full: its arguments and its result as the model got it (text, error flag, images, details), from
    /// the chat's messages. <c>{ callId, sessionId? }</c>: the session is found in the tool log, or must be given.
    /// </summary>
    public JsonObject Tool(RpcRequest r)
    {
        var callId = r.Required("callId");
        var record = recorder.Tools().FirstOrDefault(t => t.CallId == callId);
        var sessionId = r.Str("sessionId") ?? record?.SessionId
                        ?? throw new RpcException("not_found", $"Tool call {callId} is no longer in the tool log (it keeps the last {Recorder.ToolCapacity}): pass its sessionId.");
        var parts = ToolParts([sessionId], 2000);
        if (!parts.TryGetValue(callId, out var found) && record is null)
            throw new RpcException("not_found", $"No tool call {callId} in the last 2000 messages of {sessionId}.");
        var o = record?.ToJson() ?? new JsonObject { ["callId"] = callId, ["sessionId"] = sessionId };
        if (found.Call is { } call)
        {
            o["name"] = call.Name;
            try { o["arguments"] = JsonNode.Parse(call.Arguments); } catch (JsonException) { o["arguments"] = call.Arguments; }
        }
        if (found.Result is { } res)
        {
            o["isError"] = res.IsError;
            o["result"] = res.Content.Length > 50_000 ? res.Content[..50_000] + $"\n… ({res.Content.Length} characters)" : res.Content;
            o["images"] = res.Images?.Count ?? 0;
            o["details"] = res.Details?.DeepClone();
            o["resultDurationMs"] = res.DurationMs;
        }
        else o["result"] = null;
        return o;
    }

    /// <summary>The tool calls and results in the recent messages of these sessions, by call id.</summary>
    private Dictionary<string, (ToolCallPart? Call, ToolResultPart? Result)> ToolParts(IEnumerable<string?> sessionIds, int messages = 300)
    {
        var map = new Dictionary<string, (ToolCallPart? Call, ToolResultPart? Result)>();
        foreach (var sid in sessionIds.Where(x => x is not null).Distinct())
        {
            foreach (var m in ctx.Sessions.GetMessages(sid!, null, messages))
            {
                foreach (var c in m.ToolCalls) map[c.Id] = (c, map.GetValueOrDefault(c.Id).Result);
                foreach (var t in m.ToolResults) map[t.CallId] = (map.GetValueOrDefault(t.CallId).Call, t);
            }
        }
        return map;
    }

    public JsonArray Journal(RpcRequest r)
    {
        var limit = Math.Clamp(r.Int("limit") ?? 100, 1, Recorder.JournalCapacity);
        var type = r.Str("type");
        var sid = r.Str("sessionId");
        var since = long.TryParse(r.Str("sinceSeq"), out var s) ? s : (long?)null;
        var entries = recorder.Journal()
            .Where(e => (type is null || e.Type.StartsWith(type, StringComparison.Ordinal)) && (sid is null || e.SessionId == sid) && (since is null || e.Seq > since))
            .Take(limit)
            .Reverse(); // oldest first: reads as a timeline
        return new JsonArray([.. entries.Select(e => (JsonNode?)e.ToJson())]);
    }

    // ---------------------------------------------------------------- one run in depth

    public async Task<JsonObject> RunAsync(RpcRequest r, CancellationToken ct)
    {
        var runtime = ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime (netpi.runtime) is not running.");
        var info = (r.Str("agentId") ?? r.Str("runId")) is { } id ? runtime.Get(id)
                 : r.Str("sessionId") is { } sid ? runtime.GetBySession(sid)
                 : throw new RpcException("bad_request", "Pass sessionId or agentId.");
        var sessionId = info?.SessionId ?? r.Str("sessionId") ?? throw new RpcException("not_found", "No such run.");
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
        var slots = ctx.Services.Get<IAgentScheduler>()?.Snapshot() ?? [];
        var o = new JsonObject
        {
            ["run"] = info is null ? null : NetPiJson.ToNode(info),
            ["statusSince"] = info is not null && StatusSince().TryGetValue(info.Id, out var t) ? t.ToString("O") : null,
            ["session"] = new JsonObject
            {
                ["id"] = session.Id, ["title"] = session.Title, ["kind"] = session.Kind, ["model"] = session.Model, ["reasoning"] = session.Reasoning,
                ["projectId"] = session.ProjectId, ["parentSessionId"] = session.ParentSessionId, ["messageCount"] = session.MessageCount,
                ["contextTokens"] = session.ContextTokens, ["meta"] = session.Meta?.DeepClone() is JsonObject meta ? Trim(meta) : null,
            },
            ["slot"] = info is null ? null : new JsonArray([.. slots.SelectMany(s =>
                s.Owners.Where(h => h.AgentId == info.Id).Select(h => (JsonNode?)$"holds {s.Key} since {h.Since:O}")
                    .Concat(s.Waiters.Where(h => h.AgentId == info.Id).Select(h => (JsonNode?)$"waits for {s.Key} since {h.Since:O} ({s.Busy}/{s.Capacity} busy: {string.Join(", ", s.Owners.Select(x => x.Label ?? x.AgentId))})")))]),
            ["queue"] = new JsonArray([.. runtime.GetQueue(sessionId).Select(q => (JsonNode?)$"{q.Mode}: {Recorder.Preview(q.Text, 160)}")]),
            ["children"] = info is null ? null : new JsonArray([.. runtime.List(true).Where(a => a.ParentAgentId == info.Id).Select(a => (JsonNode?)new JsonObject
            {
                ["id"] = a.Id, ["name"] = a.Name, ["sessionId"] = a.SessionId,
                ["status"] = JsonNamingPolicy.CamelCase.ConvertName(a.Status.ToString()), ["activity"] = a.Activity, ["agent"] = a.Agent,
            })]),
            ["calls"] = new JsonArray([.. recorder.Calls().Where(c => c.SessionId == sessionId).Take(10).Select(c => (JsonNode?)c.ToJson())]),
            ["tools"] = new JsonArray([.. recorder.Tools().Where(x => x.SessionId == sessionId).Take(20).Select(x => (JsonNode?)x.ToJson())]),
            ["journal"] = new JsonArray([.. recorder.Journal().Where(e => e.SessionId == sessionId).Take(40).Reverse().Select(e => (JsonNode?)e.ToJson())]),
            ["messages"] = new JsonArray([.. ctx.Sessions.GetMessages(sessionId, null, 12).Select(m => (JsonNode?)new JsonObject
            {
                ["seq"] = m.Seq, ["time"] = m.CreatedAt.ToString("O"), ["role"] = JsonNamingPolicy.CamelCase.ConvertName(m.Role.ToString()),
                ["kind"] = (string?)m.Meta?["kind"], ["stopReason"] = m.StopReason, ["compacted"] = m.Compacted,
                ["text"] = Recorder.Preview(string.Join(" ", m.Parts.OfType<TextPart>().Select(p => p.Text)), 200),
                ["toolCalls"] = m.ToolCalls.Any() ? string.Join("; ", m.ToolCalls.Select(c => $"{c.Name} {Recorder.Preview(c.Arguments, 150)} ({c.Id})")) : null,
                ["toolResults"] = m.ToolResults.Any() ? string.Join("; ", m.ToolResults.Select(x => $"{x.Name}{(x.IsError ? " (error)" : "")}: {Recorder.Preview(x.Content, 300)}")) : null,
            })]),
        };
        return await Task.FromResult(o).ConfigureAwait(false);
    }

    /// <summary>Long meta values (a profile's prompt, instructions) cut short.</summary>
    private static JsonObject Trim(JsonObject meta)
    {
        foreach (var (k, v) in meta.ToList())
            if (v is JsonValue s && s.TryGetValue<string>(out var text) && text.Length > 300) meta[k] = text[..300] + "…";
        return meta;
    }

    // ---------------------------------------------------------------- settings, logs, failed requests

    /// <summary>The settings document with every secret replaced ("env:NAME" references are kept).</summary>
    public async Task<JsonObject> SettingsAsync(CancellationToken ct)
    {
        var secretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (await RpcAsync("settings.schema", null, ct).ConfigureAwait(false) is JsonArray schema)
            foreach (var section in schema.OfType<JsonObject>())
                foreach (var s in (section["settings"] as JsonArray ?? []).OfType<JsonObject>())
                    if ((string?)s["type"] == "secret" && (string?)s["key"] is { } key) secretKeys.Add(key);
        var doc = ctx.Settings.Snapshot();
        Redact(doc, "", secretKeys);
        return new JsonObject { ["file"] = ctx.Paths.SettingsFile, ["settings"] = doc };
    }

    [GeneratedRegex("(api[-_]?key|token|secret|password|passwd|authorization|bearer|cookie)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    internal static void Redact(JsonObject node, string path, HashSet<string> secretKeys)
    {
        foreach (var (key, value) in node.ToList())
        {
            var p = path.Length == 0 ? key : path + "." + key;
            if (value is JsonObject child) Redact(child, p, secretKeys);
            else if (value is JsonValue v && v.TryGetValue<string>(out var s) && (secretKeys.Contains(p) || SecretName().IsMatch(key)))
                node[key] = s.Length == 0 ? "" : s.StartsWith("env:", StringComparison.Ordinal) || s.StartsWith('$') ? s : $"<secret, {s.Length} chars>";
        }
    }

    public async Task<JsonArray> LogsAsync(RpcRequest r, CancellationToken ct)
    {
        var limit = Math.Clamp(r.Int("limit") ?? 200, 1, 2000);
        var min = (r.Str("level") ?? "debug").ToLowerInvariant() switch
        {
            "error" or "err" => 4, "warn" or "warning" or "wrn" => 3, "info" or "inf" => 2, "trace" => 0, _ => 1,
        };
        var category = r.Str("category");
        var contains = r.Str("contains");
        var sinceMinutes = r.Int("sinceMinutes");
        var now = DateTimeOffset.UtcNow;
        if (await RpcAsync("logs.recent", new JsonObject { ["max"] = 2000 }, ct).ConfigureAwait(false) is not JsonArray logs) return [];
        static int Rank(string? level) => (level ?? "").ToLowerInvariant() switch
        {
            var l when l.StartsWith("tr", StringComparison.Ordinal) => 0,
            var l when l.StartsWith("d", StringComparison.Ordinal) => 1,
            var l when l.StartsWith("i", StringComparison.Ordinal) => 2,
            var l when l.StartsWith("w", StringComparison.Ordinal) => 3,
            _ => 4,
        };
        var hits = logs.OfType<JsonObject>()
            .Where(l => Rank((string?)l["level"]) >= min
                        && (category is null || ((string?)l["category"] ?? "").Contains(category, StringComparison.OrdinalIgnoreCase))
                        && (contains is null || $"{l["message"]} {l["exception"]}".Contains(contains, StringComparison.OrdinalIgnoreCase))
                        && (sinceMinutes is null || (l["time"] is JsonValue v && DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) && now - t <= TimeSpan.FromMinutes(sinceMinutes.Value))))
            .TakeLast(limit)
            .Select(l => (JsonNode?)l.DeepClone());
        return new JsonArray([.. hits]);
    }

    private IEnumerable<FileInfo> FailureFiles()
    {
        var dir = new DirectoryInfo(Path.Combine(ctx.Paths.LogsDir, "failed-requests"));
        return dir.Exists ? dir.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc) : [];
    }

    /// <summary>The failed requests the providers saved (logs/failed-requests): when, which model, the error, the server's ids.</summary>
    public JsonArray Failures(RpcRequest r)
    {
        var limit = Math.Clamp(r.Int("limit") ?? 20, 1, 100);
        var list = new JsonArray();
        foreach (var f in FailureFiles().Take(limit))
        {
            var o = new JsonObject { ["name"] = f.Name, ["time"] = f.LastWriteTimeUtc.ToString("O"), ["bytes"] = f.Length };
            try
            {
                using var stream = f.OpenRead();
                if (JsonNode.Parse(stream) is JsonObject doc)
                    foreach (var key in (string[])["provider", "model", "sessionId", "transport", "requestId", "responseId", "error"])
                        o[key] = doc[key]?.DeepClone();
            }
            catch (Exception ex) { o["unreadable"] = ex.Message; }
            list.Add(o);
        }
        return list;
    }

    /// <summary>One saved failed request with its request body (cut at <c>maxChars</c>).</summary>
    public JsonObject Failure(RpcRequest r)
    {
        var name = r.Required("name");
        if (Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new RpcException("bad_request", "'name' is a file name from diag.failures");
        var file = new FileInfo(Path.Combine(ctx.Paths.LogsDir, "failed-requests", name));
        if (!file.Exists) throw new RpcException("not_found", $"No saved request {name}");
        var max = Math.Clamp(r.Int("maxChars") ?? 200_000, 1000, 5_000_000);
        var text = File.ReadAllText(file.FullName);
        return new JsonObject
        {
            ["name"] = name, ["bytes"] = file.Length, ["truncated"] = text.Length > max,
            ["content"] = text.Length > max ? text[..max] : text,
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A session's tools now and every change with its cause. The context plugin keeps the baseline and the notices
    /// (it is what tells the model), so this is its <c>context.toolsets</c> as-is; without that plugin there is
    /// nothing to replay.
    /// </summary>
    public async Task<JsonObject> ToolSetsAsync(string sessionId, CancellationToken ct)
    {
        var node = await RpcAsync("context.toolsets", new JsonObject { ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
        if (node is JsonObject o && o["error"] is { } error)
            throw new RpcException("unavailable", $"context.toolsets failed: {error}");
        return node as JsonObject ?? throw new RpcException("unavailable", "The context plugin is not loaded, so no tool-set history is kept.");
    }

    private async Task<JsonNode?> RpcAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists(method)) return null;
        try { return NetPiJson.ToNode(await ctx.Rpc.InvokeAsync(method, parameters, ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
    }
}
