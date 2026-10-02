using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Agents;

internal sealed partial class Ledger
{
    internal sealed record Reservation(long Id, double Cost, ModelRequest Request, string? Agent, JsonObject? Config);

    /// <summary>
    /// Record one call (a reservation, or a final charge) with a fresh id, and add its cost to the lane's day.
    /// Runs inside the caller's storage transaction, so the id, the call and the roll-ups commit together; a failed
    /// write fails the call, rather than silently disabling the budget.
    /// </summary>
    private long AddCharge(ModelRequest request, string? agent, string? root, Usage u, double cost, string source)
    {
        var now = DateTime.Now;
        var ts = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var period = PeriodKey(now, Options().ResetDay);
        var doc = new JsonObject
        {
            ["ts"] = ts,
            ["day"] = day,
            ["period"] = period,
            ["provider"] = request.Model.Provider,
            ["model"] = request.Model.Id,
            ["purpose"] = request.Purpose,
            ["inputTokens"] = u.InputTokens,
            ["outputTokens"] = u.OutputTokens,
            ["cacheReadTokens"] = u.CacheReadTokens,
            ["cacheWriteTokens"] = u.CacheWriteTokens,
            ["costUsd"] = cost,
            ["costSource"] = source,
        };
        if (request.SessionId is not null) doc["sessionId"] = request.SessionId;
        if (root is not null) doc["rootSessionId"] = root;
        if (request.AgentId is not null) doc["agentId"] = request.AgentId;
        if (agent is not null) doc["lane"] = agent;
        var id = NextChargeId();
        if (!_calls!.Insert(id.ToString(CultureInfo.InvariantCulture), doc))
            throw new StorageException("The usage call id " + id + " is already taken");
        // The lane's day counts the call at its recorded cost (a reservation at its estimate); Settle replaces the
        // estimate with the settled cost. The period's roll-up does the same, per model.
        if (agent is not null)
        {
            var laneKey = agent.ToLowerInvariant();
            var key = day + "|" + laneKey;
            var laneDoc = _laneUsage!.Get(key);
            if (laneDoc is null)
                laneDoc = new JsonObject { ["day"] = day, ["lane"] = agent, ["laneKey"] = laneKey };
            laneDoc["costUsd"] = D(laneDoc["costUsd"]) + cost;
            _laneUsage.Put(key, laneDoc);
        }
        PeriodUsageAdd(period, agent, request.Model.Provider, request.Model.Id, 1,
            u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, cost, source == "unknown" ? 1 : 0);
        return id;
    }

    /// <summary>
    /// The period's per-model roll-up, upserted in the caller's storage transaction: a call adds its recorded cost
    /// and tokens, a settlement corrects the reservation's estimate to the settled cost, so the read is the document,
    /// never a scan of the period's calls. Deltas may be negative (a settlement that prices below the reservation).
    /// </summary>
    private void PeriodUsageAdd(string period, string? agent, string provider, string model, int calls,
        long input, long output, long cacheRead, long cacheWrite, double cost, int unknown)
    {
        var key = period + "|" + (agent?.ToLowerInvariant() ?? "") + "|" + provider + "|" + model;
        var doc = _periodUsage!.Get(key);
        if (doc is null)
            doc = new JsonObject
            {
                ["period"] = period,
                ["provider"] = provider,
                ["model"] = model,
                ["calls"] = 0L,
                ["inputTokens"] = 0L,
                ["outputTokens"] = 0L,
                ["cacheReadTokens"] = 0L,
                ["cacheWriteTokens"] = 0L,
                ["costUsd"] = 0.0,
                ["unknownCalls"] = 0L,
            };
        if (agent is not null) doc["lane"] = agent;
        doc["calls"] = L(doc["calls"]) + calls;
        doc["inputTokens"] = L(doc["inputTokens"]) + input;
        doc["outputTokens"] = L(doc["outputTokens"]) + output;
        doc["cacheReadTokens"] = L(doc["cacheReadTokens"]) + cacheRead;
        doc["cacheWriteTokens"] = L(doc["cacheWriteTokens"]) + cacheWrite;
        doc["costUsd"] = D(doc["costUsd"]) + cost;
        doc["unknownCalls"] = L(doc["unknownCalls"]) + unknown;
        _periodUsage.Put(key, doc);
    }

    /// <summary>
    /// Rebuild the current period's roll-up from the calls when it has not caught up: the store pre-dates the roll-up
    /// (an upgrade), or a changed budget.resetDay moved the period's border. One transaction, so no call's own write
    /// interleaves: the check and the rebuild are the read of the roll-up the next period starts from.
    /// </summary>
    private void CatchUpPeriodUsage()
    {
        var now = DateTime.Now;
        var (start, _) = Period(now, Options().ResetDay);
        var period = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var calls = _calls!.Count(new DataQuery().Ge("ts", from));
        if (_periodUsage!.Sum("calls", new DataQuery().Eq("period", period)) == calls) return;

        static void Add(JsonObject g, JsonObject call, double cost, long unknown)
        {
            g["calls"] = L(g["calls"]) + 1;
            g["inputTokens"] = L(g["inputTokens"]) + L(call["inputTokens"]);
            g["outputTokens"] = L(g["outputTokens"]) + L(call["outputTokens"]);
            g["cacheReadTokens"] = L(g["cacheReadTokens"]) + L(call["cacheReadTokens"]);
            g["cacheWriteTokens"] = L(g["cacheWriteTokens"]) + L(call["cacheWriteTokens"]);
            g["costUsd"] = D(g["costUsd"]) + cost;
            g["unknownCalls"] = L(g["unknownCalls"]) + unknown;
        }

        var groups = new Dictionary<(string? Lane, string? Provider, string? Model), JsonObject>();
        foreach (var d in _calls.Find(new DataQuery().Ge("ts", from)))
        {
            var call = d.Doc;
            var key = (Lane: call["lane"]?.GetValue<string>(), Provider: call["provider"]?.GetValue<string>(), Model: call["model"]?.GetValue<string>());
            if (!groups.TryGetValue(key, out var g))
            {
                g = new JsonObject
                {
                    ["period"] = period,
                    ["provider"] = key.Provider,
                    ["model"] = key.Model,
                    ["calls"] = 0L,
                    ["inputTokens"] = 0L,
                    ["outputTokens"] = 0L,
                    ["cacheReadTokens"] = 0L,
                    ["cacheWriteTokens"] = 0L,
                    ["costUsd"] = 0.0,
                    ["unknownCalls"] = 0L,
                };
                if (key.Lane is not null) g["lane"] = key.Lane;
                groups[key] = g;
            }
            Add(g, call, D(call["costUsd"]), call["costSource"]?.GetValue<string>() == "unknown" ? 1L : 0L);
        }
        _periodUsage.DeleteWhere(new DataQuery().Eq("period", period));
        foreach (var (k, g) in groups)
            _periodUsage.Put(period + "|" + (k.Lane?.ToLowerInvariant() ?? "") + "|" + k.Provider + "|" + k.Model, g);
    }

    /// <summary>The next call id: a stored sequence, so ids never repeat across hot reloads.</summary>
    private long NextChargeId()
    {
        var doc = _counters!.Get("usage_calls");
        var next = L(doc?["value"]) + 1;
        _counters.Put("usage_calls", new JsonObject { ["value"] = next });
        return next;
    }

    // A gate, not a bill: what the call may cost at worst, so concurrent reservations fit against the budgets.
    // The input is counted in tokens (a conversation serialised to JSON is 3-5x its token count, and base64 images
    // would count at full size), priced at the cache-read rate — in an agent loop the context is re-read, not
    // re-sent, and a call that misses the cache is corrected by the settlement, while a 3-5x over-reservation
    // locks a tight budget for nothing. The output is the effective maximum the provider would accept: the same
    // clamp the transports send (the model's own maximum and the room the window leaves), so what is reserved is what
    // the call can spend (idea-begg3v). A model with no limit of its own takes agent.defaultMaxOutputTokens.
    internal static double Estimate(ModelRequest request, Price? price, ISettings? settings)
    {
        if (price is null || price.Free) return 0;
        var input = EstimateInput(request);
        var output = ModelMessages.ClampMaxTokens(request, request.MaxOutputTokens is > 0 and var max ? max : OutputLimit.Model(request.Model, settings));
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
        if (_calls is null) return (0, 0, 0, 0);
        var from = new DateTimeOffset(PeriodStart).ToUnixTimeMilliseconds();
        return (_calls.Sum("costUsd", new DataQuery().Ge("ts", from).Eq("costSource", "reserved")),
                _calls.Sum("costUsd", new DataQuery().Ge("ts", from).Eq("costSource", "interrupted-estimate")),
                _calls.Count(new DataQuery().Ge("ts", from).Eq("costSource", "interrupted-estimate")),
                _calls.Count(new DataQuery().Ge("ts", from).Eq("costSource", "unknown")));
    }

    public Reservation Reserve(ModelRequest request, string? agent, JsonObject? cfg)
    {
        // Priced outside the transaction: the estimate must not be computed while the store transaction is held
        // (it is pure, and a long conversation used to be serialised here on every paid attempt).
        var price = PriceOf(request.Model, cfg);
        var paid = Paid(request.Model, price);
        if (paid && request.MaxOutputTokens is not > 0)
            request.MaxOutputTokens = OutputLimit.Model(request.Model, _ctx.Settings);
        var cost = paid ? Estimate(request, price, _ctx.Settings) : 0;
        var o = Options();
        var limited = o.MonthlyUsd.HasValue || o.DailyUsd.HasValue || DailyCap(cfg).HasValue;
        var root = RootSession(request.SessionId);
        // The session and the allowance are read outside the transaction: nothing may call into the session store
        // from inside a plugin transaction. The allowance only changes through budget.allow, which holds nothing here.
        var session = request.SessionId is null ? null : _ctx.Sessions.GetSession(request.SessionId);
        var ask = o.OnLimit == "ask" && session is { Kind: not "subagent" } && request.Purpose == "agent";
        var allowed = ask && AllowedNow(session!);
        if (_calls is null)
        {
            // Fail closed, as when the ledger was unavailable: the limits cannot be checked for a paid call, and a
            // free one still runs (unrecorded — there is no in-memory ledger to keep it in).
            if (paid && limited)
                throw new CallRefusedException("The budget ledger is unavailable; paid calls are stopped until storage is repaired.") { Kind = "budget" };
            return new Reservation(0, cost, request, agent, cfg);
        }

        return _ctx.Data.Transaction(() =>
        {
            var now = DateTime.Now;
            var (period, today) = ReadSpent(now);
            var spentToday = agent is null ? 0.0 : SpentToday(agent);
            string? why = null;
            if (paid)
            {
                // The "already spent" refusals first (Check's wording), then this call's reservation against the rest.
                if (o.MonthlyUsd is { } month && period >= month)
                    why = $"The monthly budget is spent: {Usd(period)} of {Usd(month)} since {Period(now, o.ResetDay).Start.ToString("d MMM", CultureInfo.InvariantCulture)}.";
                else if (o.DailyUsd is { } day && today >= day)
                    why = $"Today's budget is spent: {Usd(today)} of {Usd(day)}.";
                else if (agent is not null && DailyCap(cfg) is { } cap && spentToday >= cap)
                    why = $"The agent {agent} has spent its {Usd(cap)} for today ({Usd(spentToday)}).";
            }
            if (why is not null && !allowed)
                throw new CallRefusedException(why + (ask
                    ? " You can let this chat go over, or switch it to a free model."
                    : " Raise the budget in Settings → Budget (budget.*), or use a free agent.")) { Kind = "budget", CanOverride = ask };
            string? fit = null;
            if (paid && limited)
            {
                if (price is null)
                    fit = "The budget cannot reserve this model's cost: configure both input and output prices in Agents before continuing.";
                else if (o.MonthlyUsd is { } m && period + cost > m || o.DailyUsd is { } d && today + cost > d
                    || agent is not null && DailyCap(cfg) is { } cap && spentToday + cost > cap)
                    fit = $"The budget cannot fit this call's {Usd(cost)} reservation including concurrent calls. Lower the output limit, raise the budget, or use a free model.";
            }
            if (fit is not null && !allowed) throw new CallRefusedException(fit) { Kind = "budget", CanOverride = ask };
            // Persist before dispatch. A crash leaves a conservative reservation, not unaccounted spending.
            var id = AddCharge(request, agent, root, new Usage(), cost, price is null && paid ? "unknown" : "reserved");
            ScheduleChanged();
            return new Reservation(id, cost, request, agent, cfg);
        });
    }

    public void Settle(Reservation reservation, Usage? usage, bool completed, bool rejected)
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
        if (_calls is null) return;   // nothing was recorded while the store was unavailable: nothing to settle
        var model = reservation.Request.Model;
        var key = reservation.Id.ToString(CultureInfo.InvariantCulture);
        _ctx.Data.Transaction(() =>
        {
            var doc = _calls!.Get(key);
            if (doc is not null)
            {
                var oldCost = D(doc["costUsd"]);
                var oldSource = doc["costSource"]?.GetValue<string>() ?? "";
                var oldInput = L(doc["inputTokens"]);
                var oldOutput = L(doc["outputTokens"]);
                var oldCacheRead = L(doc["cacheReadTokens"]);
                var oldCacheWrite = L(doc["cacheWriteTokens"]);
                doc["costUsd"] = cost;
                doc["costSource"] = source;
                doc["inputTokens"] = u.InputTokens;
                doc["outputTokens"] = u.OutputTokens;
                doc["cacheReadTokens"] = u.CacheReadTokens;
                doc["cacheWriteTokens"] = u.CacheWriteTokens;
                _calls.Put(key, doc);
                // The lane's day carried the reservation's estimate; replace it with the settled cost, so the pool
                // counts the call once, at what it actually cost.
                if (reservation.Agent is { } lane && doc["day"]?.GetValue<string>() is { } day)
                {
                    var laneKey = lane.ToLowerInvariant();
                    var laneDoc = _laneUsage!.Get(day + "|" + laneKey);
                    if (laneDoc is not null)
                    {
                        laneDoc["costUsd"] = D(laneDoc["costUsd"]) + (cost - oldCost);
                        _laneUsage.Put(day + "|" + laneKey, laneDoc);
                    }
                }
                // The period's roll-up carries it the same way: the corrected cost and the tokens the reservation
                // did not record. A call recorded before the roll-up existed (no period of its own) is caught up
                // from its row on the next start.
                if (doc["period"]?.GetValue<string>() is { } period)
                    PeriodUsageAdd(period, reservation.Agent, model.Provider, model.Id, 0,
                        u.InputTokens - oldInput, u.OutputTokens - oldOutput,
                        u.CacheReadTokens - oldCacheRead, u.CacheWriteTokens - oldCacheWrite,
                        cost - oldCost, (source == "unknown" ? 1 : 0) - (oldSource == "unknown" ? 1 : 0));
            }
            RecordTx(model.Provider, model.Id, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens);
        });
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
