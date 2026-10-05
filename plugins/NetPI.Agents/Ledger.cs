using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

// The ledger's storage (ctx.Data, plugin "netpi.agents"). Document shapes, kept here for the one-off store migration:
//
// usage_calls — one document per model call: reserved when the call starts, settled in place when it ends.
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
//   doc:   { day, provider, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, calls,
//            budgetTokens }            (budgetTokens = inputTokens + outputTokens + cacheWriteTokens)
//   index: day Text, provider Text, model Text, budgetTokens Integer
//
// lane_usage — the cost per (day, lane): the lane's day gets the call's cost when it is recorded (a reservation at
//   its estimate), and Settle replaces the estimate with the settled cost.
//   key:   <day>|<laneKey>             (laneKey = the lane lower-cased; a call with no lane has no document)
//   doc:   { day, lane, laneKey, costUsd }
//   index: day Text, laneKey Text, costUsd Real
//
// counters — the plugin's sequences.
//   key:   the counter name ("usage_calls")
//   doc:   { value }
//   index: none

/// <summary>
/// Every model call with its tokens and cost, and the budgets.
/// <list type="bullet">
/// <item><c>usage_calls</c>: one row per call (agents, compaction, anything that asks a model), recorded by
/// <see cref="LedgerMiddleware"/>. The cost is what the provider reported (<see cref="Usage.CostUsd"/>: OpenRouter),
/// else tokens × the price (the agent's <c>cost</c>, else the catalog's pricing); local models are free; other cloud
/// models without a price are "unknown" and cannot start under a dollar cap without an explicit override.</item>
/// <item><c>lanes_usage</c>: tokens per day, provider and model (the Work tab, the legacy
/// <c>budget.providers.&lt;provider&gt;.dailyTokens</c>).</item>
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
    private IDataCollection? _counters;     // per-plugin sequences

    public sealed class Totals
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long Calls;
        public long BudgetTokens => InputTokens + OutputTokens + CacheWriteTokens;
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
            // All four or none: a half-opened ledger would let a free call through to a null collection and fail it.
            var calls = _ctx.Data.Collection("usage_calls", new CollectionSpec()
                .Integer("ts").Text("day").Text("sessionId").Text("rootSessionId").Text("lane")
                .Real("costUsd").Text("costSource"));
            var lanesUsage = _ctx.Data.Collection("lanes_usage", new CollectionSpec()
                .Text("day").Text("provider").Text("model").Integer("budgetTokens"));
            var laneUsage = _ctx.Data.Collection("lane_usage", new CollectionSpec()
                .Text("day").Text("laneKey").Real("costUsd"));
            var periodUsage = _ctx.Data.Collection("period_usage", new CollectionSpec()
                .Text("period").Integer("calls"));
            var counters = _ctx.Data.Collection("counters", new CollectionSpec());
            _calls = calls;
            _lanesUsage = lanesUsage;
            _laneUsage = laneUsage;
            _periodUsage = periodUsage;
            _counters = counters;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Usage storage unavailable; paid calls are stopped until it is repaired");
        }
        // The roll-up is maintained in every call's transaction; a period it has not caught up on (a store that
        // pre-dates it, or a changed budget.resetDay) is rebuilt from the calls once, in one transaction.
        RebuildPeriodUsage();
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
        return new JsonObject
        {
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

    /// <summary>What a chat cost: its own calls, and with its subagents.</summary>
    public JsonObject SessionCost(string sessionId)
    {
        double own = 0, all = 0;
        long calls = 0, allCalls = 0;
        if (_calls is not null)
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

    // ---------------------------------------------------------------- tokens per day (lanes_usage) and the legacy token budget

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
        // What the legacy per-provider token budgets count.
        doc["budgetTokens"] = L(doc["inputTokens"]) + L(doc["outputTokens"]) + L(doc["cacheWriteTokens"]);
        _lanesUsage.Put(key, doc);
    }

    public long? Budget(string provider)
    {
        try
        {
            var node = _ctx.Settings.GetNode("budget.providers") as JsonObject;
            if (node is null) return null;
            foreach (var (key, cfg) in node)
            {
                if (!string.Equals(key, provider, StringComparison.OrdinalIgnoreCase)) continue;
                var v = cfg is JsonObject o ? o["dailyTokens"] : cfg;
                if (Number(v) is > 0 and var n) return (long)n;
            }
        }
        catch { /* ignore bad settings */ }
        return null;
    }

    public long UsedToday(string provider)
    {
        if (_lanesUsage is null) return 0;
        var used = 0L;
        foreach (var d in _lanesUsage.Find(new DataQuery().Eq("day", Today)))
            if (string.Equals(d.Doc["provider"]?.GetValue<string>(), provider, StringComparison.OrdinalIgnoreCase))
                used += L(d.Doc["budgetTokens"]);
        return used;
    }

    public bool IsOverBudget(string? provider, out string? message)
    {
        message = null;
        if (string.IsNullOrEmpty(provider)) return false;
        if (Budget(provider) is not { } budget) return false;
        var used = UsedToday(provider);
        if (used < budget) return false;
        message = $"The daily token budget for provider '{provider}' is exhausted ({used:N0} of {budget:N0} tokens used today). " +
                  $"Raise budget.providers.{provider}.dailyTokens in the settings, pick a model from another provider, or wait until tomorrow.";
        return true;
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
        // providers with a budget but no usage yet
        try
        {
            if (_ctx.Settings.GetNode("budget.providers") is JsonObject budgets)
                foreach (var (key, _) in budgets)
                    if (!byProvider.ContainsKey(key)) byProvider[key] = new Totals();
        }
        catch { }

        var arr = new JsonArray();
        foreach (var (provider, t) in byProvider)
        {
            var o = new JsonObject
            {
                ["provider"] = provider,
                ["inputTokens"] = t.InputTokens,
                ["outputTokens"] = t.OutputTokens,
                ["cacheReadTokens"] = t.CacheReadTokens,
                ["cacheWriteTokens"] = t.CacheWriteTokens,
                ["calls"] = t.Calls,
            };
            if (Budget(provider) is { } b)
            {
                o["budgetTokens"] = b;
                o["budgetUsed"] = t.BudgetTokens;
            }
            arr.Add(o);
        }
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
