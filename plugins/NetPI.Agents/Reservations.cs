using System.Runtime.CompilerServices;
using System.Text;
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
            _memorySeq++;
            // Keep only what the next roll needs (the current period and today, like Record does for _totals):
            // without a database the list would otherwise grow for the life of the process and be rescanned
            // on every budget roll.
            PruneMemoryCharges();
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

    /// <summary>Forget in-memory charges that are neither in the current period nor today (under the ledger gate).</summary>
    private void PruneMemoryCharges()
    {
        var day = Today;
        var from = new DateTimeOffset(Period(DateTime.Now, Options().ResetDay).Start).ToUnixTimeMilliseconds();
        foreach (var k in _memoryCharges.Where(c => c.Value.Ts < from && c.Value.Day != day).Select(c => c.Key).ToList())
            _memoryCharges.Remove(k);
    }

    /// <summary>Test seam (memory mode): inject a legacy in-memory charge as if it had been made at a given moment and
    /// had survived until now (no pruning — that is what a new record does).</summary>
    internal void BackdateCharge(long ts, string day, string? agent, double cost)
    {
        lock (_gate)
        {
            _memoryCharges[++_nextCharge] = new Charge(ts, day, agent, cost);
            _memorySeq++;
        }
    }

    // A gate, not a bill: what the call may cost at worst, so concurrent reservations fit against the budgets.
    // The input is counted in tokens (a conversation serialised to JSON is 3-5x its token count, and base64 images
    // would count at full size), priced at the cache-read rate — in an agent loop the context is re-read, not
    // re-sent, and a call that misses the cache is corrected by the settlement, while a 3-5x over-reservation
    // locks a tight budget for nothing. The output is the effective maximum the provider would accept.
    internal static double Estimate(ModelRequest request, Price? price)
    {
        if (price is null || price.Free) return 0;
        var input = EstimateInput(request);
        var output = request.MaxOutputTokens is > 0 and var max ? max : request.Model.MaxOutputTokens ?? 16384;
        if (request.Model.MaxOutputTokens is > 0 and var cap && output > cap) output = cap;   // the provider clamps to its own maximum
        return (input * price.CacheRead + output * price.Output) / 1_000_000;
    }

    /// <summary>Approximate input context in tokens: the conversation, the system prompt and the tool definitions (images at 4000).</summary>
    internal static long EstimateInput(ModelRequest request)
    {
        var input = ModelMessages.EstimateTokens(request.Messages) + ModelMessages.EstimateTokens(request.SystemPrompt);
        if (request.Tools is { Count: > 0 } tools)
        {
            var chars = 0;
            foreach (var t in tools)
            {
                chars += t.Name.Length + t.Description.Length + (t.Help?.Length ?? 0);
                if (t.Parameters is not null) chars += t.Parameters.ToJsonString(NetPiJson.Options).Length;
            }
            input += chars / 4;
        }
        return input;
    }

    private (double Reserved, double Interrupted, long InterruptedCalls, long Unknown) EstimateStatus()
    {
        if (!_dbReady) return (0, 0, 0, 0);
        var from = new DateTimeOffset(PeriodStart).ToUnixTimeMilliseconds();
        return _ctx.Db.QuerySingle("""
            SELECT COALESCE(SUM(CASE WHEN cost_source='reserved' THEN cost_usd ELSE 0 END),0) AS reserved,
                   COALESCE(SUM(CASE WHEN cost_source='interrupted-estimate' THEN cost_usd ELSE 0 END),0) AS interrupted,
                   COALESCE(SUM(CASE WHEN cost_source='interrupted-estimate' THEN 1 ELSE 0 END),0) AS interruptedCalls,
                   SUM(CASE WHEN cost_source='unknown' THEN 1 ELSE 0 END) AS unknown
              FROM usage_calls WHERE ts >= @from
            """, new { from }, r => (r.GetDouble("reserved"), r.GetDouble("interrupted"), r.GetInt64("interruptedCalls"), r.GetInt64OrNull("unknown") ?? 0));
    }

    public Reservation Reserve(ModelRequest request, string? agent, JsonObject? cfg)
    {
        // Priced outside the gate: the estimate must not be computed while the ledger gate or a database transaction
        // is held (it is pure, and a long conversation used to be serialised here on every paid attempt).
        var price = PriceOf(request.Model, cfg);
        var paid = Paid(request.Model, price);
        if (paid && request.MaxOutputTokens is not > 0)
            request.MaxOutputTokens = request.Model.MaxOutputTokens is > 0 ? request.Model.MaxOutputTokens : 16384;
        var cost = paid ? Estimate(request, price) : 0;
        lock (_gate)
        {
            Reservation Begin()
            {
                Check(request, agent, cfg);
                var o = Options();
                var limited = o.MonthlyUsd.HasValue || o.DailyUsd.HasValue || DailyCap(cfg).HasValue;
                if (paid && limited && !_dbReady)
                    throw new BudgetExceededException("The budget ledger is unavailable; paid calls are stopped until storage is repaired.");
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
            double cost;
            string source;
            if (rejected)
            {
                // Nothing reached the consumer before the failure or stop: nothing was billed.
                (cost, source) = (0, "rejected");
            }
            else
            {
                // The partial work, priced at what was actually used — never the reservation (a stop after
                // message_start used to charge the full output limit; a 503 after a few minutes, the worst case).
                (cost, source) = CostOf(reservation.Request.Model, u, reservation.Config);
                if (!completed && u.CostUsd is not { } && source == "estimated") source = "interrupted-estimate";
            }
            if (_dbReady)
                _ctx.Db.Execute("""
                    UPDATE usage_calls SET cost_usd=@cost, cost_source=@source, input_tokens=@input,
                      output_tokens=@output, cache_read_tokens=@cacheRead, cache_write_tokens=@cacheWrite WHERE id=@id
                    """, new { id = reservation.Id, cost, source, input = u.InputTokens, output = u.OutputTokens,
                    cacheRead = u.CacheReadTokens, cacheWrite = u.CacheWriteTokens });
            else if (_memoryCharges.TryGetValue(reservation.Id, out var charge))
            {
                _memoryCharges[reservation.Id] = charge with { Cost = cost };
                _memorySeq++; // the cost changed: the next roll must rescan
            }
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
        // A hot swap briefly runs two generations of this middleware on one call (the old one is removed only after
        // the new one serves): the first one to run reserves, the other sees the mark and passes through, so the call
        // is reserved and settled exactly once instead of twice.
        var state = request.PipelineState ??= new JsonObject();
        if (state["$ledger"] is not null)
        {
            await foreach (var e in next(request, ct).ConfigureAwait(false)) yield return e;
            yield break;
        }
        var (agent, cfg) = scheduler.AgentOf(request.Model, request.SessionId);
        var reservation = ledger.Reserve(request, agent, cfg);
        state["$ledger"] = reservation.Id;
        Usage? usage = null;
        var completed = false;
        var started = false;
        var streamed = new StringBuilder();
        try
        {
            await using var iterator = next(request, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                // A failure propagates to the retry middleware (or the caller); the finally settles what the
                // stream did not report.
                bool more = await iterator.MoveNextAsync().ConfigureAwait(false);
                if (!more) break;
                var e = iterator.Current;
                if (e is TextDelta or ThinkingDelta or ToolCallStarted or ToolCallArgsDelta or UsageUpdate) started = true;
                if (e is TextDelta t) streamed.Append(t.Text);
                else if (e is ThinkingDelta th) streamed.Append(th.Text);
                else if (e is ToolCallArgsDelta a) streamed.Append(a.Delta);
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
            if (!completed)
            {
                if (usage is null && started)
                    // The stream died before reporting usage: price what was sent and what had streamed, not the
                    // reservation (a stop after a few seconds no longer charges the full output limit).
                    usage = new Usage { InputTokens = Ledger.EstimateInput(request), OutputTokens = ModelMessages.EstimateTokens(streamed.ToString()) };
                // !started: nothing arrived before the failure or stop — rejected, $0 (a 503/529 before the first
                // byte, a connect failure, or a cancel that made it out the door).
                ledger.Settle(reservation, usage, false, !started);
            }
            // The attempt is over: a retry re-runs the pipeline with the same request, and each attempt reserves
            // and settles of its own.
            state.Remove("$ledger");
        }
    }
}
