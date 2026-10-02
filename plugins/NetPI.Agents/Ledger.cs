using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

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
/// <see cref="BudgetExceededException"/>; <c>budget.onLimit</c> "ask" lets the user allow a chat to go over
/// (<c>session.meta.budgetAllowedFrom</c> = the start of the period).</item>
/// </list>
/// </summary>
internal sealed partial class Ledger : IBudgetGate
{
    public const double CacheReadShare = 0.1;   // of the input price, when no cache price is known (Anthropic's rate)
    public const double CacheWriteShare = 1.25;

    private readonly IPluginContext _ctx;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Day, string Provider, string Model), Totals> _totals = [];
    private bool _dbReady;

    // Monetary snapshots read atomically from the ledger, including persistent reservations
    private string _costDay = "";
    private DateTime _periodStart;
    private double _periodSpent, _todaySpent;
    private readonly Dictionary<string, double> _agentToday = new(StringComparer.OrdinalIgnoreCase);
    private int _changeScheduled;
    // In-memory charges (no database): _memorySeq counts every add/settle and _memorySeqRolled is its value when the
    // snapshot was last rescanned, so a roll skips the rescan while the day is unchanged and nothing was recorded.
    private long _memorySeq, _memorySeqRolled = -1;
    private int _memoryScans;   // test seam: how often the in-memory charge list was rescanned

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
        var db = _ctx.Db;
        if (db is null) return;
        try
        {
            db.Migrate("lanes",
                """
                CREATE TABLE IF NOT EXISTS lanes_usage (
                  day TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  model TEXT NOT NULL,
                  input_tokens INTEGER NOT NULL DEFAULT 0,
                  output_tokens INTEGER NOT NULL DEFAULT 0,
                  cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                  cache_write_tokens INTEGER NOT NULL DEFAULT 0,
                  calls INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY (day, provider, model)
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS usage_calls (
                  id INTEGER PRIMARY KEY,
                  ts INTEGER NOT NULL,
                  day TEXT NOT NULL,
                  session_id TEXT,
                  root_session_id TEXT,
                  agent_id TEXT,
                  lane TEXT,
                  provider TEXT NOT NULL,
                  model TEXT NOT NULL,
                  purpose TEXT NOT NULL,
                  input_tokens INTEGER NOT NULL DEFAULT 0,
                  output_tokens INTEGER NOT NULL DEFAULT 0,
                  cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                  cache_write_tokens INTEGER NOT NULL DEFAULT 0,
                  cost_usd REAL NOT NULL DEFAULT 0,
                  cost_source TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS usage_calls_ts ON usage_calls(ts);
                CREATE INDEX IF NOT EXISTS usage_calls_root ON usage_calls(root_session_id);
                """,
                "CREATE INDEX IF NOT EXISTS usage_calls_day_lane ON usage_calls(day, lane)");
            var day = Today;
            var rows = db.Query("SELECT provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls FROM lanes_usage WHERE day = @day",
                new Dictionary<string, object?> { ["day"] = day },
                r => (Provider: r.GetString("provider"), Model: r.GetString("model"), T: new Totals
                {
                    InputTokens = r.GetInt64("input_tokens"),
                    OutputTokens = r.GetInt64("output_tokens"),
                    CacheReadTokens = r.GetInt64("cache_read_tokens"),
                    CacheWriteTokens = r.GetInt64("cache_write_tokens"),
                    Calls = r.GetInt64("calls"),
                }));
            lock (_gate)
                foreach (var (provider, model, t) in rows) _totals[(day, provider, model)] = t;
            _dbReady = true;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Usage tables unavailable; usage is tracked in memory only");
        }
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

    // ---------------------------------------------------------------- recording

    /// <summary>A finished call: its cost row, the cost caches, the daily token totals, and <c>usage.changed</c>.</summary>
    public void RecordCall(ModelRequest request, ChatMessage message, string? agent, JsonObject? agentCfg)
    {
        var u = message.Usage;
        if (u is null) return;
        var model = request.Model;
        var (cost, source) = CostOf(model, u, agentCfg);
        lock (_gate)
        {
            AddCharge(request, agent, u, cost, source);
            Record(message.Provider ?? model.Provider, model.Id, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens);
        }
        ScheduleChanged();
    }

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

    /// <summary>Refresh the monetary snapshot under the same lock as recording and reservation.</summary>
    private void Roll(DateTime now)
    {
        lock (_gate)
        {
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dayChanged = day != _costDay;
            _costDay = day;
            (_periodStart, _) = Period(now, Options().ResetDay);
            var from = new DateTimeOffset(_periodStart).ToUnixTimeMilliseconds();
            if (_dbReady)
            {
                // Read through the database: another ledger may still be settling a call after hot reload.
                // Never publish a stale snapshot over increments made by a different thread.
                _agentToday.Clear();
                var totals = _ctx.Db.Query("""
                    SELECT lane,
                        SUM(CASE WHEN ts >= @from THEN cost_usd ELSE 0 END) AS period,
                        SUM(CASE WHEN day = @day THEN cost_usd ELSE 0 END) AS today
                    FROM usage_calls WHERE ts >= @from OR day = @day GROUP BY lane
                    """, new { from, day = _costDay },
                    r => (Agent: r.GetStringOrNull("lane"), Period: r.GetDouble("period"), Today: r.GetDouble("today")));
                _periodSpent = totals.Sum(t => t.Period);
                _todaySpent = totals.Sum(t => t.Today);
                foreach (var t in totals.Where(t => t.Agent is not null)) _agentToday[t.Agent!] = t.Today;
            }
            else if (!dayChanged && _memorySeqRolled == _memorySeq)
            {
                // The in-memory snapshot is current: the same day (hence the same period) and nothing was
                // recorded or settled since the last roll, so the charge list needs no rescanning.
                return;
            }
            else
            {
                _memoryScans++;
                _agentToday.Clear();
                _periodSpent = _memoryCharges.Values.Where(c => c.Ts >= from).Sum(c => c.Cost);
                var today = _memoryCharges.Values.Where(c => c.Day == _costDay).ToList();
                _todaySpent = today.Sum(c => c.Cost);
                foreach (var c in today.Where(c => c.Agent is not null))
                    _agentToday[c.Agent!] = _agentToday.GetValueOrDefault(c.Agent!) + c.Cost;
                _memorySeqRolled = _memorySeq;
            }
        }
    }

    /// <summary>Test seam: how many in-memory charges are kept and how often their list was rescanned.</summary>
    internal (int Charges, int Scans) MemoryStats
    {
        get { lock (_gate) return (_memoryCharges.Count, _memoryScans); }
    }

    public (double Period, double Today) Spent()
    {
        lock (_gate) { Roll(DateTime.Now); return (_periodSpent, _todaySpent); }
    }

    public double SpentToday(string agent)
    {
        lock (_gate) { Roll(DateTime.Now); return _agentToday.GetValueOrDefault(agent); }
    }

    /// <summary>
    /// What every agent has spent today in one roll: the agent list shows every pool at once, so a snapshot must not
    /// roll (a database read) once per pool.
    /// </summary>
    public IReadOnlyDictionary<string, double> SpentTodayByAgent()
    {
        lock (_gate)
        {
            Roll(DateTime.Now);
            return new Dictionary<string, double>(_agentToday, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ScheduleChanged()
    {
        if (Interlocked.Exchange(ref _changeScheduled, 1) == 1) return;
        _ = Task.Delay(ChangeDelayMs).ContinueWith(_ =>
        {
            Volatile.Write(ref _changeScheduled, 0);
            try { _ctx.Events.Publish("usage.changed", BudgetStatus()); }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "usage.changed publish failed"); }
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- budget

    /// <summary>
    /// Before a call to a paid model: throws when the month's or today's budget or the agent's daily cap is spent. With
    /// budget.onLimit "ask" the user's own chats may be allowed to go over (<see cref="Allow"/>).
    /// </summary>
    public void Check(ModelRequest request, string? agent, JsonObject? agentCfg)
    {
        var model = request.Model;
        if (!Paid(model, PriceOf(model, agentCfg))) return;
        var o = Options();
        var (period, today) = Spent();
        string? why = null;
        if (o.MonthlyUsd is { } month && period >= month)
            why = $"The monthly budget is spent: {Usd(period)} of {Usd(month)} since {PeriodStart.ToString("d MMM", CultureInfo.InvariantCulture)}.";
        else if (o.DailyUsd is { } day && today >= day)
            why = $"Today's budget is spent: {Usd(today)} of {Usd(day)}.";
        else if (agent is not null && DailyCap(agentCfg) is { } cap && SpentToday(agent) is var spent && spent >= cap)
            why = $"The agent {agent} has spent its {Usd(cap)} for today ({Usd(spent)}).";
        if (why is null) return;

        var session = request.SessionId is null ? null : _ctx.Sessions.GetSession(request.SessionId);
        var ask = o.OnLimit == "ask" && session is { Kind: not "subagent" } && request.Purpose == "agent";
        if (ask && AllowedNow(session!)) return;
        throw new BudgetExceededException(why + (ask
            ? " You can let this chat go over, or switch it to a free model."
            : " Raise the budget in Settings → Budget (budget.*), or use a free agent.")) { CanOverride = ask };
    }

    /// <summary>An agent's own daily cap: <c>agents.&lt;id&gt;.budget.limitUsd</c>.</summary>
    internal static double? DailyCap(JsonObject? agentCfg) => Positive((agentCfg?["budget"] as JsonObject)?["limitUsd"]);

    private DateTime PeriodStart
    {
        get
        {
            Roll(DateTime.Now);
            lock (_gate) return _periodStart;
        }
    }

    private bool AllowedNow(SessionInfo session) =>
        session.Meta?["budgetAllowedFrom"] is JsonValue v && v.TryGetValue<string>(out var s) && s == PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>budget.allow: this chat may go over the budget until the period ends; subagents still stop.</summary>
    public void Allow(string sessionId)
    {
        // The period is computed OUTSIDE the session update: the update runs inside a database transaction, and
        // nothing under the database gate may take this gate — Reserve takes them in the opposite order (ledger
        // first, then the database), so doing it in the update wedged the two gates across threads.
        var from = PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            s.Meta["budgetAllowedFrom"] = from;
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
        if (_dbReady)
        {
            var args = new Dictionary<string, object?> { ["sid"] = sessionId };
            try
            {
                own = _ctx.Db.Scalar<double?>("SELECT SUM(cost_usd) FROM usage_calls WHERE session_id = @sid", args) ?? 0;
                calls = _ctx.Db.Scalar<long?>("SELECT COUNT(*) FROM usage_calls WHERE session_id = @sid", args) ?? 0;
                all = _ctx.Db.Scalar<double?>("SELECT SUM(cost_usd) FROM usage_calls WHERE root_session_id = @sid OR session_id = @sid", args) ?? 0;
                allCalls = _ctx.Db.Scalar<long?>("SELECT COUNT(*) FROM usage_calls WHERE root_session_id = @sid OR session_id = @sid", args) ?? 0;
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

    /// <summary>This period per model: calls, tokens, cost (and whether some costs are unknown).</summary>
    private JsonArray ModelsThisPeriod()
    {
        var arr = new JsonArray();
        if (!_dbReady) return arr;
        try
        {
            var from = new DateTimeOffset(PeriodStart).ToUnixTimeMilliseconds();
            foreach (var row in _ctx.Db.Query(
                         """
                         SELECT lane, provider, model, COUNT(*) AS calls, SUM(input_tokens) AS i, SUM(output_tokens) AS o,
                           SUM(cache_read_tokens) AS cr, SUM(cache_write_tokens) AS cw, SUM(cost_usd) AS c,
                           SUM(CASE WHEN cost_source = 'unknown' THEN 1 ELSE 0 END) AS unknown
                         FROM usage_calls WHERE ts >= @from GROUP BY lane, provider, model ORDER BY c DESC, calls DESC
                         """,
                         new Dictionary<string, object?> { ["from"] = from },
                         r => new JsonObject
                         {
                             ["agent"] = r.GetStringOrNull("lane"),
                             ["provider"] = r.GetString("provider"),
                             ["model"] = r.GetString("model"),
                             ["calls"] = r.GetInt64("calls"),
                             ["inputTokens"] = r.GetInt64("i"),
                             ["outputTokens"] = r.GetInt64("o"),
                             ["cacheReadTokens"] = r.GetInt64("cr"),
                             ["cacheWriteTokens"] = r.GetInt64("cw"),
                             ["costUsd"] = Math.Round(r.GetDouble("c"), 6),
                             ["unknownCost"] = r.GetInt64("unknown") > 0,
                         }))
                arr.Add(row);
        }
        catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Usage per model query failed"); }
        return arr;
    }

    // ---------------------------------------------------------------- tokens per day (lanes_usage) and the legacy token budget

    public void Record(string provider, string model, long input, long output, long cacheRead, long cacheWrite)
    {
        var day = Today;
        lock (_gate)
        {
            if (!_totals.TryGetValue((day, provider, model), out var t)) _totals[(day, provider, model)] = t = new Totals();
            t.InputTokens += input; t.OutputTokens += output; t.CacheReadTokens += cacheRead; t.CacheWriteTokens += cacheWrite; t.Calls++;
            // forget previous days
            foreach (var k in _totals.Keys.Where(k => k.Day != day).ToList()) _totals.Remove(k);
        }
        if (!_dbReady) return;
        try
        {
            _ctx.Db.Execute(
                """
                INSERT INTO lanes_usage (day, provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls)
                VALUES (@day, @provider, @model, @input, @output, @cacheRead, @cacheWrite, 1)
                ON CONFLICT(day, provider, model) DO UPDATE SET
                  input_tokens = input_tokens + excluded.input_tokens,
                  output_tokens = output_tokens + excluded.output_tokens,
                  cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens,
                  cache_write_tokens = cache_write_tokens + excluded.cache_write_tokens,
                  calls = calls + 1
                """,
                new Dictionary<string, object?>
                {
                    ["day"] = day, ["provider"] = provider, ["model"] = model,
                    ["input"] = input, ["output"] = output, ["cacheRead"] = cacheRead, ["cacheWrite"] = cacheWrite,
                });
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Failed to persist usage");
        }
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
        var day = Today;
        lock (_gate)
            return _totals.Where(kv => kv.Key.Day == day && string.Equals(kv.Key.Provider, provider, StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value.BudgetTokens);
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
        lock (_gate)
        {
            foreach (var (key, t) in _totals)
            {
                if (key.Day != day) continue;
                if (!byProvider.TryGetValue(key.Provider, out var agg)) byProvider[key.Provider] = agg = new Totals();
                agg.InputTokens += t.InputTokens; agg.OutputTokens += t.OutputTokens;
                agg.CacheReadTokens += t.CacheReadTokens; agg.CacheWriteTokens += t.CacheWriteTokens; agg.Calls += t.Calls;
            }
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
}
