using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

// The ledger's storage (ctx.Data, plugin "netpi.agents"). Document shapes, kept here for the one-off store migration:
//
// usage_calls — one document per model call: reserved when the call starts, settled in place when it ends (a reservation
//   of a process that died before settling becomes "interrupted-estimate" at the next start: Ledger.SweepAbandoned).
//   key:   the call id, a decimal string allocated from the "usage_calls" counter in "counters" (never reused;
//          migrate the old ids, and set the counter to the old maximum)
//   doc:   { ts, day, period, sessionId?, rootSessionId?, agentId?, lane?, provider, model, purpose,
//            inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd, costSource }
//          (the old usage_calls columns one-to-one; period = the budget period the call is charged to, the "yyyy-MM-dd"
//           of the day it started; costSource: "reserved", "reported", "estimated", "free",
//           "unknown", "rejected", "interrupted-estimate")
//   index: ts Integer, day Text, sessionId Text, rootSessionId Text, lane Text, costUsd Real, costSource Text
//
// period_usage — calls, tokens and cost per (period, lane, provider, model), upserted in every call's transaction and
//   corrected by its settlement, so the period's per-model view is a read of these documents, not a scan of its calls:
//   key:   <period>|<laneKey>|<provider>|<model>     (period = the day the budget period starts, "yyyy-MM-dd";
//          laneKey = the lane lower-cased, "" when the call has no lane)
//   doc:   { period, lane?, provider, model, calls, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens,
//            costUsd, unknownCalls }
//   index: period Text, calls Integer
//
// lanes_usage — tokens per (day, provider, model), upserted with every recorded call.
//   key:   <day>|<provider>|<model>
//   doc:   { day, provider, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, calls }
//          (rows written before 2026-10-08 also carry budgetTokens, the sum the per-provider token budget counted: it is gone)
//   index: day Text, provider Text, model Text
//
// lane_usage — the cost per (day, lane): the lane's day gets the call's cost when it is recorded (a reservation at
//   its estimate), and Settle replaces the estimate with the settled cost.
//   key:   <day>|<laneKey>             (laneKey = the lane lower-cased; a call with no lane has no document)
//   doc:   { day, lane, laneKey, costUsd }
//   index: day Text, laneKey Text, costUsd Real
//
// day_usage — the calls and cost per day (the usage tab's day chart reads these, not the day's call rows): added to when a call
//   is recorded (a reservation at its estimate) and corrected by its settlement, on the day the call started.
//   key:   <day>
//   doc:   { day, calls, costUsd }
//   index: day Text
//
// session_usage — what each chat cost, so a chat's cost does not need its call rows (idea-sdevr6): its own calls, and every
//   call of its tree (the calls whose root it is), upserted with each call and corrected by its settlement.
//   key:   s|<sessionId>  { sessionId, calls, costUsd, reservedUsd }         (the chat's own calls)
//          r|<rootId>     { rootSessionId, calls, costUsd, reservedUsd }     (the calls of the chat and its subagents)
//   index: none
//
// counters — the plugin's sequences, and marks.
//   key:   the counter name ("usage_calls"), or "session_usage" / "day_usage" once that roll-up was built from the calls
//   doc:   { value } / { built: true }
//   index: none
//
// Retention: a usage_calls row older than budget.keepCallsDays (default 90, at least 62, 0 = never) and before the current
// period is deleted at start and every few thousand calls; the roll-ups above keep the totals.

/// <summary>
/// Every model call with its tokens and cost, and the budgets.
/// <list type="bullet">
/// <item><c>usage_calls</c>: one row per call (agents, compaction, anything that asks a model), recorded by
/// <see cref="LedgerMiddleware"/>. The cost is what the provider reported (<see cref="Usage.CostUsd"/>: OpenRouter),
/// else tokens × the price (the agent's <c>cost</c>, else the catalog's pricing); local models are free; other cloud
/// models without a price are "unknown" and cannot start under a dollar cap without an explicit override.</item>
/// <item><c>lanes_usage</c>: tokens per day, provider and model (the Work tab).</item>
/// <item>Budgets: <c>budget.monthlyUsd</c> (the month starts on <c>budget.resetDay</c>), <c>budget.dailyUsd</c> and a
/// agent's <c>agents.&lt;id&gt;.budget.limitUsd</c> per day. When one is spent, calls to paid models throw
/// <see cref="CallRefusedException"/>; <c>budget.onLimit</c> "ask" lets the user allow a chat to go over
/// (<c>session.meta.budgetAllowedFrom</c> = the start of the period).</item>
/// </list>
/// </summary>
internal sealed partial class Ledger
{
    /// <summary>The session meta key that holds a chat's allowance to go over the budget (the start of the period it was given for). A fork starts without it: the plugin declares it as run state.</summary>
    internal const string AllowanceMetaKey = "budgetAllowedFrom";

    public const double CacheReadShare = 0.1;   // of the input price, when no cache price is known (Anthropic's rate)
    public const double CacheWriteShare = 1.25;

    private readonly IPluginContext _ctx;
    private int _changeScheduled;
    private int _stopped;

    // The ledger's own storage (ctx.Data), opened in Initialize; all null while the store is unavailable. Paid calls
    // under a limit are then refused (fail-closed, below), and nothing is kept in memory.
    private IDataCollection? _calls;        // usage_calls: one document per call
    private IDataCollection? _lanesUsage;   // lanes_usage: tokens per day, provider and model
    private IDataCollection? _laneUsage;    // lane_usage: the cost per day and lane (an agent's pool for the day)
    private IDataCollection? _periodUsage;  // period_usage: calls, tokens and cost per period, lane, provider and model
    private IDataCollection? _dayUsage;     // day_usage: calls and cost per day
    private IDataCollection? _sessionUsage; // session_usage: calls and cost per chat, and per chat with its subagents
    private IDataCollection? _counters;     // per-plugin sequences

    /// <summary>Days a call's own row is kept (<c>budget.keepCallsDays</c>).</summary>
    public const int DefaultKeepCallsDays = 90;
    /// <summary>The least it may be: a changed <c>budget.resetDay</c> rebuilds the current period from its calls, which reach up to two months back.</summary>
    public const int MinKeepCallsDays = 62;

    public sealed class Totals
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long Calls;
    }

    /// <summary>USD per million tokens.</summary>
    internal sealed record Price(double Input, double Output, double CacheRead, double CacheWrite, string Source)
    {
        public bool Free => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0;
    }

    internal sealed record BudgetOptions(double? MonthlyUsd, double? DailyUsd, int WarnPercent, int ResetDay, string OnLimit);

    public Ledger(IPluginContext ctx)
    {
        _ctx = ctx;
    }

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Publishes <c>usage.changed</c> (debounced) after calls were recorded.</summary>
    public int ChangeDelayMs { get; init; } = 300;

    public void Initialize()
    {
        try
        {
            // All of them or none: a half-opened ledger would let a free call through to a null collection and fail it.
            var calls = _ctx.Data.Collection("usage_calls", new CollectionSpec()
                .Integer("ts").Text("day").Text("sessionId").Text("rootSessionId").Text("lane")
                .Real("costUsd").Text("costSource"));
            var lanesUsage = _ctx.Data.Collection("lanes_usage", new CollectionSpec()
                .Text("day").Text("provider").Text("model"));
            var laneUsage = _ctx.Data.Collection("lane_usage", new CollectionSpec()
                .Text("day").Text("laneKey").Real("costUsd"));
            var periodUsage = _ctx.Data.Collection("period_usage", new CollectionSpec()
                .Text("period").Integer("calls"));
            var dayUsage = _ctx.Data.Collection("day_usage", new CollectionSpec().Text("day"));
            var sessionUsage = _ctx.Data.Collection("session_usage", new CollectionSpec());
            var counters = _ctx.Data.Collection("counters", new CollectionSpec());
            _calls = calls;
            _lanesUsage = lanesUsage;
            _laneUsage = laneUsage;
            _periodUsage = periodUsage;
            _dayUsage = dayUsage;
            _sessionUsage = sessionUsage;
            _counters = counters;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Usage storage unavailable; paid calls are stopped until it is repaired");
        }
        // The roll-up is maintained in every call's transaction; a period it has not caught up on (a store that
        // pre-dates it, or a changed budget.resetDay) is rebuilt from the calls once, in one transaction.
        RebuildPeriodUsage();
        // The chats' roll-up is built from the calls once (a store from before it), then kept with every call; only then
        // may old call rows go (idea-sdevr6).
        try
        {
            if (_calls is not null && _sessionUsage is not null && _counters is not null) _ctx.Data.Transaction(CatchUpSessionUsage);
            if (_calls is not null && _dayUsage is not null && _counters is not null) _ctx.Data.Transaction(CatchUpDayUsage);
            SweepAbandoned(ProcessStartMs());
            PruneCalls();
        }
        catch (Exception ex) { _ctx.Logger.LogWarning(ex, "The chats' usage roll-up could not be built; old call rows are kept"); }
    }

    /// <summary>Build <c>session_usage</c> from the calls, once: paged, inside the caller's transaction.</summary>
    private void CatchUpSessionUsage()
    {
        if (_counters!.Get("session_usage") is not null) return;
        var totals = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        void Add(string key, string field, string id, double cost, double reserved)
        {
            if (!totals.TryGetValue(key, out var doc))
                totals[key] = doc = new JsonObject { [field] = id, ["calls"] = 0L, ["costUsd"] = 0.0, ["reservedUsd"] = 0.0 };
            doc["calls"] = L(doc["calls"]) + 1;
            doc["costUsd"] = Usd6(D(doc["costUsd"]) + cost);
            doc["reservedUsd"] = Usd6(D(doc["reservedUsd"]) + reserved);
        }
        const int Page = 2000;
        for (var offset = 0; ; offset += Page)
        {
            var page = _calls!.Find(new DataQuery { Offset = offset }.Order("ts").Take(Page));
            foreach (var d in page)
            {
                var cost = D(d.Doc["costUsd"]);
                var reserved = d.Doc["costSource"]?.GetValue<string>() == "reserved" ? cost : 0;
                if (d.Doc["sessionId"]?.GetValue<string>() is { Length: > 0 } sid) Add("s|" + sid, "sessionId", sid, cost, reserved);
                if (d.Doc["rootSessionId"]?.GetValue<string>() is { Length: > 0 } root) Add("r|" + root, "rootSessionId", root, cost, reserved);
            }
            if (page.Count < Page) break;
        }
        _sessionUsage!.DeleteWhere(new DataQuery());
        foreach (var (key, doc) in totals) _sessionUsage.Put(key, doc);
        _counters.Put("session_usage", new JsonObject { ["built"] = true });
    }

    /// <summary>Build <c>day_usage</c> from the calls, once (a store from before it): paged, inside the caller's transaction.</summary>
    private void CatchUpDayUsage()
    {
        if (_counters!.Get("day_usage") is not null) return;
        var perDay = new Dictionary<string, (long Calls, double Cost)>(StringComparer.Ordinal);
        ScanCalls(new DataQuery(), int.MaxValue, doc =>
        {
            if (doc["day"]?.GetValue<string>() is not { } day) return;
            var (n, c) = perDay.GetValueOrDefault(day);
            perDay[day] = (n + 1, c + D(doc["costUsd"]));
        });
        _dayUsage!.DeleteWhere(new DataQuery());
        foreach (var (day, (n, c)) in perDay)
            _dayUsage.Put(day, new JsonObject { ["day"] = day, ["calls"] = n, ["costUsd"] = Usd6(c) });
        _counters.Put("day_usage", new JsonObject { ["built"] = true });
    }

    /// <summary>A call (calls 1) or its settlement (calls 0, the cost's change) in the day's roll-up, in the caller's transaction.</summary>
    private void DayUsageAdd(string day, int calls, double cost)
    {
        if (_dayUsage is null) return;
        var doc = _dayUsage.Get(day) ?? new JsonObject { ["day"] = day, ["calls"] = 0L, ["costUsd"] = 0.0 };
        doc["calls"] = L(doc["calls"]) + calls;
        doc["costUsd"] = Usd6(D(doc["costUsd"]) + cost);
        _dayUsage.Put(day, doc);
    }

    private static long ProcessStartMs()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new DateTimeOffset(self.StartTime).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// A reservation recorded before <paramref name="beforeMs"/> (this process's start) belongs to a process that died
    /// between reserve and settle: nothing will ever settle it. It becomes a terminal <c>interrupted-estimate</c> at its
    /// reserved cost (still counted: what the dead call was billed is unknown, so the estimate stays conservative), leaves
    /// the chats' reserved figures, and is pruned like any settled row. A reservation of an earlier plugin generation in
    /// this process is younger than the process and is left to settle. The number swept.
    /// </summary>
    public int SweepAbandoned(long beforeMs)
    {
        if (_calls is null) return 0;
        return _ctx.Data.Transaction(() =>
        {
            var stale = _calls.Find(new DataQuery().Lt("ts", beforeMs).Eq("costSource", "reserved"));
            foreach (var d in stale)
            {
                var cost = D(d.Doc["costUsd"]);
                d.Doc["costSource"] = "interrupted-estimate";
                _calls.Put(d.Key, d.Doc);
                SessionUsageAdd(d.Doc["sessionId"]?.GetValue<string>(), d.Doc["rootSessionId"]?.GetValue<string>(), 0, 0, -cost);
            }
            if (stale.Count > 0)
                _ctx.Logger.LogInformation("Usage: {Count} reservations of an earlier process were never settled; kept as interrupted estimates", stale.Count);
            return stale.Count;
        });
    }

    /// <summary>
    /// A call (or its settlement) in the chats' roll-up: the chat's own line and its tree's, in the caller's transaction.
    /// <paramref name="reserved"/> moves the part still at a reservation (a settlement takes it back out).
    /// </summary>
    private void SessionUsageAdd(string? sessionId, string? root, int calls, double cost, double reserved)
    {
        if (_sessionUsage is null) return;
        void Add(string key, string field, string id)
        {
            var doc = _sessionUsage.Get(key) ?? new JsonObject { [field] = id, ["calls"] = 0L, ["costUsd"] = 0.0, ["reservedUsd"] = 0.0 };
            doc["calls"] = L(doc["calls"]) + calls;
            doc["costUsd"] = Usd6(D(doc["costUsd"]) + cost);
            doc["reservedUsd"] = Usd6(Math.Max(0, D(doc["reservedUsd"]) + reserved));
            _sessionUsage.Put(key, doc);
        }
        if (!string.IsNullOrEmpty(sessionId)) Add("s|" + sessionId, "sessionId", sessionId);
        if (!string.IsNullOrEmpty(root)) Add("r|" + root, "rootSessionId", root);
    }

    /// <summary>
    /// Delete the call rows older than <c>budget.keepCallsDays</c> (and before the current period): one row per model call is
    /// thousands a day, and the roll-ups keep the totals (idea-sdevr6). A reservation is never deleted. The number deleted.
    /// </summary>
    public int PruneCalls()
    {
        if (_calls is null) return 0;
        var days = (int)(Number(_ctx.Settings.GetNode("budget.keepCallsDays")) ?? DefaultKeepCallsDays);
        if (days <= 0) return 0;
        days = Math.Max(days, MinKeepCallsDays);
        // until the chats' roll-up exists, their costs come from these rows: keep them
        if (_counters?.Get("session_usage") is null || _counters.Get("day_usage") is null) return 0;
        var before = Math.Min(new DateTimeOffset(DateTime.Now.AddDays(-days)).ToUnixTimeMilliseconds(),
            new DateTimeOffset(PeriodStart).ToUnixTimeMilliseconds());
        var deleted = _ctx.Data.Transaction(() => _calls.DeleteWhere(new DataQuery().Lt("ts", before).NotIn("costSource", ["reserved"])));
        if (deleted > 0) _ctx.Logger.LogInformation("Usage: deleted {Count} model call rows older than {Days} days (the totals stay)", deleted, days);
        return deleted;
    }

    /// <summary>
    /// The start's catch-up, on demand: a changed <c>budget.resetDay</c> moved the current period's border while the
    /// app runs, and the roll-up the calls maintain is keyed by the period — so the period is rebuilt from the calls,
    /// in one transaction, before the next <c>usage.changed</c> goes out.
    /// </summary>
    public void RebuildPeriodUsage()
    {
        if (_calls is null || _periodUsage is null) return;   // the store is unavailable: nothing to rebuild, and a paid call is refused anyway
        try { _ctx.Data.Transaction(CatchUpPeriodUsage); }
        catch (Exception ex) { _ctx.Logger.LogDebug(ex, "The period usage roll-up could not be rebuilt"); }
    }

    // ---------------------------------------------------------------- settings

    public BudgetOptions Options()
    {
        var onLimit = Str("budget.onLimit")?.Trim().ToLowerInvariant() == "ask" ? "ask" : "stop";
        return new BudgetOptions(Positive(_ctx.Settings.GetNode("budget.monthlyUsd")), Positive(_ctx.Settings.GetNode("budget.dailyUsd")),
            Math.Clamp((int)(Number(_ctx.Settings.GetNode("budget.warnPercent")) ?? 80), 1, 100),
            Math.Clamp((int)(Number(_ctx.Settings.GetNode("budget.resetDay")) ?? 1), 1, 28), onLimit);
    }

    private string? Str(string path)
    {
        try { return _ctx.Settings.GetNode(path) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch { return null; }
    }

    internal static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        var s = v.ToJsonString().Trim('"');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    private static double? Positive(JsonNode? n) => Number(n) is > 0 and var d ? d : null;

    /// <summary>The price of a model: the agent's <c>cost</c> ($/Mtok), else the catalog's pricing ($/token), else free for local models.</summary>
    public static Price? PriceOf(ModelInfo model, JsonObject? agentCfg)
    {
        if (agentCfg?["cost"] is JsonObject c && Number(c["input"]) is >= 0 && Number(c["output"]) is >= 0)
        {
            var input = Number(c["input"]) ?? 0;
            return new Price(input, Number(c["output"]) ?? 0, Math.Max(0, Number(c["cacheRead"]) ?? input * CacheReadShare),
                Math.Max(0, Number(c["cacheWrite"]) ?? input * CacheWriteShare), "settings");
        }
        if (model.Extra?["pricing"] is JsonObject p && Number(p["prompt"]) is >= 0 and var prompt && Number(p["completion"]) is >= 0 and var completion)
        {
            static double PerM(double perToken) => Math.Round(perToken * 1_000_000, 6);
            var input = PerM(prompt);
            return new Price(input, PerM(completion),
                Number(p["input_cache_read"]) is >= 0 and var cr ? PerM(cr) : input * CacheReadShare,
                Number(p["input_cache_write"]) is >= 0 and var cw ? PerM(cw) : input * CacheWriteShare, "catalog");
        }
        if (model.IsLocal) return new Price(0, 0, 0, 0, "local");
        return null;
    }

    /// <summary>Costs money: a cloud model that is not known to be free (an unknown price counts as paid).</summary>
    public static bool Paid(ModelInfo model, Price? price) => !model.IsLocal && price is not { Free: true };

    /// <summary>The budget period containing <paramref name="now"/>: from the last <paramref name="resetDay"/> to the next.</summary>
    internal static (DateTime Start, DateTime End) Period(DateTime now, int resetDay)
    {
        var start = new DateTime(now.Year, now.Month, 1).AddDays(resetDay - 1);
        if (now < start) start = start.AddMonths(-1);
        return (start, start.AddMonths(1));
    }

    /// <summary>The key of a period in the roll-up: the day it starts, "yyyy-MM-dd".</summary>
    private static string PeriodKey(DateTime now, int resetDay) =>
        Period(now, resetDay).Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- recording

    // A call is recorded by its reservation (Reserve) and corrected by its settlement (Settle), both in Reservations.cs:
    // there is no second way to charge a call.

    internal static (double Cost, string Source) CostOf(ModelInfo model, Usage u, JsonObject? agentCfg)
    {
        if (u.CostUsd is { } reported && double.IsFinite(reported)) return (Math.Max(0, reported), "reported");
        var price = PriceOf(model, agentCfg);
        if (price is null) return (0, "unknown");
        if (price.Free) return (0, model.IsLocal ? "free" : "estimated");
        var cost = (u.InputTokens * price.Input + u.OutputTokens * price.Output + u.CacheReadTokens * price.CacheRead + u.CacheWriteTokens * price.CacheWrite) / 1_000_000;
        return (cost, "estimated");
    }

    /// <summary>The top-level chat a session belongs to (subagents roll up to it).</summary>
    private string? RootSession(string? sessionId)
    {
        var id = sessionId;
        for (var i = 0; i < 16 && id is not null; i++)
        {
            var s = _ctx.Sessions.GetSession(id);
            if (s?.ParentSessionId is not { Length: > 0 } parent) return id;
            id = parent;
        }
        return id;
    }

    /// <summary>The two spend totals summed from the calls (each read is atomic): the period's and today's.</summary>
    private (double Period, double Today) ReadSpent(DateTime now)
    {
        // Today is always inside the current period, so the two sums need no join: the period starts on the reset
        // day of the current (or previous) month and today's calls all fall from then on.
        var (start, _) = Period(now, Options().ResetDay);
        var from = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var calls = _calls!;
        return (calls.Sum("costUsd", new DataQuery().Ge("ts", from)),
                calls.Sum("costUsd", new DataQuery().Eq("day", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
    }

    public (double Period, double Today) Spent()
    {
        if (_calls is null) return (0, 0);
        return ReadSpent(DateTime.Now);
    }

    /// <summary>What one lane (agent) has spent today: its cost roll-up, case-insensitive on the lane.</summary>
    public double SpentToday(string agent)
    {
        if (_laneUsage is null) return 0;
        return _laneUsage.Sum("costUsd", new DataQuery().Eq("day", Today).Eq("laneKey", agent.ToLowerInvariant()));
    }

    /// <summary>
    /// What every lane has spent today in one read: the agent list shows every pool at once, so a snapshot must not
    /// read once per pool.
    /// </summary>
    public IReadOnlyDictionary<string, double> SpentTodayByAgent()
    {
        var byAgent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_laneUsage is null) return byAgent;
        foreach (var d in _laneUsage.Find(new DataQuery().Eq("day", Today)))
        {
            if (d.Doc["lane"]?.GetValue<string>() is { } lane)
                byAgent[lane] = D(d.Doc["costUsd"]);
        }
        return byAgent;
    }

    private void ScheduleChanged()
    {
        if (Interlocked.Exchange(ref _changeScheduled, 1) == 1) return;
        _ = Task.Delay(ChangeDelayMs, _ctx.Stopping).ContinueWith(_ =>
        {
            Volatile.Write(ref _changeScheduled, 0);
            // the plugin stopped while the debounce was running: its store goes with the load context, so the
            // publish must not reach it (idea-sx4xxx)
            if (Volatile.Read(ref _stopped) == 1 || _ctx.Stopping.IsCancellationRequested) return;
            try { _ctx.Events.Publish("usage.changed", BudgetStatus()); }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "usage.changed publish failed"); }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The plugin is stopping: the debounced <c>usage.changed</c> that is still scheduled checks <see cref="_stopped"/> before
    /// it publishes, so it does not run after the unload. The collections stay: a call that started before a hot swap keeps
    /// the old middleware (and so this ledger) until it ends, and its settlement must still reach the store, which is the
    /// shared one and outlives the load context.
    /// </summary>
    public void Stop() => Volatile.Write(ref _stopped, 1);

    // ---------------------------------------------------------------- budget

    // The budget is checked where a call is reserved (Reserve, Reservations.cs): the "already spent" refusals and the
    // reservation that must still fit are one transaction there, and the refusal wording lives there once.

    /// <summary>An agent's own daily cap: <c>agents.&lt;id&gt;.budget.limitUsd</c>.</summary>
    internal static double? DailyCap(JsonObject? agentCfg) => Positive((agentCfg?["budget"] as JsonObject)?["limitUsd"]);

    private DateTime PeriodStart => Period(DateTime.Now, Options().ResetDay).Start;

    private bool AllowedNow(SessionInfo session) =>
        session.Meta?[AllowanceMetaKey] is JsonValue v && v.TryGetValue<string>(out var s) && s == PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>budget.allow: this chat may go over the budget until the period ends; subagents still stop.</summary>
    public void Allow(string sessionId)
    {
        // The period is computed OUTSIDE the session update: the update runs inside the session store's transaction,
        // and nothing under it may touch the ledger's storage transactions (which take the storage lock in their own
        // order); PeriodStart reads nothing of it (it is pure arithmetic on the settings), so there is no lock pair.
        var from = PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            s.Meta[AllowanceMetaKey] = from;
        });
    }

    /// <summary>$50, $12.40, $0.02, $0.0005.</summary>
    internal static string Usd(double v) => "$" + v.ToString(
        v >= 1 && Math.Abs(v - Math.Round(v)) < 0.005 ? "0" : v >= 1 || v == 0 ? "0.00" : "0.00##", CultureInfo.InvariantCulture);

    /// <summary>The budget as agents and the UI see it.</summary>
    public JsonObject BudgetStatus()
    {
        var o = Options();
        var (period, today) = Spent();
        var start = PeriodStart;
        var warn = o.MonthlyUsd is { } m && period >= m * o.WarnPercent / 100 || o.DailyUsd is { } d && today >= d * o.WarnPercent / 100;
        var exhausted = o.MonthlyUsd is { } m2 && period >= m2 || o.DailyUsd is { } d2 && today >= d2;
        var estimates = EstimateStatus();
        // Calls in flight count at their reservation (the gate above must see them), but a figure a person reads jumped up by
        // a whole reservation at every call and back at its settlement (idea-qbi2fv): the settled amounts are given apart.
        var reservedToday = _calls?.Sum("costUsd", new DataQuery().Eq("day", Today).Eq("costSource", "reserved")) ?? 0;
        return new JsonObject
        {
            ["settledUsd"] = Math.Round(Math.Max(0, period - estimates.Reserved), 6),
            ["todaySettledUsd"] = Math.Round(Math.Max(0, today - reservedToday), 6),
            ["reservedTodayUsd"] = Math.Round(reservedToday, 6),
            ["reservedOrUnsettledUsd"] = estimates.Reserved,
            ["interruptedEstimateUsd"] = estimates.Interrupted,
            ["interruptedEstimateCalls"] = estimates.InterruptedCalls,
            ["unknownCostCalls"] = estimates.Unknown,
            ["monthlyUsd"] = o.MonthlyUsd,
            ["dailyUsd"] = o.DailyUsd,
            ["warnPercent"] = o.WarnPercent,
            ["resetDay"] = o.ResetDay,
            ["onLimit"] = o.OnLimit,
            ["periodStart"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["periodEnd"] = start.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["spentUsd"] = Math.Round(period, 6),
            ["todayUsd"] = Math.Round(today, 6),
            ["warning"] = warn,
            ["exhausted"] = exhausted,
        };
    }

    /// <summary>One line for agents: <c>Budget: $12.40 of $50 this month (25 %), $1.10 today.</c></summary>
    public string BudgetLine()
    {
        var o = Options();
        var (period, today) = Spent();
        var parts = new List<string>();
        parts.Add(o.MonthlyUsd is { } m
            ? $"{Usd(period)} of {Usd(m)} this month ({Math.Round(period / m * 100).ToString("0", CultureInfo.InvariantCulture)} %)"
            : $"{Usd(period)} this month (no monthly limit)");
        parts.Add(o.DailyUsd is { } d ? $"{Usd(today)} of {Usd(d)} today" : $"{Usd(today)} today");
        var line = "Budget: " + string.Join(", ", parts) + ". Free models don't count.";
        var status = BudgetStatus();
        if (status["exhausted"]?.GetValue<bool>() == true) line += " The budget is spent: paid models are stopped.";
        else if (status["warning"]?.GetValue<bool>() == true) line += $" Over {o.WarnPercent} %: use paid agents only when the user asked.";
        return line;
    }

    /// <summary>What a chat cost: its own calls, and with its subagents; <c>reservedUsd</c> is the part of the latter still at a
    /// reservation (calls in flight), which the chat's cost shows apart (idea-qbi2fv).</summary>
    public JsonObject SessionCost(string sessionId)
    {
        double own = 0, all = 0, reserved = 0;
        long calls = 0, allCalls = 0;
        if (_sessionUsage is not null && _counters?.Get("session_usage") is not null)
        {
            // the roll-up: a read of two documents, and it outlives the call rows (idea-sdevr6). A top-level chat's own calls
            // are in its tree; a subagent's tree is its own calls (the calls are rolled up under the top-level chat).
            try
            {
                var mine = _sessionUsage.Get("s|" + sessionId);
                var tree = _sessionUsage.Get("r|" + sessionId);
                own = D(mine?["costUsd"]);
                calls = L(mine?["calls"]);
                all = tree is not null ? D(tree["costUsd"]) : own;
                allCalls = tree is not null ? L(tree["calls"]) : calls;
                reserved = tree is not null ? D(tree["reservedUsd"]) : D(mine?["reservedUsd"]);
            }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Session cost read failed"); }
        }
        else if (_calls is not null)
        {
            try
            {
                own = _calls.Sum("costUsd", new DataQuery().Eq("sessionId", sessionId));
                calls = _calls.Count(new DataQuery().Eq("sessionId", sessionId));
                // root_session_id = session_id on the session's own calls, so the "with subagents" total is the
                // union of the two roots: |root| + |own| − |both|.
                var root = _calls.Sum("costUsd", new DataQuery().Eq("rootSessionId", sessionId));
                var rootCalls = _calls.Count(new DataQuery().Eq("rootSessionId", sessionId));
                var overlap = new DataQuery().Eq("rootSessionId", sessionId).Eq("sessionId", sessionId);
                all = root + own - _calls.Sum("costUsd", overlap);
                allCalls = rootCalls + calls - _calls.Count(overlap);
                reserved = _calls.Sum("costUsd", new DataQuery().Eq("rootSessionId", sessionId).Eq("costSource", "reserved"))
                    + _calls.Sum("costUsd", new DataQuery().Eq("sessionId", sessionId).Eq("costSource", "reserved"))
                    - _calls.Sum("costUsd", new DataQuery().Eq("rootSessionId", sessionId).Eq("sessionId", sessionId).Eq("costSource", "reserved"));
            }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Session cost query failed"); }
        }
        return new JsonObject
        {
            ["sessionId"] = sessionId,
            ["costUsd"] = Math.Round(own, 6),
            ["calls"] = calls,
            ["withSubagentsUsd"] = Math.Round(all, 6),
            ["withSubagentsCalls"] = allCalls,
            ["reservedUsd"] = Math.Round(Math.Max(0, reserved), 6),
        };
    }

    /// <summary>This period per model: calls, tokens, cost (and whether some costs are unknown) — the roll-up the
    /// recorded calls maintain, so the read is one indexed find per period instead of the period's calls.</summary>
    private JsonArray ModelsThisPeriod()
    {
        var arr = new JsonArray();
        if (_periodUsage is null) return arr;
        try
        {
            var period = PeriodKey(DateTime.Now, Options().ResetDay);
            foreach (var d in _periodUsage.Find(new DataQuery().Eq("period", period))
                .OrderByDescending(x => D(x.Doc["costUsd"])).ThenByDescending(x => L(x.Doc["calls"])))
            {
                var doc = d.Doc;
                arr.Add(new JsonObject
                {
                    ["agent"] = doc["lane"]?.GetValue<string>(),
                    ["provider"] = doc["provider"]?.GetValue<string>(),
                    ["model"] = doc["model"]?.GetValue<string>(),
                    ["calls"] = L(doc["calls"]),
                    ["inputTokens"] = L(doc["inputTokens"]),
                    ["outputTokens"] = L(doc["outputTokens"]),
                    ["cacheReadTokens"] = L(doc["cacheReadTokens"]),
                    ["cacheWriteTokens"] = L(doc["cacheWriteTokens"]),
                    ["costUsd"] = Math.Round(D(doc["costUsd"]), 6),
                    ["unknownCost"] = L(doc["unknownCalls"]) > 0,
                });
            }
        }
        catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Usage per model query failed"); }
        return arr;
    }

    // ---------------------------------------------------------------- tokens per day (lanes_usage)

    /// <summary>
    /// The per-(day, provider, model) token roll-up: upserted with every recorded call. When called inside the
    /// caller's storage transaction (as Settle does) it joins it, so the roll-up commits with the call's row.
    /// </summary>
    public void Record(string provider, string model, long input, long output, long cacheRead, long cacheWrite)
    {
        if (_lanesUsage is null) return;
        _ctx.Data.Transaction(() => RecordTx(provider, model, input, output, cacheRead, cacheWrite));
    }

    private void RecordTx(string provider, string model, long input, long output, long cacheRead, long cacheWrite)
    {
        var day = Today;
        var key = day + "|" + provider + "|" + model;
        var doc = _lanesUsage!.Get(key);
        if (doc is null)
            doc = new JsonObject
            {
                ["day"] = day,
                ["provider"] = provider,
                ["model"] = model,
                ["inputTokens"] = 0L,
                ["outputTokens"] = 0L,
                ["cacheReadTokens"] = 0L,
                ["cacheWriteTokens"] = 0L,
                ["calls"] = 0L,
            };
        doc["inputTokens"] = L(doc["inputTokens"]) + input;
        doc["outputTokens"] = L(doc["outputTokens"]) + output;
        doc["cacheReadTokens"] = L(doc["cacheReadTokens"]) + cacheRead;
        doc["cacheWriteTokens"] = L(doc["cacheWriteTokens"]) + cacheWrite;
        doc["calls"] = L(doc["calls"]) + 1;
        _lanesUsage.Put(key, doc);
    }

    /// <summary><c>usage.summary</c>: today's tokens per provider, the budget, and this period's calls per model.</summary>
    public JsonObject Summary()
    {
        var day = Today;
        var byProvider = new SortedDictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        if (_lanesUsage is not null)
            foreach (var d in _lanesUsage.Find(new DataQuery().Eq("day", day)))
            {
                var p = d.Doc["provider"]?.GetValue<string>();
                if (p is null) continue;
                if (!byProvider.TryGetValue(p, out var agg)) byProvider[p] = agg = new Totals();
                agg.InputTokens += L(d.Doc["inputTokens"]); agg.OutputTokens += L(d.Doc["outputTokens"]);
                agg.CacheReadTokens += L(d.Doc["cacheReadTokens"]); agg.CacheWriteTokens += L(d.Doc["cacheWriteTokens"]);
                agg.Calls += L(d.Doc["calls"]);
            }

        var arr = new JsonArray();
        foreach (var (provider, t) in byProvider)
            arr.Add(new JsonObject
            {
                ["provider"] = provider,
                ["inputTokens"] = t.InputTokens,
                ["outputTokens"] = t.OutputTokens,
                ["cacheReadTokens"] = t.CacheReadTokens,
                ["cacheWriteTokens"] = t.CacheWriteTokens,
                ["calls"] = t.Calls,
            });
        return new JsonObject { ["day"] = day, ["providers"] = arr, ["budget"] = BudgetStatus(), ["models"] = ModelsThisPeriod() };
    }

    // ----------------------------------------------------------------

    // The roll-up documents store plain JSON numbers; read them with a zero default for a fresh document.
    private static double D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;

    // The roll-ups' USD precision (the same as the reports): round every accumulation so an estimate followed by its
    // settlement (estimate, then settled − estimate) lands exactly on the settled cost instead of drifting by a
    // fraction of an ulp per pair, which 100 concurrent writes turn into 100 ± 1e-14.
    private static double Usd6(double v) => Math.Round(v, 6);
}
