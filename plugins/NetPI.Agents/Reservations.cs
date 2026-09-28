using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Agents;

internal sealed partial class Ledger
{
    private sealed record Charge(long Ts, string Day, string? Agent, double Cost);
    private readonly Dictionary<long, Charge> _memoryCharges = [];
    private long _nextCharge;
    internal sealed record Reservation(long Id, double Cost, ModelRequest Request, string? Agent, JsonObject? Config);

    private long AddCharge(ModelRequest request, string? agent, Usage u, double cost, string source)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var day = Today;
        if (!_dbReady)
        {
            var id = ++_nextCharge;
            _memoryCharges[id] = new Charge(ts, day, agent, cost);
            return id;
        }
        // A failed write must fail the call, rather than silently disabling the budget.
        return _ctx.Db.Insert("""
            INSERT INTO usage_calls (ts, day, session_id, root_session_id, agent_id, lane, provider, model, purpose,
                input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd, cost_source)
            VALUES (@ts, @day, @session, @root, @agentId, @agent, @provider, @model, @purpose,
                @input, @output, @cacheRead, @cacheWrite, @cost, @source)
            """, new Dictionary<string, object?>
        {
            ["ts"] = ts, ["day"] = day, ["session"] = request.SessionId, ["root"] = RootSession(request.SessionId),
            ["agentId"] = request.AgentId, ["agent"] = agent, ["provider"] = request.Model.Provider,
            ["model"] = request.Model.Id, ["purpose"] = request.Purpose, ["input"] = u.InputTokens,
            ["output"] = u.OutputTokens, ["cacheRead"] = u.CacheReadTokens, ["cacheWrite"] = u.CacheWriteTokens,
            ["cost"] = cost, ["source"] = source,
        });
    }

    // Conservative estimate, not a provider billing guarantee. Reserve uncached input and the complete output limit.
    internal static double Estimate(ModelRequest request, Price? price)
    {
        if (price is null || price.Free) return 0;
        var bytes = Encoding.UTF8.GetByteCount(request.SystemPrompt ?? "")
            + (long)JsonSerializer.SerializeToUtf8Bytes(request.Messages, NetPiJson.Options).Length
            + JsonSerializer.SerializeToUtf8Bytes(request.Tools, NetPiJson.Options).Length + 2048;
        var input = request.Model.ContextWindow is > 0 and var window ? Math.Min(bytes, window) : bytes;
        var output = request.MaxOutputTokens is > 0 and var max ? max : request.Model.MaxOutputTokens ?? 16384;
        return (input * Math.Max(price.Input, price.CacheWrite) + output * price.Output) / 1_000_000;
    }

    private (double Reserved, double Interrupted, long Unknown) EstimateStatus()
    {
        if (!_dbReady) return (0, 0, 0);
        var from = new DateTimeOffset(PeriodStart).ToUnixTimeMilliseconds();
        return _ctx.Db.QuerySingle("""
            SELECT COALESCE(SUM(CASE WHEN cost_source='reserved' THEN cost_usd ELSE 0 END),0) AS reserved,
                   COALESCE(SUM(CASE WHEN cost_source='interrupted-estimate' THEN cost_usd ELSE 0 END),0) AS interrupted,
                   SUM(CASE WHEN cost_source='unknown' THEN 1 ELSE 0 END) AS unknown
              FROM usage_calls WHERE ts >= @from
            """, new { from }, r => (r.GetDouble("reserved"), r.GetDouble("interrupted"), r.GetInt64OrNull("unknown") ?? 0));
    }

    public Reservation Reserve(ModelRequest request, string? agent, JsonObject? cfg)
    {
        lock (_gate)
        {
            Reservation Begin()
            {
                Check(request, agent, cfg);
                var price = PriceOf(request.Model, cfg);
                var o = Options();
                var limited = o.MonthlyUsd.HasValue || o.DailyUsd.HasValue || DailyCap(cfg).HasValue;
                var paid = Paid(request.Model, price);
                if (paid && limited && !_dbReady)
                    throw new BudgetExceededException("The budget ledger is unavailable; paid calls are stopped until storage is repaired.");
                if (paid && request.MaxOutputTokens is not > 0)
                    request.MaxOutputTokens = request.Model.MaxOutputTokens is > 0 ? request.Model.MaxOutputTokens : 16384;
                var cost = paid ? Estimate(request, price) : 0;
                var session = request.SessionId is null ? null : _ctx.Sessions.GetSession(request.SessionId);
                var ask = o.OnLimit == "ask" && session is { Kind: not "subagent" } && request.Purpose == "agent";
                var allowed = ask && AllowedNow(session!);
                string? why = null;
                if (paid && limited && price is null)
                    why = "The budget cannot reserve this model's cost: configure both input and output prices in Agents before continuing.";
                else if (paid && limited)
                {
                    var (period, today) = Spent();
                    if (o.MonthlyUsd is { } m && period + cost > m || o.DailyUsd is { } d && today + cost > d
                        || agent is not null && DailyCap(cfg) is { } cap && SpentToday(agent) + cost > cap)
                        why = $"The budget cannot fit this call's {Usd(cost)} reservation including concurrent calls. Lower the output limit, raise the budget, or use a free model.";
                }
                if (why is not null && !allowed) throw new BudgetExceededException(why) { CanOverride = ask };
                // Persist before dispatch. A crash leaves a conservative reservation, not unaccounted spending.
                var id = AddCharge(request, agent, new Usage(), cost, price is null && paid ? "unknown" : "reserved");
                ScheduleChanged();
                return new Reservation(id, cost, request, agent, cfg);
            }
            // Also serializes with a prior plugin generation during hot reload.
            return _dbReady ? _ctx.Db.Transaction(_ => Begin()) : Begin();
        }
    }

    public void Settle(Reservation reservation, Usage? usage, bool completed, bool rejected)
    {
        lock (_gate)
        {
            var u = usage ?? new Usage();
            var (cost, source) = usage is not null ? CostOf(reservation.Request.Model, u, reservation.Config)
                : (rejected ? 0 : reservation.Cost, rejected ? "rejected" : "interrupted-estimate");
            if (!completed && usage is not null && (u.CostUsd is null || !double.IsFinite(u.CostUsd.Value)))
            {
                cost = Math.Max(cost, reservation.Cost);
                source = "interrupted-estimate";
            }
            if (!rejected && Paid(reservation.Request.Model, null) && PriceOf(reservation.Request.Model, reservation.Config) is null
                && (usage?.CostUsd is null || !double.IsFinite(usage.CostUsd.Value))) source = "unknown";
            if (_dbReady)
                _ctx.Db.Execute("""
                    UPDATE usage_calls SET cost_usd=@cost, cost_source=@source, input_tokens=@input,
                      output_tokens=@output, cache_read_tokens=@cacheRead, cache_write_tokens=@cacheWrite WHERE id=@id
                    """, new { id = reservation.Id, cost, source, input = u.InputTokens, output = u.OutputTokens,
                        cacheRead = u.CacheReadTokens, cacheWrite = u.CacheWriteTokens });
            else if (_memoryCharges.TryGetValue(reservation.Id, out var charge))
                _memoryCharges[reservation.Id] = charge with { Cost = cost };
            Record(reservation.Request.Model.Provider, reservation.Request.Model.Id,
                u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens);
        }
        ScheduleChanged();
    }
}

/// <summary>Inside retry: each provider attempt reserves and settles independently.</summary>
internal sealed class LedgerMiddleware(Ledger ledger, AgentScheduler scheduler) : IModelMiddleware
{
    public int Order => 100;

    public async IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (agent, cfg) = scheduler.AgentOf(request.Model, request.SessionId);
        var reservation = ledger.Reserve(request, agent, cfg);
        Usage? usage = null;
        var completed = false;
        var rejected = false;
        var started = false;
        try
        {
            await using var iterator = next(request, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool more;
                try { more = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (ModelException ex) when (ex.StatusCode is 400 or 401 or 403 or 404 or 429 || ex.ContextOverflow)
                {
                    rejected = !started && usage is null;
                    throw;
                }
                if (!more) break;
                var e = iterator.Current;
                if (e is TextDelta or ThinkingDelta or ToolCallStarted or ToolCallArgsDelta or UsageUpdate) started = true;
                if (e is UsageUpdate update) usage = update.Usage;
                if (e is StreamCompleted done)
                {
                    usage = done.Message.Usage ?? usage;
                    completed = true;
                    // Settle before consumers inspect budgets or start another call.
                    ledger.Settle(reservation, usage, true, false);
                }
                yield return e;
                if (completed) yield break;
            }
        }
        finally
        {
            if (!completed) ledger.Settle(reservation, usage, false, rejected);
        }
    }
}
