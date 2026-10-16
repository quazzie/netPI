using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

/// <summary>
/// Parallel slots (a pool of them per key in the code). The agents the user sets up are pools: <c>agents.&lt;id&gt; = { model,
/// instances, use, disabled, budget, cost }</c>, one slot per instance; chats and subagents run on them. An agent takes
/// work only while it is not disabled and its model is loaded (local: AiProxy's status) or reachable (cloud); NetPI never
/// loads a model. Agents on one local model share its slots (valid catalog <see cref="ModelInfo.Concurrency"/>, otherwise
/// <c>models.localSlots</c>, clamped to one): no more
/// runs on the model than it serves. Model calls without an agent (a separate summarizer, and every call while no agent
/// is set up) get a slot per model while they run: a local model by its concurrency (<c>models.localSlots</c>),
/// a cloud provider's models together (<c>models.cloudSlots</c>). Waiters are served by priority (desc), then FIFO, and
/// wait until they get a slot or are cancelled. A critical waiter (<see cref="RunPriority.Preempts"/>) with no slot free
/// signals the pausable runs below it that could free one (<see cref="IAgentSlot.Wanted"/>); the first of them to reach a
/// point where it can stop gives its slot up (<see cref="IAgentSlot.GiveUpIfWanted"/>).
/// </summary>
internal sealed class AgentScheduler : IAgentScheduler
{
    private readonly IPluginContext _ctx;
    private readonly Ledger? _usage;
    private readonly IResourceLeases? _resources;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pool> _pools = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Runs waiting for any of several agents (<see cref="AgentSlotRequest.Candidates"/>): in nobody's queue until one takes them.</summary>
    private readonly List<Waiter> _floating = [];
    /// <summary>Every model we have seen (catalog cache + models passed to <see cref="Resolve(ModelInfo)"/>), by ref.</summary>
    private readonly Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The model refs the catalog lists now. A model that left its backend's list is not loaded, whatever it last reported.</summary>
    private HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private bool _stopped;
    private int _publishScheduled;
    private int _publishPending;
    private string _signature = "";

    public AgentScheduler(IPluginContext ctx, Ledger? usage)
    {
        _ctx = ctx;
        _usage = usage;
        _resources = ctx.Services.Get<IResourceLeases>(); // host-owned, lifetime of this host
    }

    /// <summary>Delay used to coalesce <c>agents.changed</c> events.</summary>
    public int PublishDelayMs { get; init; } = 100;

    /// <summary>A coalesced <c>agents.changed</c> is still on its timer or being sent: a test waits for this instead of guessing at the window.</summary>
    internal bool PublishPending => Volatile.Read(ref _publishPending) > 0;

    // ---------------------------------------------------------------- agents

    /// <summary>Keys under <c>agents</c> that are settings, not agent ids.</summary>
    internal static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "maxDepth", SessionAgent.Any };

    internal sealed record AgentConfig(string Id, string Model, JsonObject Cfg)
    {
        public int? Instances => ReadInt(Cfg["instances"]) ?? ReadInt(Cfg["capacity"]);
        public bool Disabled => Cfg["disabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        public string? Use => Text(Cfg["use"]);
    }

    /// <summary>The agents the user set up: <c>agents.&lt;id&gt;</c> objects with a <c>model</c>.</summary>
    internal IReadOnlyList<AgentConfig> Agents()
    {
        JsonObject? agents = null;
        try { agents = _ctx.Settings.GetNode("agents") as JsonObject; } catch { }
        if (agents is null) return [];
        var list = new List<AgentConfig>();
        foreach (var (id, cfg) in agents)
            if (!Reserved.Contains(id) && cfg is JsonObject o && Text(o["model"]) is { } model)
                list.Add(new AgentConfig(id, model, o));
        return list;
    }

    internal AgentConfig? Agent(string? id) =>
        id is null ? null : Agents().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The agent a model call is billed and capped by: the session's agent when it runs on that model, else the first
    /// agent on the model; else the call's own slot key and no agent settings.
    /// </summary>
    internal (string Agent, JsonObject? Cfg) AgentOf(ModelInfo model, string? sessionId = null)
    {
        var agents = Agents();
        if (sessionId is not null && SessionAgent.Running(_ctx.Sessions.GetSession(sessionId)) is { } sid
            && agents.FirstOrDefault(a => string.Equals(a.Id, sid, StringComparison.OrdinalIgnoreCase)) is { } mine
            && string.Equals(mine.Model, model.Ref, StringComparison.OrdinalIgnoreCase))
            return (mine.Id, mine.Cfg);
        if (agents.FirstOrDefault(a => string.Equals(a.Model, model.Ref, StringComparison.OrdinalIgnoreCase)) is { } first)
            return (first.Id, first.Cfg);
        return (ModelKey(model), null);
    }

    /// <summary>A model the scheduler has seen (catalog cache or a resolved pool), by ref.</summary>
    internal ModelInfo? ModelInfo(string? modelRef)
    {
        if (modelRef is null) return null;
        lock (_gate) return _models.GetValueOrDefault(modelRef);
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private sealed class Pool(string key)
    {
        public string Key { get; } = key;
        public string? Provider { get; set; }
        public int Capacity { get; set; } = 1;
        public string Source { get; set; } = "default";
        /// <summary>An agent the user set up (else a slot for model calls without an agent).</summary>
        public bool Configured { get; set; }
        /// <summary>
        /// A configured agent that was removed: its runs finish, nothing new starts on it, and its queued work is
        /// refused. Distinct from a pool that was never configured (a plain model call), which is available again the
        /// moment a slot frees. The model association is kept while owners remain, so their calls still count against
        /// the shared slots of the local model they run on.
        /// </summary>
        public bool Retired { get; set; }
        public string? Model { get; set; }
        public string? Use { get; set; }
        public bool Disabled { get; set; }
        public List<string> Models { get; } = [];
        public List<Lease> Owners { get; } = [];
        public List<Waiter> Waiters { get; } = [];
    }

    private sealed class Waiter
    {
        public required AgentSlotRequest Request { get; init; }
        public required long Seq { get; init; }
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<IAgentSlot> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        /// <summary>Waits for any of several agents instead of one (it is in <c>_floating</c>, not in a pool's queue).</summary>
        public bool Floating => Request.Candidates is { Count: > 1 };
    }

    internal sealed class Lease(AgentScheduler owner, AgentSlotRequest request, string? localModel, IDisposable? physical) : IAgentSlot
    {
        public string? LeaseId => (physical as IResourceLease)?.Id;
        private int _released;
        private CancellationTokenSource _wanted = new();
        public string Key { get; } = request.Key;
        public string AgentId { get; } = request.AgentId;
        public string? SessionId { get; } = request.SessionId;
        public string? Label { get; } = request.Label;
        public int Priority { get; set; } = request.Priority;
        public bool Pausable { get; set; } = request.Pausable;
        public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        // The admitted call stays on this resource even if its agent is rebound while it runs.
        public string? LocalModel { get; } = localModel;
        public CancellationToken Wanted => Volatile.Read(ref _wanted).Token;

        /// <summary>
        /// A critical run waits for this slot. Caller holds the scheduler's gate: the token's callbacks (a run waiting on it)
        /// run on the pool, never inline under the gate.
        /// </summary>
        internal void Want()
        {
            var cts = Volatile.Read(ref _wanted);
            if (!cts.IsCancellationRequested) _ = cts.CancelAsync();
        }

        /// <summary>No one needs it any more: a fresh token for the next time. Caller holds the scheduler's gate.</summary>
        internal void Renew()
        {
            if (Volatile.Read(ref _wanted).IsCancellationRequested) Volatile.Write(ref _wanted, new CancellationTokenSource());
        }

        public bool GiveUpIfWanted() => owner.GiveUp(this);

        /// <summary>Mark released without granting anything (scheduler stopped).</summary>
        internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

        /// <summary>The physical slot, freed at once (the scheduler's give-up does this under its gate).</summary>
        internal void ReleasePhysical() => physical?.Dispose();

        public void Dispose()
        {
            if (!MarkReleased()) return;
            physical?.Dispose();
            owner.Release(this);
        }
    }

    private static int? ReadInt(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
        return null;
    }

    // ---------------------------------------------------------------- slots

    /// <summary>The slot key of a model call without an agent: the local model, or the cloud provider.</summary>
    private static string ModelKey(ModelInfo model) => model.IsLocal ? model.Ref : model.Provider;

    private int LocalCapacity(ModelInfo model) => ModelCapacity.Local(_ctx.Settings, model);

    private int ModelKeyCapacity(ModelInfo model) => model.IsLocal
        ? LocalCapacity(model)
        : ModelCapacity.Cloud(_ctx.Settings);

    /// <summary>Default instances of an agent: a local model's slots, 1 on a cloud model (it may cost money).</summary>
    internal int DefaultInstances(ModelInfo? model) =>
        model is { IsLocal: true } ? LocalCapacity(model) : 1;

    private void ApplyModelPool(Pool pool, ModelInfo model)
    {
        pool.Provider = model.Provider;
        pool.Capacity = Math.Max(1, ModelKeyCapacity(model));
        pool.Source = model.IsLocal && model.Concurrency is > 0 ? "catalog" : "default";
        if (!pool.Models.Contains(model.Ref, StringComparer.OrdinalIgnoreCase)) pool.Models.Add(model.Ref);
    }

    private void ApplyAgent(Pool pool, AgentConfig a)
    {
        var model = _models.GetValueOrDefault(a.Model);
        pool.Configured = true;
        pool.Retired = false;   // the agent is in the settings again: it takes work once more
        pool.Model = a.Model;
        pool.Use = a.Use;
        pool.Disabled = a.Disabled;
        pool.Source = "settings";
        pool.Provider = model?.Provider ?? (a.Model.Contains('/') ? a.Model[..a.Model.IndexOf('/')] : null);
        pool.Capacity = Math.Max(1, a.Instances ?? DefaultInstances(model));
        pool.Models.Clear();
        pool.Models.Add(a.Model);
    }

    /// <summary>The local model a pool runs on, for the shared model slots (null: a cloud pool, no shared limit).</summary>
    private string? LocalModelOf(Pool p)
    {
        var modelRef = p.Configured ? p.Model : p.Models.Count == 1 ? p.Models[0] : null;
        return modelRef is not null && _models.TryGetValue(modelRef, out var m) && m.IsLocal ? m.Ref : null;
    }

    /// <summary>One physical limit for a local model: valid catalog concurrency, otherwise the clamped fallback.</summary>
    private int? ModelSlots(string? localModel) =>
        localModel is not null && _models.TryGetValue(localModel, out var m) ? LocalCapacity(m) : null;

    private int BusyOn(string localModel) => _resources is { } shared
        ? shared.Snapshot().Count(x => string.Equals(x.Resource, "local:" + localModel, StringComparison.OrdinalIgnoreCase))
        : _pools.Values.Sum(p => p.Owners.Count(o => string.Equals(o.LocalModel, localModel, StringComparison.OrdinalIgnoreCase)));

    private string ResourceOf(Pool pool) => LocalModelOf(pool) is { } model ? "local:" + model
        : pool.Configured ? "agent:" + pool.Key : "provider:" + (pool.Provider ?? pool.Key);
    private int ResourceCapacity(Pool pool) => ModelSlots(LocalModelOf(pool)) ?? pool.Capacity;
    private int PoolBusy(Pool pool) => _resources is { } shared
        ? shared.Snapshot().Count(x => string.Equals(x.Key, pool.Key, StringComparison.OrdinalIgnoreCase)) : pool.Owners.Count;

    private Lease? NewLease(Pool pool, AgentSlotRequest request)
    {
        IDisposable? physical = null;
        if (_resources is { } shared && !shared.TryAcquire(ResourceOf(pool), ResourceCapacity(pool), request, pool.Capacity, out physical)) return null;
        return new Lease(this, request, LocalModelOf(pool), physical);
    }

    /// <summary>A free slot in the pool, and on its model. Caller holds <see cref="_gate"/>.</summary>
    private bool CanGrant(Pool pool)
    {
        if (PoolBusy(pool) >= pool.Capacity) return false;
        var model = LocalModelOf(pool);
        return ModelSlots(model) is not { } slots || BusyOn(model!) < slots;
    }

    /// <summary>Whether an agent can take work now, and why not. Caller holds <see cref="_gate"/>.</summary>
    private (bool Available, string? Reason) AvailabilityOf(Pool p)
    {
        if (p.Retired) return (false, "removed");
        if (!p.Configured) return (true, null);
        if (p.Disabled) return (false, "disabled");
        if (p.Model is null || !_models.TryGetValue(p.Model, out var m)) return (false, $"{p.Model} is not in the model list");
        if (m.IsLocal)
        {
            if (!_listed.Contains(m.Ref)) return (false, $"{m.Id} isn't loaded");
            return m.Status switch
            {
                null or "loaded" => (true, null),
                "offline" => (false, $"{m.Provider} can't be reached"),
                "stopped" => (false, $"{m.Id} isn't loaded (its backend isn't running)"),
                _ => (false, $"{m.Id} isn't loaded"),
            };
        }
        return m.Status is "offline" ? (false, $"{m.Provider} can't be reached") : (true, null);
    }

    // ---------------------------------------------------------------- IAgentScheduler

    public string Resolve(ModelInfo model)
    {
        var key = ModelKey(model);
        lock (_gate)
        {
            _models[model.Ref] = model;
            var pool = GetOrCreate(key);
            if (!pool.Configured)
                ApplyModelPool(pool, model);
            Pump();
        }
        return key;
    }

    public IReadOnlyList<string> Candidates(ModelInfo? model, string? preferred)
    {
        var agents = Agents();
        if (agents.Count == 0) return [];
        var matching = model is null ? agents.ToList() : agents.Where(a => string.Equals(a.Model, model.Ref, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
            throw new CallRefusedException(model is null
                ? "No agent is set up. Set one up in Settings → Agents."
                : $"No agent runs {model.Ref}. Choose an agent for this chat, or set one up on this model (Settings → Agents).") { Kind = "unavailable" };
        lock (_gate)
        {
            if (model is not null) _models[model.Ref] = model;
            var ranked = matching.Select(a =>
            {
                var pool = GetOrCreate(a.Id);
                ApplyAgent(pool, a);
                var (available, reason) = AvailabilityOf(pool);
                return (a.Id, Available: available, Reason: reason, Free: available && CanGrant(pool) && pool.Waiters.Count == 0,
                    Preferred: string.Equals(a.Id, preferred, StringComparison.OrdinalIgnoreCase), Load: pool.Owners.Count + pool.Waiters.Count);
            }).ToList();
            var usable = ranked.Where(r => r.Available).ToList();
            if (usable.Count == 0)
            {
                if (model is null)
                    throw new CallRefusedException("No agent can take work now: " + string.Join("; ", ranked.Select(r => $"{r.Id} ({r.Reason})")) + ". Load a model (AiSwitcher) or switch an agent on.") { Kind = "unavailable" };
                // agents run the model but none can take work: the best of them, so the acquire says why
                return [ranked.OrderBy(r => r.Preferred ? 0 : 1).ThenBy(r => r.Load).First().Id];
            }
            return usable.OrderBy(r => r.Free ? 0 : 1).ThenBy(r => r.Preferred ? 0 : 1).ThenBy(r => r.Load).Select(r => r.Id).ToList();
        }
    }

    public string? ModelOf(string agent) => Agent(agent)?.Model;

    public IReadOnlyList<SlotHolder> Unassigned()
    {
        lock (_gate)
            return _floating.Select(w => new SlotHolder
            {
                AgentId = w.Request.AgentId, SessionId = w.Request.SessionId, Label = w.Request.Label, Since = w.Since,
                Priority = w.Request.Priority, WaitingFor = "a free agent",
            }).ToList();
    }

    public string Resolve(ModelInfo model, string? agent)
    {
        if (Agent(agent) is not { } a) return Resolve(model);
        lock (_gate)
        {
            _models[model.Ref] = model;
            var pool = GetOrCreate(a.Id);
            ApplyAgent(pool, a);
            Pump();
        }
        return a.Id;
    }

    /// <summary>Re-read the agents and the models' states (settings or models changed). Returns whether anything changed.</summary>
    public bool Refresh()
    {
        IReadOnlyList<ModelInfo>? cached = null;
        try { cached = _ctx.Models?.Cached ?? []; } catch { }
        var agents = Agents();
        List<(Waiter W, string Reason)> refused = [];

        lock (_gate)
        {
            if (cached is not null)
            {
                foreach (var m in cached) _models[m.Ref] = m;
                _listed = new HashSet<string>(cached.Select(m => m.Ref), StringComparer.OrdinalIgnoreCase);
            }
            var ids = new HashSet<string>(agents.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var a in agents) ApplyAgent(GetOrCreate(a.Id), a);
            foreach (var pool in _pools.Values.ToList())
            {
                if (pool.Configured && !ids.Contains(pool.Key))
                {
                    // An agent that was removed: its runs finish, nothing new starts on it, and whatever was queued for
                    // it is refused below (an unconfigured pool used to count as available, so a waiter was handed the
                    // removed agent's slot). The model association stays until the last owner releases, so those runs
                    // keep counting against the shared slots of their local model.
                    pool.Configured = false;
                    pool.Retired = true;
                }
                if (!pool.Configured && pool.Owners.Count == 0 && pool.Waiters.Count == 0) { _pools.Remove(pool.Key); continue; }
                if (!pool.Configured && !pool.Retired && pool.Models.Count > 0
                    && _models.TryGetValue(pool.Models[0], out var model))
                    ApplyModelPool(pool, model);
                var (available, reason) = AvailabilityOf(pool);
                if (!available)
                {
                    // waiting on an agent that can't take work: tell them now instead of letting them wait
                    foreach (var w in pool.Waiters) refused.Add((w, reason!));
                    pool.Waiters.Clear();
                    continue;
                }
            }
            foreach (var w in _floating.ToList())
            {
                var states = w.Request.Candidates!.Select(k => (Key: k, State: _pools.TryGetValue(k, out var cp) ? AvailabilityOf(cp) : (Available: false, Reason: "removed"))).ToList();
                if (states.Any(x => x.State.Available)) continue;
                _floating.Remove(w);
                refused.Add((w, string.Join("; ", states.Select(x => $"{x.Key} ({x.State.Reason})"))));
            }
            // Finish every removal/disable/capacity change before choosing work across the shared resources.
            Pump();
        }
        foreach (var (w, reason) in refused)
        {
            w.Registration.Unregister();
            w.Tcs.TrySetException(new CallRefusedException(w.Floating ? NoneAvailable(w.Request.Candidates!, reason) : UnavailableMessage(w.Request.Key, reason)) { Kind = "unavailable" });
        }
        var signature = Signature();
        var changed = !string.Equals(signature, Interlocked.Exchange(ref _signature, signature), StringComparison.Ordinal);
        if (changed || refused.Count > 0) SchedulePublish();
        return changed;
    }

    /// <summary>
    /// What the Work tab and the agents' rows show (capacity, state, price, daily cap): agents.changed only when it changes.
    /// The price and the cap are the agent's settings (<c>cost</c>, <c>budget</c>): without them a price set in the agent's
    /// dialog was announced only when something else changed too.
    /// </summary>
    private string Signature()
    {
        var priced = Agents().ToDictionary(a => a.Id, a => $"{a.Cfg["cost"]?.ToJsonString()}|{a.Cfg["budget"]?.ToJsonString()}", StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            return string.Join('\n', _pools.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}|{p.Capacity}|{p.Disabled}|{AvailabilityOf(p).Available}|{p.Use}|{p.Model}|{ModelSlots(LocalModelOf(p))}|{p.Source}|{priced.GetValueOrDefault(p.Key)}"));
    }

    internal static string UnavailableMessage(string agent, string reason) => reason switch
    {
        "removed" =>
            $"The agent \"{agent}\" was removed while this was waiting for a slot, so the work was not started. " +
            "Add the agent back in Settings → Agents, or send the message again on another agent.",
        "disabled" =>
            $"The agent \"{agent}\" is disabled. Enable it (Settings → Agents, or its switch in the Work tab) or choose another agent.",
        _ =>
            $"The agent \"{agent}\" can't take work now: {reason}. Load its model (AiSwitcher) or choose another agent.",
    };

    internal static string NoneAvailable(IReadOnlyList<string> agents, string reasons) =>
        $"None of the agents this run could use can take work now ({string.Join(", ", agents)}): {reasons}. Load a model (AiSwitcher) or switch an agent on.";

    /// <summary>The request as it is granted: for the agent that took it.</summary>
    private static AgentSlotRequest Bind(AgentSlotRequest r, Pool pool) => new()
    {
        Key = pool.Key, AgentId = r.AgentId, SessionId = r.SessionId, Label = r.Label, Priority = r.Priority, Pausable = r.Pausable,
        Provider = pool.Provider ?? r.Provider, ExecutorGeneration = r.ExecutorGeneration,
    };

    private static AgentSlotRequest Copy(AgentSlotRequest r, string key, IReadOnlyList<string>? candidates) => new()
    {
        Key = key, Candidates = candidates, AgentId = r.AgentId, SessionId = r.SessionId, Label = r.Label, Priority = r.Priority, Pausable = r.Pausable,
        Provider = r.Provider, ExecutorGeneration = r.ExecutorGeneration,
    };

    /// <summary>The agents a run may go to, in order.</summary>
    private static List<string> Candidates(AgentSlotRequest request) => [.. request.Candidates ?? [request.Key]];

    private Pool GetOrCreate(string key, string? provider = null)
    {
        if (_pools.TryGetValue(key, out var pool)) return pool;
        pool = new Pool(key) { Provider = provider };
        // Refresh can remove an idle plain pool between Resolve and Acquire. Its key still names
        // the model we resolved: restore the resource association before admitting the call.
        if (_models.TryGetValue(key, out var model))
            ApplyModelPool(pool, model);
        else
        {
            // A pool we have never resolved: guess by the key shape ("provider/model" = local model, else cloud provider).
            pool.Capacity = key.Contains('/')
                ? ModelCapacity.Local(_ctx.Settings, null)
                : ModelCapacity.Cloud(_ctx.Settings);
        }
        _pools[key] = pool;
        return pool;
    }

    /// <summary>The agents (always) and the other slots while they are busy.</summary>
    public IReadOnlyList<ModelResourceSlots> Resources()
    {
        lock (_gate)
        {
            var held = _resources?.Snapshot() ?? [];
            var grouped = _pools.Values.GroupBy(ResourceOf, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in held)
                if (!grouped.ContainsKey(entry.Resource)) grouped[entry.Resource] = [];
            return grouped.Select(g =>
            {
                var pool = g.Value.FirstOrDefault();
                var model = g.Key.StartsWith("local:", StringComparison.OrdinalIgnoreCase) ? g.Key[6..] : pool?.Model;
                var modelInfo = _models.GetValueOrDefault(model ?? "");
                var canTakeWork = g.Value.Any(p => AvailabilityOf(p).Available)
                    || pool is null && modelInfo is { Status: null or "loaded" };
                var unavailable = canTakeWork ? null : pool is null ? "Model is unavailable" : AvailabilityOf(pool).Reason;
                var owners = _resources is null
                    ? g.Value.SelectMany(p => p.Owners).Select(o => new SlotHolder { AgentId = o.AgentId, SessionId = o.SessionId, Label = o.Label, Priority = o.Priority, Since = o.AcquiredAt }).ToList()
                    : held.Where(x => string.Equals(x.Resource, g.Key, StringComparison.OrdinalIgnoreCase)).Select(x => x.Holder).ToList();
                return new ModelResourceSlots
                {
                    Key = g.Key, Model = model, Capacity = pool is not null ? ResourceCapacity(pool) : ModelSlots(model) ?? ModelCapacity.DefaultLocal,
                    Busy = owners.Count, Queued = g.Value.Sum(p => p.Waiters.Count), Owners = owners,
                    Available = canTakeWork,
                    Unavailable = unavailable,
                };
            }).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public bool UpdatePriority(string sessionId, int priority, bool pausable)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var pool in _pools.Values)
            {
                foreach (var waiter in pool.Waiters.Where(w => string.Equals(w.Request.SessionId, sessionId, StringComparison.Ordinal)))
                {
                    var resume = Math.Abs(waiter.Request.Priority % 100) == RunPriority.Resume;
                    waiter.Request.Priority = priority + (resume ? RunPriority.Resume : 0);
                    waiter.Request.Pausable = pausable;
                    changed = true;
                }
                if (pool.Waiters.Count > 1)
                {
                    var sorted = pool.Waiters.OrderByDescending(w => w.Request.Priority).ThenBy(w => w.Seq).ToList();
                    pool.Waiters.Clear();
                    pool.Waiters.AddRange(sorted);
                }
                foreach (var owner in pool.Owners.Where(o => string.Equals(o.SessionId, sessionId, StringComparison.Ordinal)))
                {
                    owner.Priority = priority;
                    owner.Pausable = pausable;
                    changed = true;
                }
            }
            foreach (var waiter in _floating.Where(w => string.Equals(w.Request.SessionId, sessionId, StringComparison.Ordinal)))
            {
                var resume = Math.Abs(waiter.Request.Priority % 100) == RunPriority.Resume;
                waiter.Request.Priority = priority + (resume ? RunPriority.Resume : 0);
                waiter.Request.Pausable = pausable;
                changed = true;
            }
            if (_floating.Count > 1)
            {
                var sorted = _floating.OrderByDescending(w => w.Request.Priority).ThenBy(w => w.Seq).ToList();
                _floating.Clear();
                _floating.AddRange(sorted);
            }
            if (changed) Pump();
        }
        if (changed) SchedulePublish();
        return changed;
    }

    public IReadOnlyList<AgentSlots> Snapshot()
    {
        IReadOnlyList<ModelInfo> cached;
        try { cached = _ctx.Models?.Cached ?? []; } catch { cached = []; }
        var needRefresh = false;
        lock (_gate) needRefresh = cached.Any(m => !_models.ContainsKey(m.Ref)) || Agents().Any(a => !_pools.ContainsKey(a.Id));
        if (needRefresh) Refresh();

        var configs = Agents().ToDictionary(a => a.Id, a => a.Cfg, StringComparer.OrdinalIgnoreCase);
        List<AgentSlots> list;
        lock (_gate)
        {
            var held = _resources?.Snapshot();
            var resources = Resources().ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            list = _pools.Values
                .Where(p => p.Configured || PoolBusy(p) > 0 || p.Waiters.Count > 0)
                .OrderBy(p => p.Configured ? 0 : 1)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var (available, reason) = AvailabilityOf(p);
                    var resource = resources.GetValueOrDefault(ResourceOf(p));
                    var info = new AgentSlots
                    {
                        Resource = resource?.Key,
                        ResourceCapacity = resource?.Capacity,
                        ResourceBusy = resource?.Busy,
                        ResourceQueued = resource?.Queued,
                        Key = p.Key,
                        Provider = p.Provider,
                        Capacity = p.Capacity,
                        Busy = PoolBusy(p),
                        Queued = p.Waiters.Count,
                        Models = [.. p.Models],
                        Owners = held is null ? p.Owners.Select(o => new SlotHolder { AgentId = o.AgentId, SessionId = o.SessionId, Label = o.Label, Priority = o.Priority, Since = o.AcquiredAt }).ToList()
                            : held.Where(x => string.Equals(x.Key, p.Key, StringComparison.OrdinalIgnoreCase)).Select(x => x.Holder).ToList(),
                        Waiters = p.Waiters.Select(w => new SlotHolder { AgentId = w.Request.AgentId, SessionId = w.Request.SessionId, Label = w.Request.Label, Since = w.Since, Priority = w.Request.Priority,
                            WaitingFor = p.Configured && PoolBusy(p) >= p.Capacity ? "agent instances" : resource?.Busy >= resource?.Capacity ? "model capacity" : "earlier queued work" }).ToList(),
                        Source = p.Source,
                        Status = StatusOf(p, available),
                        Configured = p.Configured,
                        Model = p.Model,
                        Use = p.Use,
                        Available = available,
                        Unavailable = reason,
                        Disabled = p.Disabled,
                    };
                    var modelRef = p.Model ?? (p.Models.Count == 1 ? p.Models[0] : null);
                    if (modelRef is not null && _models.TryGetValue(modelRef, out var mi))
                    {
                        var price = Ledger.PriceOf(mi, configs.GetValueOrDefault(p.Key));
                        info.PriceInput = price?.Input;
                        info.PriceOutput = price?.Output;
                        info.PriceSource = price?.Source ?? "unknown";
                        info.Free = !Ledger.Paid(mi, price);
                    }
                    info.DailyLimitUsd = Ledger.DailyCap(configs.GetValueOrDefault(p.Key));
                    return info;
                })
                .ToList();
        }
        if (_usage is not null)
        {
            // One ledger roll for the whole snapshot, not one per pool: SpentToday rolls (a database read) per call, and
            // a slot-churn burst would otherwise be one query per agent for every coalesced agents.changed.
            var spent = _usage.SpentTodayByAgent();
            foreach (var info in list) info.SpentTodayUsd = Math.Round(spent.GetValueOrDefault(info.Key), 6);
        }
        return list;
    }

    private string StatusOf(Pool p, bool available)
    {
        var busy = PoolBusy(p);
        if (p.Retired) return busy > 0 ? "retiring" : "retired";
        if (p.Waiters.Count > 0) return "queued";
        if (busy >= p.Capacity) return "full";
        if (busy > 0) return "busy";
        if (p.Disabled) return "disabled";
        return available ? "idle" : "unavailable";
    }

    public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease)
    {
        lease = null;
        if (request.Candidates is { Count: > 1 }) return TryAcquireAny(request, out lease);
        lock (_gate)
        {
            if (_stopped) return false;
            var pool = GetOrCreate(request.Key, request.Provider);
            Pump();
            if (!AvailabilityOf(pool).Available || pool.Waiters.Count > 0 || !CanGrant(pool)) return false;
            var l = NewLease(pool, request);
            if (l is null) return false;
            pool.Owners.Add(l);
            lease = l;
        }
        SchedulePublish();
        return true;
    }

    private bool TryAcquireAny(AgentSlotRequest request, out IAgentSlot? lease)
    {
        lease = null;
        var eligible = Candidates(request);
        lock (_gate)
        {
            if (_stopped) return false;
            Pump();
            foreach (var key in eligible)
            {
                if (!_pools.TryGetValue(key, out var pool) || !AvailabilityOf(pool).Available || pool.Waiters.Count > 0 || !CanGrant(pool)) continue;
                var l = NewLease(pool, Bind(request, pool));
                if (l is null) continue;
                pool.Owners.Add(l);
                lease = l;
                break;
            }
        }
        if (lease is null) return false;
        SchedulePublish();
        return true;
    }

    /// <summary>
    /// A run that may go to several agents waits in none of their queues: the first with a free instance takes it (see
    /// <see cref="Pump"/>), so an agent that frees up first is not passed over because the run was queued behind another.
    /// </summary>
    private ValueTask<IAgentSlot> AcquireAnyAsync(AgentSlotRequest request, CancellationToken ct)
    {
        var eligible = Candidates(request);
        if (eligible.Count == 1) return AcquireAsync(Copy(request, eligible[0], null), ct);
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException("The agent scheduler was stopped (plugin reload).");
            var pools = eligible.Select(k => GetOrCreate(k)).ToList();
            if (!pools.Any(p => AvailabilityOf(p).Available))
                throw new CallRefusedException(NoneAvailable(eligible, string.Join("; ", pools.Select(p => $"{p.Key} ({AvailabilityOf(p).Reason})")))) { Kind = "unavailable" };
            Pump();
            foreach (var pool in pools)
            {
                if (!AvailabilityOf(pool).Available || pool.Waiters.Count > 0 || !CanGrant(pool)) continue;
                var lease = NewLease(pool, Bind(request, pool));
                if (lease is null) continue;
                pool.Owners.Add(lease);
                SchedulePublish();
                return ValueTask.FromResult<IAgentSlot>(lease);
            }
            waiter = new Waiter { Request = Copy(request, eligible[0], eligible), Seq = ++_seq };
            var index = _floating.FindIndex(w => w.Request.Priority < request.Priority);
            if (index < 0) _floating.Add(waiter); else _floating.Insert(index, waiter);
            AttachWaiter(waiter, ct);
            SignalWanted();
        }
        SchedulePublish();
        return new ValueTask<IAgentSlot>(waiter.Tcs.Task);
    }

    public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
    {
        if (request.Candidates is { Count: > 1 }) return AcquireAnyAsync(request, ct);
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException("The agent scheduler was stopped (plugin reload).");
            var pool = GetOrCreate(request.Key, request.Provider);
            var (available, reason) = AvailabilityOf(pool);
            if (!available) throw new CallRefusedException(UnavailableMessage(pool.Key, reason!)) { Kind = "unavailable" };
            Pump();
            if (pool.Waiters.Count == 0 && CanGrant(pool))
            {
                var lease = NewLease(pool, request);
                if (lease is not null)
                {
                    pool.Owners.Add(lease);
                    SchedulePublish();
                    return ValueTask.FromResult<IAgentSlot>(lease);
                }
            }
            // A run waits until it gets a slot or is cancelled (its chat stopped, its parent cancelling it): the queue has
            // no cap and no longest wait, and every waiting run is in the Work tab with its cancel button.
            waiter = new Waiter { Request = request, Seq = ++_seq };
            // priority desc, then FIFO
            var index = pool.Waiters.FindIndex(w => w.Request.Priority < request.Priority);
            if (index < 0) pool.Waiters.Add(waiter); else pool.Waiters.Insert(index, waiter);
            AttachWaiter(waiter, ct);
            SignalWanted();
        }
        SchedulePublish();
        return new ValueTask<IAgentSlot>(waiter.Tcs.Task);
    }

    // Queue publication and cleanup attachment share the gate; no terminal path can race a late attachment.
    private void AttachWaiter(Waiter waiter, CancellationToken ct)
    {
        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(() => Cancel(waiter, ct));
            // An already-cancelled token invokes Cancel inline, before Register returns its handle.
            if (waiter.Tcs.Task.IsCompleted) waiter.Registration.Unregister();
        }
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        var removed = false;
        lock (_gate)
        {
            if (waiter.Floating) removed = _floating.Remove(waiter);
            else if (_pools.TryGetValue(waiter.Request.Key, out var pool))
                removed = pool.Waiters.Remove(waiter);
        }
        if (removed)
        {
            waiter.Registration.Unregister();
            waiter.Tcs.TrySetCanceled(ct);
            SchedulePublish();
        }
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue(lease.Key, out var pool) || !pool.Owners.Remove(lease)) return;
            if (!_stopped) Pump();
            if (!pool.Configured && pool.Owners.Count == 0 && pool.Waiters.Count == 0) _pools.Remove(pool.Key);
        }
        SchedulePublish();
    }

    private static bool Outranks(Waiter a, Waiter b) =>
        a.Request.Priority > b.Request.Priority || (a.Request.Priority == b.Request.Priority && a.Seq < b.Seq);

    /// <summary>The first of an unassigned run's agents that can take it now.</summary>
    private Pool? PoolFor(Waiter w)
    {
        foreach (var key in w.Request.Candidates!)
            if (_pools.TryGetValue(key, out var pool) && AvailabilityOf(pool).Available && CanGrant(pool)) return pool;
        return null;
    }

    /// <summary>
    /// Choose eligible work across pools by priority then arrival, skipping pools at their own cap. The work is the head of
    /// each pool's queue and the runs that wait for any of several agents: one of those goes to the first of its agents
    /// that can take it, so whichever agent frees up first serves the oldest run it is eligible for. A critical run still
    /// waiting afterwards signals the runs that could make room for it (<see cref="SignalWanted"/>).
    /// </summary>
    private void Pump()
    {
        Grant();
        SignalWanted();
    }

    private void Grant()
    {
        while (true)
        {
            Waiter? best = null;
            Pool? bestPool = null;
            foreach (var p in _pools.Values)
            {
                if (p.Waiters.Count == 0 || !CanGrant(p) || !AvailabilityOf(p).Available) continue;
                if (best is null || Outranks(p.Waiters[0], best)) { best = p.Waiters[0]; bestPool = p; }
            }
            foreach (var w in _floating)
            {
                if (best is not null && !Outranks(w, best)) continue;
                if (PoolFor(w) is not { } p) continue;
                best = w;
                bestPool = p;
            }
            if (best is null || bestPool is null) return;
            var lease = NewLease(bestPool, best.Floating ? Bind(best.Request, bestPool) : best.Request);
            if (lease is null) return;
            if (best.Floating) _floating.Remove(best); else bestPool.Waiters.Remove(best);
            best.Registration.Unregister(); // never Dispose under the lock: it would wait for a running callback
            bestPool.Owners.Add(lease);
            if (!best.Tcs.TrySetResult(lease))
            {
                bestPool.Owners.Remove(lease);
                lease.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- a critical run takes a slot

    /// <summary>The waiting runs that take a slot from a pausable run below them (<see cref="RunPriority.Preempts"/>). Caller holds <see cref="_gate"/>.</summary>
    private IEnumerable<Waiter> CriticalWaiters() =>
        _pools.Values.SelectMany(p => p.Waiters).Concat(_floating).Where(w => RunPriority.Preempts(w.Request.Priority));

    /// <summary>
    /// The runs that could make room for <paramref name="w"/>: pausable runs below it on an agent it may go to that is
    /// full, or, when what is full is the slots of the local model that agent runs on, on any agent on that model. Caller
    /// holds <see cref="_gate"/>.
    /// </summary>
    private IEnumerable<Lease> Victims(Waiter w)
    {
        foreach (var key in Candidates(w.Request))
        {
            if (!_pools.TryGetValue(key, out var pool) || !AvailabilityOf(pool).Available) continue;
            IEnumerable<Lease> holders;
            if (PoolBusy(pool) >= pool.Capacity) holders = pool.Owners;
            else if (LocalModelOf(pool) is { } model && ModelSlots(model) is { } slots && BusyOn(model) >= slots)
                holders = _pools.Values.SelectMany(p => p.Owners).Where(o => string.Equals(o.LocalModel, model, StringComparison.OrdinalIgnoreCase));
            else continue;   // there is room: the next grant takes it
            foreach (var lease in holders)
                if (lease.Pausable && !lease.IsReleased && lease.Priority < w.Request.Priority && !RunPriority.Preempts(lease.Priority))
                    yield return lease;
        }
    }

    /// <summary>
    /// Signals every run that could make room for a critical run still waiting (<see cref="IAgentSlot.Wanted"/>): the first of
    /// them to reach a point where it can stop gives its slot up, and the others find no one waiting and go on. Caller
    /// holds <see cref="_gate"/>.
    /// </summary>
    private void SignalWanted()
    {
        foreach (var w in CriticalWaiters())
            foreach (var lease in Victims(w))
                lease.Want();
    }

    /// <summary>
    /// <see cref="IAgentSlot.GiveUpIfWanted"/>: releases <paramref name="lease"/> when it makes room for a critical run that
    /// still waits, and grants that run the slot, all under the gate (a second run signalled for the same one then finds no
    /// one waiting and keeps its slot); otherwise the lease gets a fresh token.
    /// </summary>
    internal bool GiveUp(Lease lease)
    {
        lock (_gate)
        {
            if (lease.IsReleased || _stopped) return false;
            if (!CriticalWaiters().Any(w => Victims(w).Contains(lease)))
            {
                lease.Renew();
                return false;
            }
            lease.MarkReleased();
            lease.ReleasePhysical();
            if (_pools.TryGetValue(lease.Key, out var pool) && pool.Owners.Remove(lease))
            {
                Pump();
                if (!pool.Configured && pool.Owners.Count == 0 && pool.Waiters.Count == 0) _pools.Remove(pool.Key);
            }
        }
        _ctx.Logger.LogInformation("{Agent} gave its slot on {Key} to a critical run", lease.AgentId, lease.Key);
        SchedulePublish();
        return true;
    }

    /// <summary>Fail waiters; active calls keep their host leases until their owners dispose them.</summary>
    public void Stop()
    {
        List<Waiter> waiters = [];
        lock (_gate)
        {
            _stopped = true;
            foreach (var pool in _pools.Values)
            {
                waiters.AddRange(pool.Waiters);
                pool.Waiters.Clear();
            }
            waiters.AddRange(_floating);
            _floating.Clear();
        }
        foreach (var w in waiters)
        {
            w.Registration.Unregister();
            w.Tcs.TrySetException(new OperationCanceledException("The agent scheduler was stopped (plugin reload)."));
        }
    }

    // ---------------------------------------------------------------- events

    private void SchedulePublish()
    {
        if (Interlocked.Exchange(ref _publishScheduled, 1) == 1) return;
        Interlocked.Increment(ref _publishPending);
        _ = Task.Delay(PublishDelayMs).ContinueWith(_ =>
        {
            try
            {
                Volatile.Write(ref _publishScheduled, 0);
                if (_stopped) return;
                PublishNow();
            }
            finally { Interlocked.Decrement(ref _publishPending); }
        }, TaskScheduler.Default);
    }

    public void PublishNow()
    {
        try
        {
            var pools = Snapshot();
            _ctx.Events.Publish(AgentSchedulerEvents.Changed, new JsonObject { ["agents"] = NetPiJson.ToNode(pools), ["resources"] = NetPiJson.ToNode(Resources()), ["unassigned"] = NetPiJson.ToNode(Unassigned()) });
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogDebug(ex, "agents.changed publish failed");
        }
    }
}
