using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Coordinator;

/// <summary>
/// A project's task queue for coordinators (idea-lsj5u2): work packages with a brief, an agent, a profile and a workspace
/// (what <c>sessions.dispatch</c> takes), and <c>after</c> conditions on other tasks: done (its run ended well) or merged
/// (its workspace branch is an ancestor of the project checkout's HEAD). The queue dispatches a task when its conditions
/// hold and its agent has a free instance, through <c>sessions.dispatch</c>, and records how its run ended; merging stays
/// with the coordinator or the user. It replaces the coordinator's markdown table of preconditions and its watcher that
/// dispatched by hand when a worker went idle.
/// <para>A task: <c>{ id, projectId, title, text, agent?, profile?, workspace?, after: [{ task, when: done|merged }], hold,
/// status: queued|running|done|failed|cancelled, sessionId?, branch?, error?, lastError?, order, createdAt, dispatchedAt?,
/// finishedAt? }</c>. The pump runs when a run's status or the agents' capacity changes, when a task changes, and every
/// 30 s (a merge happens outside NetPI).</para>
/// </summary>
internal sealed class TaskQueue : IAsyncDisposable
{
    public const string ChangedEvent = "tasks.changed";
    public const string AutoDispatchSetting = "tasks.autoDispatch";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly IPluginContext _ctx;
    private readonly IDataCollection _tasks;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Task _loop;
    private int _pumping;
    private int _again;

    public TaskQueue(IPluginContext ctx)
    {
        _ctx = ctx;
        _tasks = ctx.Data.Collection("tasks", new CollectionSpec().Text("projectId").Text("status").Integer("order"));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
        _subscriptions.Add(ctx.Events.Subscribe(EventTypes.AgentStatus, _ => Kick()));
        _subscriptions.Add(ctx.Events.Subscribe(AgentSchedulerEvents.Changed, _ => Kick()));
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Every);
            try { while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) Kick(); }
            catch (OperationCanceledException) { }
        });
        Kick();
    }

    // ------------------------------------------------------------------ RPC

    public async Task<object?> AddAsync(RpcRequest req, Coordinator coordinator, CancellationToken ct)
    {
        var project = ResolveProject(req.Required("project"));
        var title = req.Str("title") is { Length: > 0 } t ? t.Trim() : throw new RpcException("bad_request", "A task needs a title.");
        var text = await TextAsync(req, ct).ConfigureAwait(false) ?? throw new RpcException("bad_request", "A task needs its brief: text or textFile.");
        // the agent and the profile are checked now, as a dispatch would check them, so a typo fails here and not at dispatch time
        var agent = req.Str("agent") is { Length: > 0 } a ? await coordinator.ResolveAgentForTaskAsync(a, ct).ConfigureAwait(false) : null;
        var after = After(req.Prop("after"), project.Id, selfId: null);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = "task-" + Guid.NewGuid().ToString("N")[..8];
            var order = _tasks.Find(new DataQuery().Eq("projectId", project.Id)).Select(d => Num(d.Doc["order"])).DefaultIfEmpty(0).Max() + 1;
            var doc = new JsonObject
            {
                ["id"] = id, ["projectId"] = project.Id, ["title"] = title, ["text"] = text,
                ["agent"] = agent, ["profile"] = req.Str("profile"),
                ["workspace"] = req.Prop("workspace") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w ? JsonNode.Parse(w.GetRawText()) : null,
                ["after"] = after, ["hold"] = req.Bool("hold") ?? false, ["status"] = "queued", ["order"] = order,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            _tasks.Put(id, doc);
            Changed(doc);
            Kick();
            return await ShownAsync(doc, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<object?> ListAsync(RpcRequest req, CancellationToken ct)
    {
        var query = new DataQuery();
        if (req.Str("project") is { Length: > 0 } p) query = query.Eq("projectId", ResolveProject(p).Id);
        if (req.Str("status") is { Length: > 0 } s) query = query.Eq("status", s.Trim().ToLowerInvariant());
        var shown = new JsonArray();
        foreach (var d in _tasks.Find(query).OrderBy(d => d.Doc["projectId"]?.GetValue<string>(), StringComparer.Ordinal).ThenBy(d => Num(d.Doc["order"])))
            shown.Add(await ShownAsync(d.Doc, ct).ConfigureAwait(false));
        return shown;
    }

    public async Task<object?> UpdateAsync(RpcRequest req, Coordinator coordinator, CancellationToken ct)
    {
        var id = req.Required("id");
        var agent = req.Str("agent") is { Length: > 0 } a ? await coordinator.ResolveAgentForTaskAsync(a, ct).ConfigureAwait(false) : null;
        var text = await TextAsync(req, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = _tasks.Get(id) ?? throw new RpcException("not_found", $"No task {id}.");
            var status = Str(doc, "status");
            if (req.Str("title") is { Length: > 0 } title) doc["title"] = title.Trim();
            if (text is not null) doc["text"] = text;
            if (agent is not null) doc["agent"] = agent;
            if (req.Str("profile") is { } profile) doc["profile"] = profile.Length == 0 ? null : profile;
            if (req.Prop("workspace") is { } w) doc["workspace"] = w.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : JsonNode.Parse(w.GetRawText());
            if (req.Prop("after") is { } afterArg)
            {
                var after = After(afterArg, Str(doc, "projectId")!, selfId: id);
                if (Cycles(id, after)) throw new RpcException("bad_request", "These conditions would make the tasks wait for each other in a circle.");
                doc["after"] = after;
            }
            if (req.Bool("hold") is { } hold) doc["hold"] = hold;
            if (req.Str("status") is { Length: > 0 } wanted)
            {
                wanted = wanted.Trim().ToLowerInvariant();
                if (wanted == "cancelled" && status is "queued" or "running") doc["status"] = "cancelled";
                else if (wanted == "queued" && status is "failed" or "cancelled" or "done")
                {
                    // run it again: a new chat at its next dispatch
                    doc["status"] = "queued";
                    foreach (var k in new[] { "sessionId", "branch", "error", "lastError", "dispatchedAt", "finishedAt" }) doc.Remove(k);
                }
                else if (wanted != status) throw new RpcException("bad_request", $"A {status} task cannot become {wanted} (cancelled: a queued or running one; queued: a failed, cancelled or done one, to run it again).");
            }
            _tasks.Put(id, doc);
            Changed(doc);
            Kick();
            return await ShownAsync(doc, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<object?> RemoveAsync(RpcRequest req, CancellationToken ct)
    {
        var id = req.Required("id");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = _tasks.Get(id) ?? throw new RpcException("not_found", $"No task {id}.");
            _tasks.Delete(id);
            doc["status"] = "removed";
            Changed(doc);
            return true;
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ the pump

    /// <summary>Run the pump soon; calls that arrive while it runs make it run once more, never twice at once.</summary>
    public void Kick()
    {
        Interlocked.Exchange(ref _again, 1);
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { while (Interlocked.Exchange(ref _again, 0) == 1 && !_stop.IsCancellationRequested) await PumpAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ctx.Logger.LogWarning(ex, "tasks: the queue could not be pumped"); }
            finally
            {
                Volatile.Write(ref _pumping, 0);
                if (Volatile.Read(ref _again) == 1 && !_stop.IsCancellationRequested) Kick();
            }
        });
    }

    /// <summary>Record how running tasks ended, then dispatch the ready ones whose agent has a free instance.</summary>
    internal async Task PumpAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = _tasks.Find().Select(d => d.Doc).ToList();
            foreach (var task in all.Where(t => Str(t, "status") == "running")) Finish(task);
            if (!_ctx.Settings.Get(AutoDispatchSetting, true)) return;
            var free = FreeInstances(all);
            foreach (var task in all.Where(t => Str(t, "status") == "queued" && !Flag(t, "hold"))
                         .OrderBy(t => Str(t, "projectId"), StringComparer.Ordinal).ThenBy(t => Num(t["order"])))
            {
                ct.ThrowIfCancellationRequested();
                if ((await WaitingForAsync(task, all, ct).ConfigureAwait(false)).Count > 0) continue;
                var agent = Pick(free, Str(task, "agent"));
                if (free is not null && agent is null) continue; // no free instance for it now
                await DispatchAsync(task, agent, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>A running task whose chat's run has ended: done, or failed with the reason.</summary>
    private void Finish(JsonObject task)
    {
        var sessionId = Str(task, "sessionId");
        var info = sessionId is null ? null : _ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        if (sessionId is not null && _ctx.Sessions.GetSession(sessionId) is null)
        {
            End(task, "failed", "its chat was deleted");
            return;
        }
        if (info is null) return;
        var dispatched = DateTimeOffset.TryParse(Str(task, "dispatchedAt"), out var at) ? at : DateTimeOffset.MinValue;
        if (info.FinishedAt is not { } finished || finished < dispatched) return; // the run has not ended (or not started) yet
        switch (info.Status)
        {
            case AgentStatus.Idle or AgentStatus.Completed when string.IsNullOrEmpty(info.Error):
                End(task, "done", null); break;
            case AgentStatus.Idle or AgentStatus.Completed or AgentStatus.Failed:
                End(task, "failed", info.Error ?? "its run failed"); break;
            case AgentStatus.Cancelled:
                End(task, "failed", "its run was stopped"); break;
        }
    }

    private void End(JsonObject task, string status, string? error)
    {
        task["status"] = status;
        task["error"] = error;
        task["finishedAt"] = DateTimeOffset.UtcNow.ToString("O");
        _tasks.Put(Str(task, "id")!, task);
        Changed(task);
    }

    private async Task DispatchAsync(JsonObject task, string? agent, CancellationToken ct)
    {
        var id = Str(task, "id")!;
        var p = new JsonObject
        {
            ["project"] = Str(task, "projectId"), ["title"] = Str(task, "title"), ["text"] = Str(task, "text"), ["send"] = true,
        };
        if (agent is not null) p["agent"] = agent;
        if (Str(task, "profile") is { } profile) p["profile"] = profile;
        if (task["workspace"] is { } workspace) p["workspace"] = workspace.DeepClone();
        try
        {
            var answer = NetPiJson.ToNode(await _ctx.Rpc.InvokeAsync("sessions.dispatch", p, ct).ConfigureAwait(false));
            task["status"] = "running";
            task["sessionId"] = Str(answer, "sessionId");
            task["dispatchedAgent"] = agent;
            task["branch"] = answer?["workspace"]?["branch"]?.DeepClone();
            task["dispatchedAt"] = DateTimeOffset.UtcNow.ToString("O");
            task.Remove("lastError");
            _ctx.Logger.LogInformation("tasks: dispatched {Task} ({Title}) as {Session}", id, Str(task, "title"), Str(task, "sessionId"));
        }
        catch (RpcException ex) when (ex.Code is "workspace_busy" or "unavailable")
        {
            task["lastError"] = ex.Message; // tried again at the next pump
        }
        catch (RpcException ex)
        {
            task["status"] = "failed";
            task["error"] = "the dispatch was refused: " + ex.Message;
            task["finishedAt"] = DateTimeOffset.UtcNow.ToString("O");
        }
        _tasks.Put(id, task);
        Changed(task);
    }

    // ------------------------------------------------------------------ readiness

    /// <summary>What a queued task still waits for (empty: it is ready): a condition not met yet, or one that cannot be.</summary>
    private async Task<List<string>> WaitingForAsync(JsonObject task, IReadOnlyList<JsonObject> all, CancellationToken ct)
    {
        var waiting = new List<string>();
        foreach (var c in (task["after"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var depId = Str(c, "task")!;
            var when = Str(c, "when") ?? "done";
            var dep = all.FirstOrDefault(t => Str(t, "id") == depId);
            if (dep is null) { waiting.Add($"{depId} was removed: this condition cannot be met"); continue; }
            var label = $"{depId} ({Str(dep, "title")})";
            switch (Str(dep, "status"))
            {
                case "failed" or "cancelled": waiting.Add($"{label} {Str(dep, "status")}: run it again (status queued) or drop the condition"); break;
                case "done" when when == "merged" && !await MergedAsync(dep, ct).ConfigureAwait(false): waiting.Add($"{label} to be merged"); break;
                case "done": break;
                default: waiting.Add($"{label} to be {(when == "merged" ? "done and merged" : "done")}"); break;
            }
        }
        return waiting;
    }

    /// <summary>The task's branch is an ancestor of the project checkout's HEAD; a task without a branch (it worked in the
    /// project checkout) is merged once it is done.</summary>
    private async Task<bool> MergedAsync(JsonObject task, CancellationToken ct)
    {
        if (Str(task, "branch") is not { Length: > 0 } branch) return true;
        var path = _ctx.Sessions.GetProject(Str(task, "projectId")!)?.Path;
        if (path is null || !Directory.Exists(path)) return false;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "-C", path, "merge-base", "--is-ancestor", branch, "HEAD" },
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await git.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return git.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _ctx.Logger.LogDebug(ex, "tasks: could not ask git whether {Branch} is merged", branch);
            return false;
        }
    }

    /// <summary>
    /// The free instances per agent (null: no scheduler, nothing to count). A task this queue dispatched to an agent whose
    /// chat does not hold one of its slots yet (its run is starting, or waits for one) counts as busy already, so a pump
    /// in that moment does not hand the same instance out twice.
    /// </summary>
    private Dictionary<string, int>? FreeInstances(IReadOnlyList<JsonObject> all)
    {
        if (_ctx.Services.Get<IAgentScheduler>() is not { } scheduler) return null;
        var free = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in scheduler.Snapshot().Where(p => p.Configured && p.Available && !p.Disabled))
        {
            var holding = p.Owners.Concat(p.Waiters).Select(o => o.SessionId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var starting = all.Count(t => Str(t, "status") == "running" && string.Equals(Str(t, "dispatchedAgent"), p.Key, StringComparison.OrdinalIgnoreCase)
                && Str(t, "sessionId") is { } s && !holding.Contains(s));
            free[p.Key] = p.Queued > 0 ? 0 : Math.Max(0, p.Capacity - p.Busy - starting);
        }
        return free;
    }

    /// <summary>The agent a task runs on now, taking one of its free instances: its own agent, or any agent with one free
    /// ("any" or none named); null when none is free. Without a scheduler the task's own agent, as it is.</summary>
    private static string? Pick(Dictionary<string, int>? free, string? agent)
    {
        if (free is null) return agent;
        if (agent is { Length: > 0 } && !agent.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            if (!free.TryGetValue(agent, out var n) || n == 0) return null;
            free[agent] = n - 1;
            return agent;
        }
        if (free.Count == 0) return agent; // no agents set up: the dispatch runs on the default model
        var some = free.FirstOrDefault(p => p.Value > 0).Key;
        if (some is null) return null;
        free[some]--;
        return some;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<JsonObject> ShownAsync(JsonObject task, CancellationToken ct)
    {
        var shown = task.DeepClone().AsObject();
        if (Str(task, "status") == "queued")
        {
            var waiting = await WaitingForAsync(task, [.. _tasks.Find(new DataQuery().Eq("projectId", Str(task, "projectId"))).Select(d => d.Doc)], ct).ConfigureAwait(false);
            if (Flag(task, "hold")) waiting.Insert(0, "held (hold: false releases it)");
            shown["ready"] = waiting.Count == 0;
            shown["waitingFor"] = new JsonArray([.. waiting.Select(w => (JsonNode)w)]);
        }
        return shown;
    }

    /// <summary>The conditions as given: "task-…" (done), or { task, when: done | merged }; each a task of the same project.</summary>
    private JsonArray After(JsonElement? arg, string projectId, string? selfId)
    {
        var after = new JsonArray();
        if (arg is not { ValueKind: JsonValueKind.Array } list)
            return arg is null or { ValueKind: JsonValueKind.Null } ? after : throw new RpcException("bad_request", "after is a list: \"task-…\" or { task, when: done | merged }.");
        foreach (var item in list.EnumerateArray())
        {
            var (id, when) = item.ValueKind switch
            {
                JsonValueKind.String => (item.GetString(), "done"),
                JsonValueKind.Object => (item.TryGetProperty("task", out var t) ? t.GetString() : null,
                    item.TryGetProperty("when", out var w) ? w.GetString()?.Trim().ToLowerInvariant() : "done"),
                _ => (null, null),
            };
            if (string.IsNullOrWhiteSpace(id) || when is not ("done" or "merged"))
                throw new RpcException("bad_request", "after takes \"task-…\" or { task, when: done | merged }.");
            if (id == selfId) throw new RpcException("bad_request", "A task cannot wait for itself.");
            var dep = _tasks.Get(id!) ?? throw new RpcException("bad_request", $"No task {id} to wait for.");
            if (Str(dep, "projectId") != projectId) throw new RpcException("bad_request", $"{id} belongs to another project.");
            after.Add(new JsonObject { ["task"] = id, ["when"] = when });
        }
        return after;
    }

    /// <summary>Whether <paramref name="id"/> waiting for <paramref name="after"/> closes a circle.</summary>
    private bool Cycles(string id, JsonArray after)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(after.OfType<JsonObject>().Select(c => Str(c, "task")!));
        while (stack.TryPop(out var next))
        {
            if (next == id) return true;
            if (!seen.Add(next) || _tasks.Get(next) is not { } dep) continue;
            foreach (var c in (dep["after"] as JsonArray ?? []).OfType<JsonObject>()) stack.Push(Str(c, "task")!);
        }
        return false;
    }

    private ProjectInfo ResolveProject(string arg) =>
        _ctx.Sessions.ListProjects().FirstOrDefault(p => p.Id == arg)
        ?? _ctx.Sessions.ListProjects().FirstOrDefault(p => string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase))
        ?? throw new RpcException("bad_request", $"No project '{arg}'.");

    private static async Task<string?> TextAsync(RpcRequest req, CancellationToken ct)
    {
        if (req.Str("text") is { } text && !string.IsNullOrWhiteSpace(text)) return text;
        if (req.Str("textFile") is not { Length: > 0 } file) return null;
        if (!Path.IsPathRooted(file)) throw new RpcException("bad_request", $"textFile must be an absolute path: {file}");
        if (!File.Exists(file)) throw new RpcException("bad_request", $"textFile not found: {file}");
        if (new FileInfo(file).Length > Coordinator.MaxTextFileBytes) throw new RpcException("bad_request", $"textFile is over {Coordinator.MaxTextFileBytes / 1024} KB: {file}");
        return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
    }

    private void Changed(JsonObject task) =>
        _ctx.Events.Publish(ChangedEvent, new JsonObject { ["projectId"] = Str(task, "projectId"), ["id"] = Str(task, "id"), ["status"] = Str(task, "status"), ["sessionId"] = Str(task, "sessionId") });

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Flag(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static long Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var s in _subscriptions) s.Dispose();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
