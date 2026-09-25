using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Parallel slots (pools in the code). The agents the user sets up are the pools: <c>agents.&lt;id&gt; = { model,
/// instances, use, disabled, budget, cost }</c>, one slot per instance; chats and subagents run on them. An agent takes
/// work only while it is not disabled and its model is loaded (local: AiProxy's status) or reachable (cloud); NetPI never
/// loads a model. Agents on one local model share its slots (the catalog's <see cref="ModelInfo.Concurrency"/>): no more
/// runs on the model than it serves. Model calls without an agent (a separate summarizer, and every call while no agent
/// is set up) get a slot per model while they run: a local model by its concurrency (<c>lanes.localDefaultCapacity</c>),
/// a cloud provider's models together (<c>lanes.cloudDefaultCapacity</c>). Waiters are served by priority (desc), then FIFO.
/// </summary>
internal sealed class LaneScheduler : ILaneScheduler
{
    public const int DefaultCloudCapacity = 4;
    public const int DefaultLocalCapacity = 1;

    private readonly IPluginContext _ctx;
    private readonly Ledger? _usage;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pool> _pools = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every model we have seen (catalog cache + models passed to <see cref="ResolvePool(ModelInfo)"/>), by ref.</summary>
    private readonly Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private bool _stopped;
    private int _publishScheduled;
    private string _signature = "";

    public LaneScheduler(IPluginContext ctx, Ledger? usage)
    {
        _ctx = ctx;
        _usage = usage;
    }

    /// <summary>Delay used to coalesce <c>lanes.changed</c> events.</summary>
    public int PublishDelayMs { get; init; } = 100;

    // ---------------------------------------------------------------- agents

    /// <summary>Keys under <c>agents</c> that are settings, not agent ids.</summary>
    internal static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "maxDepth" };

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
    internal (string Lane, JsonObject? Cfg) LaneFor(ModelInfo model, string? sessionId = null)
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
        public string? Model { get; set; }
        public string? Use { get; set; }
        public bool Disabled { get; set; }
        public List<string> Models { get; } = [];
        public List<Lease> Owners { get; } = [];
        public List<Waiter> Waiters { get; } = [];
    }

    private sealed class Waiter
    {
        public required LaneRequest Request { get; init; }
        public required long Seq { get; init; }
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<ILaneLease> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }

    internal sealed class Lease(LaneScheduler owner, LaneRequest request) : ILaneLease
    {
        private int _released;
        public string PoolKey { get; } = request.PoolKey;
        public string AgentId { get; } = request.AgentId;
        public string? SessionId { get; } = request.SessionId;
        public string? Label { get; } = request.Label;
        public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>Mark released without granting anything (scheduler stopped).</summary>
        internal bool MarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

        public void Dispose()
        {
            if (MarkReleased()) owner.Release(this);
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

    private int ModelKeyCapacity(ModelInfo model) => model.IsLocal
        ? model.Concurrency ?? Setting("lanes.localDefaultCapacity", DefaultLocalCapacity)
        : Setting("lanes.cloudDefaultCapacity", DefaultCloudCapacity);

    /// <summary>Default instances of an agent: a local model's slots, 1 on a cloud model (it may cost money).</summary>
    internal int DefaultInstances(ModelInfo? model) =>
        model is { IsLocal: true } ? model.Concurrency ?? Setting("lanes.localDefaultCapacity", DefaultLocalCapacity) : 1;

    private void ApplyAgent(Pool pool, AgentConfig a)
    {
        var model = _models.GetValueOrDefault(a.Model);
        pool.Configured = true;
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

    /// <summary>The model's own slots (local models with a known concurrency), shared by every pool on it.</summary>
    private int? ModelSlots(string? localModel) =>
        localModel is not null && _models.TryGetValue(localModel, out var m) ? m.Concurrency : null;

    private int BusyOn(string localModel) => _pools.Values.Where(p => LocalModelOf(p) == localModel).Sum(p => p.Owners.Count);

    /// <summary>A free slot in the pool, and on its model. Caller holds <see cref="_gate"/>.</summary>
    private bool CanGrant(Pool pool)
    {
        if (pool.Owners.Count >= pool.Capacity) return false;
        var model = LocalModelOf(pool);
        return ModelSlots(model) is not { } slots || BusyOn(model!) < slots;
    }

    /// <summary>Whether an agent can take work now, and why not. Caller holds <see cref="_gate"/>.</summary>
    private (bool Available, string? Reason) AvailabilityOf(Pool p)
    {
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

    // ---------------------------------------------------------------- ILaneScheduler

    public string ResolvePool(ModelInfo model)
    {
        var key = ModelKey(model);
        lock (_gate)
        {
            _models[model.Ref] = model;
            var pool = GetOrCreate(key);
            if (!pool.Configured)
            {
                pool.Provider = model.Provider;
                pool.Capacity = Math.Max(1, ModelKeyCapacity(model));
                pool.Source = model.IsLocal && model.Concurrency is not null ? "catalog" : "default";
                if (!pool.Models.Contains(model.Ref, StringComparer.OrdinalIgnoreCase)) pool.Models.Add(model.Ref);
            }
            Pump(pool);
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
            throw new LaneUnavailableException($"No agent runs {model.Ref}. Choose an agent for this chat, or set one up on this model (Settings → Agents).");
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

    public string ResolvePool(ModelInfo model, string? agent)
    {
        if (Agent(agent) is not { } a) return ResolvePool(model);
        lock (_gate)
        {
            _models[model.Ref] = model;
            var pool = GetOrCreate(a.Id);
            ApplyAgent(pool, a);
            Pump(pool);
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
                    // an agent that was removed: its runs finish, nothing new starts on it
                    pool.Configured = false;
                    pool.Models.Clear();
                }
                if (!pool.Configured && pool.Owners.Count == 0 && pool.Waiters.Count == 0) { _pools.Remove(pool.Key); continue; }
                var (available, reason) = AvailabilityOf(pool);
                if (!available)
                {
                    // waiting on an agent that can't take work: tell them now instead of letting them wait
                    foreach (var w in pool.Waiters) refused.Add((w, reason!));
                    pool.Waiters.Clear();
                    continue;
                }
                Pump(pool);
            }
        }
        foreach (var (w, reason) in refused)
        {
            w.Registration.Unregister();
            w.Tcs.TrySetException(new LaneUnavailableException(UnavailableMessage(w.Request.PoolKey, reason)));
        }
        var signature = Signature();
        var changed = !string.Equals(signature, Interlocked.Exchange(ref _signature, signature), StringComparison.Ordinal);
        if (changed || refused.Count > 0) SchedulePublish();
        return changed;
    }

    /// <summary>What the Work tab shows of the agents (capacity, state): lanes.changed only when it changes.</summary>
    private string Signature()
    {
        lock (_gate)
            return string.Join('\n', _pools.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}|{p.Capacity}|{p.Disabled}|{AvailabilityOf(p).Available}|{p.Use}|{p.Model}"));
    }

    internal static string UnavailableMessage(string agent, string reason) => reason == "disabled"
        ? $"The agent \"{agent}\" is disabled. Enable it (Settings → Agents, or its switch in the Work tab) or choose another agent."
        : $"The agent \"{agent}\" can't take work now: {reason}. Load its model (AiSwitcher) or choose another agent.";

    private Pool GetOrCreate(string key, string? provider = null)
    {
        if (_pools.TryGetValue(key, out var pool)) return pool;
        pool = new Pool(key) { Provider = provider };
        // A pool we have never resolved: guess by the key shape ("provider/model" = local model, else cloud provider).
        pool.Capacity = key.Contains('/')
            ? Math.Max(1, Setting("lanes.localDefaultCapacity", DefaultLocalCapacity))
            : Math.Max(1, Setting("lanes.cloudDefaultCapacity", DefaultCloudCapacity));
        _pools[key] = pool;
        return pool;
    }

    /// <summary>The agents (always) and the other slots while they are busy.</summary>
    public IReadOnlyList<LanePoolInfo> Snapshot()
    {
        IReadOnlyList<ModelInfo> cached;
        try { cached = _ctx.Models?.Cached ?? []; } catch { cached = []; }
        var needRefresh = false;
        lock (_gate) needRefresh = cached.Any(m => !_models.ContainsKey(m.Ref)) || Agents().Any(a => !_pools.ContainsKey(a.Id));
        if (needRefresh) Refresh();

        var configs = Agents().ToDictionary(a => a.Id, a => a.Cfg, StringComparer.OrdinalIgnoreCase);
        List<LanePoolInfo> list;
        lock (_gate)
        {
            list = _pools.Values
                .Where(p => p.Configured || p.Owners.Count > 0 || p.Waiters.Count > 0)
                .OrderBy(p => p.Configured ? 0 : 1)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var (available, reason) = AvailabilityOf(p);
                    var info = new LanePoolInfo
                    {
                        Key = p.Key,
                        Provider = p.Provider,
                        Capacity = p.Capacity,
                        Busy = p.Owners.Count,
                        Queued = p.Waiters.Count,
                        Models = [.. p.Models],
                        Owners = p.Owners.Select(o => new LaneOwnerInfo { AgentId = o.AgentId, SessionId = o.SessionId, Label = o.Label, Since = o.AcquiredAt }).ToList(),
                        Waiters = p.Waiters.Select(w => new LaneOwnerInfo { AgentId = w.Request.AgentId, SessionId = w.Request.SessionId, Label = w.Request.Label, Since = w.Since }).ToList(),
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
                    info.DailyLimitUsd = Ledger.LaneLimit(configs.GetValueOrDefault(p.Key));
                    return info;
                })
                .ToList();
        }
        if (_usage is not null)
            foreach (var info in list) info.SpentTodayUsd = Math.Round(_usage.LaneSpentToday(info.Key), 6);
        return list;
    }

    private static string StatusOf(Pool p, bool available)
    {
        if (p.Waiters.Count > 0) return "queued";
        if (p.Owners.Count >= p.Capacity) return "full";
        if (p.Owners.Count > 0) return "busy";
        if (p.Disabled) return "disabled";
        return available ? "idle" : "unavailable";
    }

    public bool TryAcquire(LaneRequest request, out ILaneLease? lease)
    {
        lease = null;
        if (_usage?.IsOverBudget(request.Provider ?? ProviderOf(request.PoolKey), out _) == true) return false;
        lock (_gate)
        {
            if (_stopped) return false;
            var pool = GetOrCreate(request.PoolKey, request.Provider);
            if (!AvailabilityOf(pool).Available || pool.Waiters.Count > 0 || !CanGrant(pool)) return false;
            var l = new Lease(this, request);
            pool.Owners.Add(l);
            lease = l;
        }
        SchedulePublish();
        return true;
    }

    public ValueTask<ILaneLease> AcquireAsync(LaneRequest request, CancellationToken ct)
    {
        var provider = request.Provider ?? ProviderOf(request.PoolKey);
        if (_usage is not null && _usage.IsOverBudget(provider, out var message))
            throw new BudgetExceededException(message!);
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (_stopped) throw new OperationCanceledException("The lane scheduler was stopped (plugin reload).");
            var pool = GetOrCreate(request.PoolKey, request.Provider);
            var (available, reason) = AvailabilityOf(pool);
            if (!available) throw new LaneUnavailableException(UnavailableMessage(pool.Key, reason!));
            if (pool.Waiters.Count == 0 && CanGrant(pool))
            {
                var lease = new Lease(this, request);
                pool.Owners.Add(lease);
                SchedulePublish();
                return ValueTask.FromResult<ILaneLease>(lease);
            }
            waiter = new Waiter { Request = request, Seq = ++_seq };
            // priority desc, then FIFO
            var index = pool.Waiters.FindIndex(w => w.Request.Priority < request.Priority);
            if (index < 0) pool.Waiters.Add(waiter); else pool.Waiters.Insert(index, waiter);
        }
        if (ct.CanBeCanceled)
            waiter.Registration = ct.Register(() => Cancel(waiter, ct));
        SchedulePublish();
        return new ValueTask<ILaneLease>(waiter.Tcs.Task);
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
            if (_pools.TryGetValue(waiter.Request.PoolKey, out var pool))
                removed = pool.Waiters.Remove(waiter);
        }
        if (removed)
        {
            waiter.Tcs.TrySetCanceled(ct);
            SchedulePublish();
        }
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue(lease.PoolKey, out var pool) || !pool.Owners.Remove(lease)) return;
            Pump(pool);
            // a slot on a shared local model: the other pools on it may go on
            if (LocalModelOf(pool) is { } model)
                foreach (var other in _pools.Values.Where(p => p != pool && LocalModelOf(p) == model).ToList()) Pump(other);
            if (!pool.Configured && pool.Owners.Count == 0 && pool.Waiters.Count == 0) _pools.Remove(pool.Key);
        }
        SchedulePublish();
    }

    /// <summary>Grant free slots to waiters. Caller holds <see cref="_gate"/>.</summary>
    private void Pump(Pool pool)
    {
        while (pool.Waiters.Count > 0 && CanGrant(pool) && AvailabilityOf(pool).Available)
        {
            var w = pool.Waiters[0];
            pool.Waiters.RemoveAt(0);
            w.Registration.Unregister(); // never Dispose under the lock: it would wait for a running callback
            var lease = new Lease(this, w.Request);
            pool.Owners.Add(lease);
            if (!w.Tcs.TrySetResult(lease))
            {
                pool.Owners.Remove(lease);
                lease.MarkReleased();
            }
        }
    }

    /// <summary>Plugin stop: fail all waiters and forget all leases (their owners re-acquire from the next scheduler).</summary>
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
                foreach (var o in pool.Owners) o.MarkReleased();
                pool.Owners.Clear();
            }
        }
        foreach (var w in waiters)
        {
            w.Registration.Unregister();
            w.Tcs.TrySetException(new OperationCanceledException("The lane scheduler was stopped (plugin reload)."));
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
            _ctx.Events.Publish(EventTypes.LanesChanged, new JsonObject { ["pools"] = NetPiJson.ToNode(pools) });
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogDebug(ex, "lanes.changed publish failed");
        }
    }
}
