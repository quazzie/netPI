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
    /// Runs inside the caller's storage transaction, so the id, the call and the roll-up commit together; a failed
    /// write fails the call, rather than silently disabling the budget.
    /// </summary>
    private long AddCharge(ModelRequest request, string? agent, string? root, Usage u, double cost, string source)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var day = Today;
        var doc = new JsonObject
        {
            ["ts"] = ts,
            ["day"] = day,
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
        // estimate with the settled cost.
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
        return id;
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
