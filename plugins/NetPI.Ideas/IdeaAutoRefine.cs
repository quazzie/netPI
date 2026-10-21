using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The automatic refine drain: the owner's free local agents refine newly filed ideas in the background, through the same
/// path as <c>ideas.refine</c> (<see cref="IdeaRefine.StartAsync"/>). Off by default, and it only ever uses the agents the owner
/// listed in <c>ideas.autoRefine.agents</c> — an empty list runs nothing, never "any agent", which could be a paid one.
/// <para>
/// Which ideas: open, filed by the user, not refined yet (no <c>autoRefine</c> field: a manual refine sets it too), not tagged
/// <c>no-refine</c>, and untouched for <c>delayMinutes</c>. The state lives on the idea: <c>autoRefine { state, sessionId, at,
/// finishedAt?, manual?, error? }</c> with state <c>running | done | failed</c> (a waiting idea has no state yet: "queued" is derived).
/// </para>
/// <para>
/// A timer (once a minute) and every idea change run <see cref="TickAsync"/>: sweep finished runs, then start refines while
/// fewer than <c>maxConcurrent</c> run and fewer than <c>perDay</c> started in the last 24 hours. A finish is seen on
/// <c>agent.status</c> events and on the sweep (a bus event can be dropped; a run still marked running after a restart is settled
/// by the sweep too).
/// </para>
/// </summary>
public sealed class IdeaAutoRefine : IDisposable
{
    public const string NoRefineTag = "no-refine";
    public const int DefaultMaxConcurrent = 1, DefaultDelayMinutes = 10, DefaultPerDay = 20;

    private readonly IPluginContext _ctx;
    private readonly IdeasRepository _repo;
    private readonly IdeaRefine _refine;
    private readonly ConcurrentDictionary<string, string> _running = new(StringComparer.Ordinal); // sessionId → idea id
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IDisposable> _subs = [];
    private Timer? _timer;
    private volatile bool _dirty;
    private int _next; // round robin position in the agents list

    /// <summary>A run younger than this is not judged finished by an idle status (the agent may not have started yet).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(5);

    internal IdeaAutoRefine(IPluginContext ctx, IdeasRepository repo, IdeaRefine refine)
    {
        _ctx = ctx;
        _repo = repo;
        _refine = refine;
    }

    public void Start()
    {
        _subs.Add(_ctx.Events.Subscribe(EventTypes.AgentStatus, OnAgentStatus));
        _subs.Add(_ctx.Events.Subscribe(IdeasPlugin.ChangedEvent, e =>
        {
            var reason = (NetPiJson.ToNode(e.Data) as JsonObject)?["reason"]?.GetValue<string>();
            if (reason is not ("auto-refine" or "session-recorded" or "attach")) Kick();
        }));
        _timer = new Timer(_ => Kick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var s in _subs) s.Dispose();
    }

    public void Register(IRpcRegistry rpc)
    {
        rpc.Register(new RpcMethod("ideas.autoRefine.status", "The automatic refine drain: → { enabled, agents, maxConcurrent, perDay, today (started in the last 24 hours), running: [{ id, title, sessionId, at }], queued: [{ id, title, dueAt }] }", ReadOnly: true, Params: []),
            (_, _) => Task.FromResult<object?>(Status()));
        rpc.Register(new RpcMethod("ideas.autoRefine.skip", "Keep an idea out of the automatic refine (adds the no-refine tag): { id } → idea", ReadOnly: false, Params: [RpcParam.Req("id")]),
            (req, _) => IdeasRpc.Guard(() =>
            {
                var idea = _repo.Find(req.Required("id"))?.Doc ?? throw new RpcException("not_found", $"Idea {req.Str("id")} not found");
                var tags = Tags(idea);
                if (!tags.Contains(NoRefineTag, StringComparer.OrdinalIgnoreCase)) tags.Add(NoRefineTag);
                var patch = new JsonObject { ["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)t)]) };
                return Task.FromResult<object?>(_repo.Update(IdeaOps.Str(idea["id"])!, patch, fromUi: true).Doc);
            }));
    }

    // ------------------------------------------------------------------ settings

    internal bool Enabled => _ctx.Settings.GetOr("ideas.autoRefine.enabled", false);
    private List<string> AgentKeys => _ctx.Settings.GetStrings("ideas.autoRefine.agents");
    private int MaxConcurrent => Math.Clamp(_ctx.Settings.GetInt("ideas.autoRefine.maxConcurrent", DefaultMaxConcurrent), 1, 4);
    private int Delay => Math.Clamp(_ctx.Settings.GetInt("ideas.autoRefine.delayMinutes", DefaultDelayMinutes), 0, 10080);
    private int PerDay => Math.Clamp(_ctx.Settings.GetInt("ideas.autoRefine.perDay", DefaultPerDay), 1, 1000);
    private bool ArchiveWhenDone => _ctx.Settings.GetOr("ideas.autoRefine.archiveWhenDone", true);

    // ------------------------------------------------------------------ eligibility

    private static string? State(JsonObject idea) => IdeaOps.Str(idea["autoRefine"]?["state"]);

    private static List<string> Tags(JsonObject idea) =>
        (idea["tags"] as JsonArray)?.Select(IdeaOps.Str).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList() ?? [];

    /// <summary>An idea the drain would refine, delay aside: open, the user's own, never refined, not opted out.</summary>
    internal static bool Candidate(JsonObject idea) =>
        (IdeaOps.Str(idea["status"]) ?? "open") == "open"
        && IdeaOps.Str(idea["createdBy"]) == "user"
        && idea["autoRefine"] is null
        && !Tags(idea).Contains(NoRefineTag, StringComparer.OrdinalIgnoreCase);

    private static DateTimeOffset? Time(JsonNode? n) =>
        IdeaOps.Str(n) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>When the idea was last written by its owner (its creation or last edit).</summary>
    private static DateTimeOffset Touched(JsonObject idea) =>
        new[] { Time(idea["updatedAt"]), Time(idea["createdAt"]) }.Max() ?? DateTimeOffset.MinValue;

    private int StartedToday(List<JsonObject> ideas)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        return ideas.Count(i => i["autoRefine"] is JsonObject a && a["manual"] is not JsonValue && Time(a["at"]) is { } t && t >= since);
    }

    public JsonObject Status()
    {
        var ideas = _repo.All();
        var delay = TimeSpan.FromMinutes(Delay);
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["agents"] = new JsonArray([.. AgentKeys.Select(k => (JsonNode)k)]),
            ["maxConcurrent"] = MaxConcurrent,
            ["perDay"] = PerDay,
            ["today"] = StartedToday(ideas),
            ["running"] = new JsonArray([.. ideas.Where(i => State(i) == "running").Select(i => (JsonNode)new JsonObject
            {
                ["id"] = IdeaOps.Str(i["id"]), ["title"] = IdeaOps.Str(i["title"]), ["sessionId"] = IdeaOps.Str(i["autoRefine"]?["sessionId"]), ["at"] = IdeaOps.Str(i["autoRefine"]?["at"]),
            })]),
            ["queued"] = new JsonArray([.. ideas.Where(Candidate).OrderBy(Touched).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = IdeaOps.Str(i["id"]), ["title"] = IdeaOps.Str(i["title"]), ["dueAt"] = (Touched(i) + delay).ToString("O"),
            })]),
        };
    }

    // ------------------------------------------------------------------ the drain

    private void Kick()
    {
        _ = Task.Run(async () =>
        {
            try { await TickAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _ctx.Logger.LogWarning(ex, "Ideas: the automatic refine pass failed"); }
        });
    }

    /// <summary>One pass: settle finished runs, then start what the limits allow. Passes never overlap (a caller waits for the one under way); a request that arrives during one runs once more.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        _dirty = true;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_dirty)
            {
                _dirty = false;
                Sweep();
                await StartDueAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Settle every run marked running whose chat is no longer running (also after a restart, and when an event was dropped).</summary>
    public void Sweep()
    {
        var runtime = _ctx.Services.Get<IAgentRuntime>();
        foreach (var idea in _repo.All().Where(i => State(i) == "running"))
        {
            var id = IdeaOps.Str(idea["id"])!;
            var sessionId = IdeaOps.Str(idea["autoRefine"]?["sessionId"]);
            if (string.IsNullOrEmpty(sessionId)) { Finish(id, null, false, "no chat was recorded for the run"); continue; }
            _running[sessionId] = id;
            if (runtime is null) continue;
            if (_ctx.Sessions.GetSession(sessionId) is null) { Finish(id, sessionId, false, "the chat was deleted"); continue; }
            var young = Time(idea["autoRefine"]?["at"]) is { } at && DateTimeOffset.UtcNow - at < Grace;
            var agent = runtime.GetBySession(sessionId);
            switch (agent?.Status)
            {
                case AgentStatus.Queued or AgentStatus.Running or AgentStatus.Yielded: break;
                case AgentStatus.Failed or AgentStatus.Cancelled:
                    Finish(id, sessionId, false, agent.Error ?? agent.Status.ToString().ToLowerInvariant()); break;
                case AgentStatus.Idle or AgentStatus.Completed:
                    if (!young) Finish(id, sessionId, true, null); break;
                default: // no agent: the app restarted (or the chat's agent was released); the transcript says how it went
                    if (young) break;
                    if (_ctx.Sessions.GetMessages(sessionId).Any(m => m.Role == MessageRole.Assistant)) Finish(id, sessionId, true, null);
                    else Finish(id, sessionId, false, "the run was interrupted before the agent answered");
                    break;
            }
        }
    }

    private void OnAgentStatus(BusEvent e)
    {
        if (NetPiJson.ToNode(e.Data) is not JsonObject data || data["agent"] is not JsonObject agent) return;
        var sessionId = IdeaOps.Str(agent["sessionId"]);
        if (string.IsNullOrEmpty(sessionId) || !_running.TryGetValue(sessionId, out var ideaId)) return;
        if (!Enum.TryParse<AgentStatus>(IdeaOps.Str(agent["status"]), true, out var status)) return;
        switch (status)
        {
            case AgentStatus.Failed or AgentStatus.Cancelled:
                Finish(ideaId, sessionId, false, IdeaOps.Str(agent["error"]) ?? status.ToString().ToLowerInvariant()); break;
            case AgentStatus.Idle or AgentStatus.Completed:
                // an idle status right after the start may be the agent's creation, not the end of the run: the sweep decides then
                if (_repo.Find(ideaId)?.Doc["autoRefine"]?["at"] is { } at && Time(at) is { } t && DateTimeOffset.UtcNow - t < Grace) break;
                Finish(ideaId, sessionId, true, null); break;
        }
    }

    private void Finish(string ideaId, string? sessionId, bool ok, string? error)
    {
        if (sessionId is not null) _running.TryRemove(sessionId, out _);
        lock (_running)
        {
            var current = _repo.Find(ideaId)?.Doc["autoRefine"] as JsonObject;
            if (current is null || IdeaOps.Str(current["state"]) != "running" || IdeaOps.Str(current["sessionId"]) != sessionId) return; // settled already
            var next = (JsonObject)current.DeepClone();
            next["state"] = ok ? "done" : "failed";
            next["finishedAt"] = IdeaOps.Now();
            if (!ok) next["error"] = error ?? "the run failed";
            _repo.SetAutoRefine(ideaId, next);
        }
        if (ok && sessionId is not null && ArchiveWhenDone)
        {
            try { _ctx.Sessions.UpdateSession(sessionId, s => s.Archived = true); }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Ideas: archiving the refine chat {Session} failed", sessionId); }
        }
    }

    private async Task StartDueAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        var keys = AgentKeys;
        if (keys.Count == 0 || _ctx.Services.Get<IAgentRuntime>() is null) return;
        var ideas = _repo.All();
        var room = Math.Min(MaxConcurrent - ideas.Count(i => State(i) == "running"), PerDay - StartedToday(ideas));
        if (room <= 0) return;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(Delay);
        foreach (var idea in ideas.Where(Candidate).Where(i => Touched(i) <= cutoff).OrderBy(Touched).Take(room))
        {
            ct.ThrowIfCancellationRequested();
            var id = IdeaOps.Str(idea["id"])!;
            var agent = await PickAgentAsync(keys, ct).ConfigureAwait(false);
            if (agent is null) return; // none of the owner's agents can take work now: try again on the next pass
            string? sessionId = null;
            try
            {
                await _refine.StartAsync(new IdeaRefine.RefineStart(id, agent, null, Auto: true, OnCreated: sid =>
                {
                    sessionId = sid;
                    _running[sid] = id;
                    _repo.SetAutoRefine(id, new JsonObject { ["state"] = "running", ["sessionId"] = sid, ["at"] = IdeaOps.Now(), ["agent"] = agent });
                }), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (sessionId is not null) _running.TryRemove(sessionId, out _);
                _ctx.Logger.LogWarning(ex, "Ideas: the automatic refine of {Idea} did not start", id);
                // failed, with the reason: it is not retried by itself (the card offers a retry)
                _repo.SetAutoRefine(id, new JsonObject { ["state"] = "failed", ["at"] = IdeaOps.Now(), ["finishedAt"] = IdeaOps.Now(), ["agent"] = agent, ["error"] = ex.Message });
            }
        }
    }

    /// <summary>
    /// The next agent of the owner's list, round robin, preferring one with a free instance. Only agents that exist, are enabled
    /// and available count (when the Agents plugin reports them). Null when none can take work.
    /// </summary>
    private async Task<string?> PickAgentAsync(List<string> keys, CancellationToken ct)
    {
        JsonArray? slots = null;
        if (_ctx.Rpc.Exists("agents.list"))
        {
            try { slots = NetPiJson.ToNode(await _ctx.Rpc.InvokeAsync("agents.list", new JsonObject(), ct).ConfigureAwait(false)) as JsonArray; }
            catch (Exception ex) when (ex is not OperationCanceledException) { _ctx.Logger.LogDebug(ex, "Ideas: agents.list failed; round robin without it"); }
        }
        var order = Enumerable.Range(0, keys.Count).Select(i => keys[(_next + i) % keys.Count]).ToList();
        var usable = new List<(string Key, bool Free)>();
        foreach (var key in order)
        {
            if (slots is null) { usable.Add((key, true)); continue; }
            var slot = slots.OfType<JsonObject>().FirstOrDefault(s => string.Equals(IdeaOps.Str(s["key"]), key, StringComparison.OrdinalIgnoreCase));
            if (slot is null || slot["disabled"]?.GetValue<bool?>() == true || slot["available"]?.GetValue<bool?>() == false) continue;
            var capacity = slot["capacity"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 1;
            var busy = slot["busy"] is JsonValue b && b.TryGetValue<int>(out var bi) ? bi : 0;
            usable.Add((key, busy < capacity));
        }
        if (usable.Count == 0) return null;
        var pick = usable.FirstOrDefault(u => u.Free);
        if (pick.Key is null) pick = usable[0];
        _next = (keys.IndexOf(pick.Key) + 1) % keys.Count;
        return pick.Key;
    }
}
