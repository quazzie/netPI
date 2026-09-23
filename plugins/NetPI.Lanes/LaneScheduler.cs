using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Parallel slots per model pool. Pools come from settings (<c>lanes.pools</c>, glob match on the model ref, first match
/// wins), otherwise cloud providers share one pool per provider (<c>lanes.cloudDefaultCapacity</c>) and every local model
/// gets its own pool sized by the catalog's <see cref="ModelInfo.Concurrency"/> (<c>lanes.localDefaultCapacity</c>).
/// Waiters are served by priority (desc), then FIFO.
/// </summary>
internal sealed class LaneScheduler : ILaneScheduler
{
    public const int DefaultCloudCapacity = 4;
    public const int DefaultLocalCapacity = 1;

    private readonly IPluginContext _ctx;
    private readonly UsageTracker? _usage;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pool> _pools = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every model we have seen (catalog cache + models passed to <see cref="ResolvePool"/>), by ref.</summary>
    private readonly Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private long _seq;
    private bool _stopped;
    private int _publishScheduled;

    public LaneScheduler(IPluginContext ctx, UsageTracker? usage)
    {
        _ctx = ctx;
        _usage = usage;
    }

    /// <summary>Delay used to coalesce <c>lanes.changed</c> events.</summary>
    public int PublishDelayMs { get; init; } = 100;

    // ---------------------------------------------------------------- pool definitions

    private sealed record PoolDef(string Key, string? Provider, int Capacity, string Source);

    private sealed class Pool(string key)
    {
        public string Key { get; } = key;
        public string? Provider { get; set; }
        public int Capacity { get; set; } = 1;
        public string Source { get; set; } = "default";
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

    private static IEnumerable<string> ReadStrings(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => !string.IsNullOrWhiteSpace(s)),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        _ => [],
    };

    internal static bool GlobMatch(string pattern, string text)
    {
        var rx = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private PoolDef Define(ModelInfo model)
    {
        var defaultKey = model.IsLocal ? model.Ref : model.Provider;
        var defaultCapacity = model.IsLocal
            ? model.Concurrency ?? Setting("lanes.localDefaultCapacity", DefaultLocalCapacity)
            : Setting("lanes.cloudDefaultCapacity", DefaultCloudCapacity);
        var defaultSource = model.IsLocal && model.Concurrency is not null ? "catalog" : "default";

        JsonObject? pools = null;
        try { pools = _ctx.Settings.GetNode("lanes.pools") as JsonObject; } catch { }
        if (pools is not null)
        {
            // 1. explicit pools with model globs (first match wins)
            foreach (var (key, cfg) in pools)
            {
                if (cfg is not JsonObject o || !o.ContainsKey("models")) continue;
                var globs = ReadStrings(o["models"]).ToList();
                if (globs.Count == 0) continue;
                if (globs.Any(g => GlobMatch(g, model.Ref) || GlobMatch(g, model.Id)))
                    return new PoolDef(key, model.Provider, Math.Max(1, ReadInt(o["capacity"]) ?? defaultCapacity), "settings");
            }
            // 2. a pool entry without "models" whose key is the default pool key overrides its capacity
            foreach (var (key, cfg) in pools)
            {
                if (cfg is JsonObject o && !o.ContainsKey("models") && string.Equals(key, defaultKey, StringComparison.OrdinalIgnoreCase)
                    && ReadInt(o["capacity"]) is { } cap)
                    return new PoolDef(defaultKey, model.Provider, Math.Max(1, cap), "settings");
            }
        }
        return new PoolDef(defaultKey, model.Provider, Math.Max(1, defaultCapacity), defaultSource);
    }

    // ---------------------------------------------------------------- ILaneScheduler

    public string ResolvePool(ModelInfo model)
    {
        var def = Define(model);
        lock (_gate)
        {
            _models[model.Ref] = model;
            var pool = GetOrCreate(def.Key);
            Apply(pool, def);
            if (!pool.Models.Contains(model.Ref, StringComparer.OrdinalIgnoreCase)) pool.Models.Add(model.Ref);
            Pump(pool);
        }
        return def.Key;
    }

    /// <summary>Re-read pool definitions (settings/models changed). Capacity increases wake waiters.</summary>
    public void Refresh()
    {
        IReadOnlyList<ModelInfo> cached;
        try { cached = _ctx.Models?.Cached ?? []; } catch { cached = []; }

        lock (_gate)
        {
            foreach (var m in cached) _models[m.Ref] = m;
            var defs = new Dictionary<string, (PoolDef Def, List<string> Models)>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _models.Values.OrderBy(m => m.Ref, StringComparer.OrdinalIgnoreCase))
            {
                var def = Define(m);
                if (!defs.TryGetValue(def.Key, out var entry)) defs[def.Key] = entry = (def, []);
                entry.Models.Add(m.Ref);
            }
            foreach (var (key, (def, models)) in defs)
            {
                var pool = GetOrCreate(key);
                Apply(pool, def);
                pool.Models.Clear();
                pool.Models.AddRange(models);
            }
            foreach (var pool in _pools.Values.ToList())
            {
                if (!defs.ContainsKey(pool.Key))
                {
                    pool.Models.Clear();
                    if (pool.Owners.Count == 0 && pool.Waiters.Count == 0) { _pools.Remove(pool.Key); continue; }
                }
                Pump(pool);
            }
        }
        SchedulePublish();
    }

    private static void Apply(Pool pool, PoolDef def)
    {
        pool.Capacity = def.Capacity;
        pool.Provider = def.Provider;
        pool.Source = def.Source;
    }

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

    public IReadOnlyList<LanePoolInfo> Snapshot()
    {
        IReadOnlyList<ModelInfo> cached;
        try { cached = _ctx.Models?.Cached ?? []; } catch { cached = []; }
        var needRefresh = false;
        lock (_gate) needRefresh = cached.Any(m => !_models.ContainsKey(m.Ref));
        if (needRefresh) Refresh();

        lock (_gate)
        {
            return _pools.Values
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new LanePoolInfo
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
                    Status = StatusOf(p),
                })
                .ToList();
        }
    }

    private string StatusOf(Pool p)
    {
        if (p.Waiters.Count > 0) return "queued";
        if (p.Owners.Count >= p.Capacity) return "full";
        if (p.Owners.Count > 0) return "busy";
        // Surface backend state for idle local pools (offline/stopped models).
        var states = p.Models.Select(r => _models.TryGetValue(r, out var m) ? m.Status : null).Where(s => s is not null).ToList();
        if (states.Count > 0 && states.All(s => s is "offline" or "stopped")) return states[0]!;
        return "idle";
    }

    public bool TryAcquire(LaneRequest request, out ILaneLease? lease)
    {
        lease = null;
        if (_usage?.IsOverBudget(request.Provider ?? ProviderOf(request.PoolKey), out _) == true) return false;
        lock (_gate)
        {
            if (_stopped) return false;
            var pool = GetOrCreate(request.PoolKey, request.Provider);
            if (pool.Waiters.Count > 0 || pool.Owners.Count >= pool.Capacity) return false;
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
            if (pool.Waiters.Count == 0 && pool.Owners.Count < pool.Capacity)
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
            if (pool.Owners.Count == 0 && pool.Waiters.Count == 0 && pool.Models.Count == 0) _pools.Remove(pool.Key);
        }
        SchedulePublish();
    }

    /// <summary>Grant free slots to waiters. Caller holds <see cref="_gate"/>.</summary>
    private void Pump(Pool pool)
    {
        while (pool.Owners.Count < pool.Capacity && pool.Waiters.Count > 0)
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
