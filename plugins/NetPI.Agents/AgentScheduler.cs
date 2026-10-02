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
/// a cloud provider's models together (<c>models.cloudSlots</c>). Waiters are served by priority (desc), then FIFO.
/// </summary>
internal sealed class AgentScheduler : IAgentScheduler
{
    public const int DefaultCloudCapacity = 4;
    public const int DefaultLocalCapacity = 1;

    private readonly IPluginContext _ctx;
    private readonly Ledger? _usage;
    private readonly IResourceLeases? _resources;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pool> _pools = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every model we have seen (catalog cache + models passed to <see cref="Resolve(ModelInfo)"/>), by ref.</summary>
    private readonly Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private bool _stopped;
    private int _publishScheduled;
    private string _signature = "";

    public AgentScheduler(IPluginContext ctx, Ledger? usage)
    {
        _ctx = ctx;
        _usage = usage;
        _resources = ctx.Services.Get<IResourceLeases>(); // host-owned, lifetime of this host
    }

    /// <summary>Delay used to coalesce <c>agents.changed</c> events.</summary>
    public int PublishDelayMs { get; init; } = 100;

    // ---------------------------------------------------------------- agents

    /// <summary>Keys under <c>agents</c> that are settings, not agent ids.</summary>
    internal static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "maxDepth", "queueMax", "queueTimeoutSeconds" };

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
        if (sessionId is not null && SessionAgent.Of(_ctx.Sessions.GetSession(sessionId)) is { } sid
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
        /// <summary>Fails the waiter when its longest wait (<see cref="QueueTimeoutSeconds"/>) is up; stopped on grant/cancel.</summary>
        public CancellationTokenSource? WaitTimeout { get; set; }
    }

    internal sealed class Lease(AgentScheduler owner, AgentSlotRequest request, string? localModel, IDisposable? physical) : IAgentSlot
    {
        public string? LeaseId => (physical as IResourceLease)?.Id;
        private int _released;
        public string Key { get; } = request.Key;
        public string AgentId { get; } = request.AgentId;
        public string? SessionId { get; } = request.SessionId;
        public string? Label { get; } = request.Label;
        public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        // The admitted call stays on this resource even if its agent is rebound while it runs.
        public string? LocalModel { get; } = localModel;

        /// <summary>Mark released without granting anything (scheduler stopped).</summary>
        internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

        public void Dispose()
        {
            if (!MarkReleased()) return;
            physical?.Dispose();
            owner.Release(this);
        }
    }

    private int Setting(string path, int fallback)
    {
        try
        {
            var n = _ctx.Settings.GetNode(path);
            if (n is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d)) return (int)d;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
            }
        }
        catch { /* bad settings value */ }
        return fallback;
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

    private int LocalCapacity(ModelInfo model) => Math.Max(1,
        model.Concurrency is > 0 ? model.Concurrency.Value : Setting("models.localSlots", DefaultLocalCapacity));

    private int ModelKeyCapacity(ModelInfo model) => model.IsLocal
        ? LocalCapacity(model)
        : Setting("models.cloudSlots", DefaultCloudCapacity);

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
            return m.Status switch
            {
                null or "loaded" => (true, null),
                "offline" => (false, $"{m.Provider} can't be reached"),
                "stopped" => (false, $"{m.Id} isn't loaded (its backend isn't running)"),
                _ => (false, $"{m.Id} isn't loaded"),
            };
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

    public string? ChooseAgent(ModelInfo model, string? agent)
    {
        var agents = Agents();
        if (agents.Count == 0) return null;
        var mine = agents.FirstOrDefault(a => string.Equals(a.Id, agent, StringComparison.OrdinalIgnoreCase));
        if (mine is not null && string.Equals(mine.Model, model.Ref, StringComparison.OrdinalIgnoreCase)) return mine.Id;
        var onModel = agents.Where(a => string.Equals(a.Model, model.Ref, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onModel.Count == 0)
            throw new CallRefusedException($"No agent runs {model.Ref}. Choose an agent for this chat, or set one up on this model (Settings → Agents).") { Kind = "unavailable" };
        lock (_gate)
        {
            _models[model.Ref] = model;
            // an active agent with a free slot, else the active one with the shortest queue, else the first (its reason is the answer)
            var ranked = onModel.Select(a =>
            {
                var pool = GetOrCreate(a.Id);
                ApplyAgent(pool, a);
                return (a.Id, Available: AvailabilityOf(pool).Available, Free: CanGrant(pool) && pool.Waiters.Count == 0, Load: pool.Owners.Count + pool.Waiters.Count);
            }).ToList();
            return ranked.OrderBy(r => r.Available ? 0 : 1).ThenBy(r => r.Free ? 0 : 1).ThenBy(r => r.Load).First().Id;
        }
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
        IReadOnlyList<ModelInfo> cached;
        try { cached = _ctx.Models?.Cached ?? []; } catch { cached = []; }
        var agents = Agents();
        List<(Waiter W, string Reason)> refused = [];

        lock (_gate)
        {
            foreach (var m in cached) _models[m.Ref] = m;
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
            // Finish every removal/disable/capacity change before choosing work across the shared resources.
            Pump();
        }
        foreach (var (w, reason) in refused)
        {
            w.Registration.Unregister();
            StopWaitTimeout(w);
            w.Tcs.TrySetException(new CallRefusedException(UnavailableMessage(w.Request.Key, reason)) { Kind = "unavailable" });
        }
        var signature = Signature();
        var changed = !string.Equals(signature, Interlocked.Exchange(ref _signature, signature), StringComparison.Ordinal);
        if (changed || refused.Count > 0) SchedulePublish();
        return changed;
    }

    /// <summary>What the Work tab shows of the agents (capacity, state): agents.changed only when it changes.</summary>
    private string Signature()
    {
        lock (_gate)
            return string.Join('\n', _pools.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}|{p.Capacity}|{p.Disabled}|{AvailabilityOf(p).Available}|{p.Use}|{p.Model}|{ModelSlots(LocalModelOf(p))}|{p.Source}"));
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
                ? Math.Max(1, Setting("models.localSlots", DefaultLocalCapacity))
                : Math.Max(1, Setting("models.cloudSlots", DefaultCloudCapacity));
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
                    ? g.Value.SelectMany(p => p.Owners).Select(o => new SlotHolder { AgentId = o.AgentId, SessionId = o.SessionId, Label = o.Label, Since = o.AcquiredAt }).ToList()
                    : held.Where(x => string.Equals(x.Resource, g.Key, StringComparison.OrdinalIgnoreCase)).Select(x => x.Holder).ToList();
                return new ModelResourceSlots
                {
                    Key = g.Key, Model = model, Capacity = pool is not null ? ResourceCapacity(pool) : ModelSlots(model) ?? DefaultLocalCapacity,
                    Busy = owners.Count, Queued = g.Value.Sum(p => p.Waiters.Count), Owners = owners,
                    Available = canTakeWork,
                    Unavailable = unavailable,
                };
            }).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
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
                        Owners = held is null ? p.Owners.Select(o => new SlotHolder { AgentId = o.AgentId, SessionId = o.SessionId, Label = o.Label, Since = o.AcquiredAt }).ToList()
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
        if (_usage?.IsOverBudget(request.Provider ?? ProviderOf(request.Key), out _) == true) return false;
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

    /// <summary>How long a pool's wait queue may grow (<c>agents.queueMax</c>): beyond it a new run is refused, not parked.</summary>
    private int QueueMax() => Math.Max(0, Setting("agents.queueMax", 20));

    /// <summary>The longest a waiter may wait for a slot (<c>agents.queueTimeoutSeconds</c>): after it, the run fails.</summary>
    private int QueueTimeoutSeconds() => Math.Max(0, Setting("agents.queueTimeoutSeconds", 600));

    private static string QueueFull(Pool pool, int maxWaiters) => maxWaiters <= 0
        ? $"The agent \"{pool.Key}\" takes no waiting runs (agents.queueMax is 0): run it when a slot is free, or use another agent."
        : $"The agent \"{pool.Key}\" has no free slot and {maxWaiters} run(s) are already queued (the cap is agents.queueMax = {maxWaiters}). Run it again later, or use another agent.";

    private static string QueueTimedOut(string poolKey, int seconds) =>
        $"The agent \"{poolKey}\" had no free slot for {seconds} s (agents.queueTimeoutSeconds). Run it again later, or use another agent.";

    public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
    {
        var provider = request.Provider ?? ProviderOf(request.Key);
        if (_usage is not null && _usage.IsOverBudget(provider, out var message))
            throw new CallRefusedException(message!) { Kind = "budget" };
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        // A burst past the pool's capacity (an unbounded subagents array) used to park in the queue without limit or
        // timeout; both limits are settings, and over either the run is refused with a clear error, not a silent hang.
        var maxWaiters = QueueMax();
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
            if (pool.Waiters.Count >= maxWaiters)
                throw new CallRefusedException(QueueFull(pool, maxWaiters)) { Kind = "unavailable" };
            waiter = new Waiter { Request = request, Seq = ++_seq };
            // priority desc, then FIFO
            var index = pool.Waiters.FindIndex(w => w.Request.Priority < request.Priority);
            if (index < 0) pool.Waiters.Add(waiter); else pool.Waiters.Insert(index, waiter);
            AttachWaiter(waiter, ct);
        }
        SchedulePublish();
        return new ValueTask<IAgentSlot>(waiter.Tcs.Task);
    }

    // Queue publication and cleanup attachment share the gate; no terminal path can race a late attachment.
    private void AttachWaiter(Waiter waiter, CancellationToken ct)
    {
        var queueTimeout = QueueTimeoutSeconds();
        if (queueTimeout is > 0)
        {
            // In line and still stuck (a run that never ends, a slot that never frees): fail the waiter instead of
            // letting it wait forever.
            waiter.WaitTimeout = new CancellationTokenSource();
            var tcs = waiter.Tcs;
            var w = waiter;
            _ = Task.Delay(TimeSpan.FromSeconds(queueTimeout), waiter.WaitTimeout.Token).ContinueWith(_ =>
            {
                bool dropped;
                lock (_gate) dropped = _pools.TryGetValue(w.Request.Key, out var p) && p.Waiters.Remove(w);
                if (dropped)
                {
                    StopWaitTimeout(w);
                    w.Registration.Unregister();
                    tcs.TrySetException(new CallRefusedException(QueueTimedOut(w.Request.Key, queueTimeout)) { Kind = "unavailable" });
                    SchedulePublish();
                }
                // No ExecuteSynchronously: this runs on the pool, so a Cancel from a path holding _gate cannot deadlock.
            }, TaskScheduler.Default);
        }
        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(() => Cancel(waiter, ct));
            // An already-cancelled token invokes Cancel inline, before Register returns its handle.
            if (waiter.Tcs.Task.IsCompleted) waiter.Registration.Unregister();
        }
    }

    private string? ProviderOf(string poolKey)
    {
        lock (_gate)
        {
            if (_pools.TryGetValue(poolKey, out var p) && p.Provider is not null) return p.Provider;
        }
        var slash = poolKey.IndexOf('/');
        return slash > 0 ? poolKey[..slash] : poolKey;
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        var removed = false;
        lock (_gate)
        {
            if (_pools.TryGetValue(waiter.Request.Key, out var pool))
                removed = pool.Waiters.Remove(waiter);
        }
        if (removed)
        {
            waiter.Registration.Unregister();
            StopWaitTimeout(waiter);
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

    /// <summary>Choose eligible work across pools by priority then arrival, skipping pools at their own cap.</summary>
    private void Pump()
    {
        while (true)
        {
            var pool = _pools.Values
                .Where(p => p.Waiters.Count > 0 && CanGrant(p) && AvailabilityOf(p).Available)
                .OrderByDescending(p => p.Waiters[0].Request.Priority)
                .ThenBy(p => p.Waiters[0].Seq)
                .FirstOrDefault();
            if (pool is null) return;
            var w = pool.Waiters[0];
            pool.Waiters.RemoveAt(0);
            var lease = NewLease(pool, w.Request);
            if (lease is null) { pool.Waiters.Insert(0, w); return; }
            w.Registration.Unregister(); // never Dispose under the lock: it would wait for a running callback
            StopWaitTimeout(w);
            pool.Owners.Add(lease);
            if (!w.Tcs.TrySetResult(lease))
            {
                pool.Owners.Remove(lease);
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// Stops a waiter's queue timeout. The delay's continuation holds the Waiter, which lives in this plugin's load
    /// context, so it must not outlive the wait: disposing alone would leave the delay pending for the whole timeout
    /// (minutes by default) and keep the context alive with it. Cancelling completes the delay at once; its
    /// continuation runs on the pool, never inline, so calling this while holding <see cref="_gate"/> is safe.
    /// </summary>
    private static void StopWaitTimeout(Waiter w)
    {
        var cts = w.WaitTimeout;
        if (cts is null) return;
        w.WaitTimeout = null;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
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
        }
        foreach (var w in waiters)
        {
            w.Registration.Unregister();
            StopWaitTimeout(w);
            w.Tcs.TrySetException(new OperationCanceledException("The agent scheduler was stopped (plugin reload)."));
        }
    }

    // ---------------------------------------------------------------- events

    private void SchedulePublish()
    {
        if (Interlocked.Exchange(ref _publishScheduled, 1) == 1) return;
        _ = Task.Delay(PublishDelayMs).ContinueWith(_ =>
        {
            Volatile.Write(ref _publishScheduled, 0);
            if (_stopped) return;
            PublishNow();
        }, TaskScheduler.Default);
    }

    public void PublishNow()
    {
        try
        {
            var pools = Snapshot();
            _ctx.Events.Publish(AgentSchedulerEvents.Changed, new JsonObject { ["agents"] = NetPiJson.ToNode(pools), ["resources"] = NetPiJson.ToNode(Resources()) });
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogDebug(ex, "agents.changed publish failed");
        }
    }
}
