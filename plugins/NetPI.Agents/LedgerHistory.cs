using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Agents;

// The usage tab's reads (usage.history, usage.chats). History comes from the roll-ups the recorded calls maintain
// (period_usage per period, lane and model; the day indexes of usage_calls and lanes_usage), so it costs a few indexed
// reads however many calls there are; only the per-chat ranking has to read the period's calls, and it is its own call.
internal sealed partial class Ledger
{
    /// <summary>Periods listed, newest first: three years of months.</summary>
    private const int MaxPeriods = 36;
    /// <summary>The most calls the per-chat ranking reads: a ranking over a longer period says so (<c>truncated</c>).</summary>
    private const int MaxRankedCalls = 200_000;

    /// <summary>Calls, tokens and cost of a group of roll-up documents.</summary>
    private sealed class Sum
    {
        public long Calls, Input, Output, CacheRead, CacheWrite, Unknown;
        public double Cost;
        public string? Name;

        public void Add(JsonObject d)
        {
            Calls += L(d["calls"]);
            Input += L(d["inputTokens"]);
            Output += L(d["outputTokens"]);
            CacheRead += L(d["cacheReadTokens"]);
            CacheWrite += L(d["cacheWriteTokens"]);
            Unknown += L(d["unknownCalls"]);
            Cost += D(d["costUsd"]);
        }

        public JsonObject ToJson(JsonObject? into = null)
        {
            var o = into ?? new JsonObject();
            o["calls"] = Calls;
            o["inputTokens"] = Input;
            o["outputTokens"] = Output;
            o["cacheReadTokens"] = CacheRead;
            o["cacheWriteTokens"] = CacheWrite;
            o["costUsd"] = Math.Round(Cost, 6);
            o["unknownCalls"] = Unknown;
            return o;
        }
    }

    /// <summary>
    /// <c>usage.history</c>: every budget period with its totals (newest first), the chosen period (default: the current one)
    /// per agent and per model, the last <paramref name="days"/> days, and the budget. A period that has no usage is not listed.
    /// </summary>
    public JsonObject History(string? period, int days)
    {
        var options = Options();
        var current = PeriodKey(DateTime.Now, options.ResetDay);
        var byPeriod = new SortedDictionary<string, Sum>(StringComparer.Ordinal);
        var chosen = new List<JsonObject>();
        if (_periodUsage is not null)
        {
            foreach (var d in _periodUsage.Find())
            {
                var key = d.Doc["period"]?.GetValue<string>();
                if (key is null) continue;
                if (!byPeriod.TryGetValue(key, out var sum)) byPeriod[key] = sum = new Sum();
                sum.Add(d.Doc);
            }
        }
        if (period is null) period = current;
        else if (!byPeriod.ContainsKey(period) && period != current) throw new RpcException("not_found", $"No usage in the period starting {period}.");
        if (_periodUsage is not null) chosen = [.. _periodUsage.Find(new DataQuery().Eq("period", period)).Select(d => d.Doc)];

        var periods = new JsonArray();
        var all = new Sum();
        foreach (var (key, sum) in byPeriod.Reverse())
        {
            all.Calls += sum.Calls; all.Input += sum.Input; all.Output += sum.Output; all.CacheRead += sum.CacheRead;
            all.CacheWrite += sum.CacheWrite; all.Unknown += sum.Unknown; all.Cost += sum.Cost;
            if (periods.Count == MaxPeriods) continue;
            var o = sum.ToJson();
            o["period"] = key;
            o["end"] = PeriodEnd(key);
            periods.Add(o);
        }
        if (!byPeriod.ContainsKey(current)) // this period has no call yet: it is still the one the budget is about
            periods.Insert(0, new JsonObject { ["period"] = current, ["end"] = PeriodEnd(current), ["calls"] = 0L, ["inputTokens"] = 0L, ["outputTokens"] = 0L, ["cacheReadTokens"] = 0L, ["cacheWriteTokens"] = 0L, ["costUsd"] = 0.0, ["unknownCalls"] = 0L });

        var total = new Sum();
        var agents = new Dictionary<string, Sum>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, Sum>(StringComparer.Ordinal);
        var modelNames = new Dictionary<string, (string? Provider, string? Model)>(StringComparer.Ordinal);
        foreach (var doc in chosen)
        {
            total.Add(doc);
            var lane = doc["lane"]?.GetValue<string>() ?? "";
            if (!agents.TryGetValue(lane, out var a)) agents[lane] = a = new Sum { Name = lane.Length > 0 ? lane : null };
            a.Add(doc);
            var provider = doc["provider"]?.GetValue<string>();
            var model = doc["model"]?.GetValue<string>();
            var mk = provider + "|" + model;
            if (!models.TryGetValue(mk, out var m)) { models[mk] = m = new Sum(); modelNames[mk] = (provider, model); }
            m.Add(doc);
        }

        JsonArray Ranked(IEnumerable<(Sum Sum, Action<JsonObject> Label)> rows) => new([.. rows
            .OrderByDescending(r => r.Sum.Cost).ThenByDescending(r => r.Sum.Input + r.Sum.Output).ThenByDescending(r => r.Sum.Calls)
            .Select(r => { var o = r.Sum.ToJson(); r.Label(o); return (JsonNode)o; })]);

        var result = new JsonObject
        {
            ["period"] = period,
            ["current"] = current,
            ["end"] = PeriodEnd(period),
            ["resetDay"] = options.ResetDay,
            ["totals"] = total.ToJson(),
            ["allTime"] = all.ToJson(new JsonObject { ["since"] = byPeriod.Count > 0 ? byPeriod.Keys.First() : null }),
            ["periods"] = periods,
            ["agents"] = Ranked(agents.Values.Select(s => (s, (Action<JsonObject>)(o => o["agent"] = s.Name)))),
            ["models"] = Ranked(models.Select(kv => (kv.Value, (Action<JsonObject>)(o => { o["provider"] = modelNames[kv.Key].Provider; o["model"] = modelNames[kv.Key].Model; })))),
            ["days"] = Days(Math.Clamp(days, 1, 366)),
            ["budget"] = BudgetStatus(),
        };
        return result;
    }

    /// <summary>The day a period ends (it starts on <paramref name="key"/>): "yyyy-MM-dd".</summary>
    private static string PeriodEnd(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : key;

    /// <summary>The last <paramref name="count"/> days, oldest first, a day with no calls as zeros: cost and calls from the day index of the calls, tokens from the daily roll-up.</summary>
    private JsonArray Days(int count)
    {
        var arr = new JsonArray();
        if (_calls is null) return arr;
        var today = DateTime.Now.Date;
        var first = today.AddDays(1 - count).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var tokens = new Dictionary<string, Sum>(StringComparer.Ordinal);
        if (_lanesUsage is not null)
            foreach (var d in _lanesUsage.Find(new DataQuery().Ge("day", first)))
            {
                var day = d.Doc["day"]?.GetValue<string>();
                if (day is null) continue;
                if (!tokens.TryGetValue(day, out var t)) tokens[day] = t = new Sum();
                t.Add(d.Doc);
            }
        for (var i = count - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var q = new DataQuery().Eq("day", day);
            var cost = _calls.Sum("costUsd", q);
            var calls = _calls.Count(new DataQuery().Eq("day", day));
            var t = tokens.GetValueOrDefault(day);
            arr.Add(new JsonObject
            {
                ["day"] = day,
                ["costUsd"] = Math.Round(cost, 6),
                ["calls"] = calls,
                ["inputTokens"] = t?.Input ?? 0,
                ["outputTokens"] = t?.Output ?? 0,
                ["cacheReadTokens"] = t?.CacheRead ?? 0,
                ["cacheWriteTokens"] = t?.CacheWrite ?? 0,
            });
        }
        return arr;
    }

    /// <summary>
    /// <c>usage.chats</c>: the chats that cost most in a period (a subagent's calls count for the chat that started it): cost, calls and
    /// tokens, the chat's title and project. The one read that goes through the period's calls, so it is asked for on its own.
    /// </summary>
    public JsonObject Chats(string? period, int limit)
    {
        var key = period ?? PeriodKey(DateTime.Now, Options().ResetDay);
        if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            throw new RpcException("bad_request", "period is the day the budget period starts: yyyy-MM-dd.");
        var from = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(start.AddMonths(1)).ToUnixTimeMilliseconds();
        var perChat = new Dictionary<string, Sum>(StringComparer.Ordinal);
        var rest = new Sum();
        var total = new Sum();
        var truncated = false;
        if (_calls is not null)
        {
            var rows = _calls.Find(new DataQuery().Ge("ts", from).Lt("ts", to).Take(MaxRankedCalls + 1));
            truncated = rows.Count > MaxRankedCalls;
            foreach (var d in rows.Take(MaxRankedCalls))
            {
                var doc = d.Doc;
                var one = new JsonObject
                {
                    ["calls"] = 1L, ["inputTokens"] = doc["inputTokens"]?.DeepClone(), ["outputTokens"] = doc["outputTokens"]?.DeepClone(),
                    ["cacheReadTokens"] = doc["cacheReadTokens"]?.DeepClone(), ["cacheWriteTokens"] = doc["cacheWriteTokens"]?.DeepClone(),
                    ["costUsd"] = doc["costUsd"]?.DeepClone(),
                };
                total.Add(one);
                var chat = doc["rootSessionId"]?.GetValue<string>() ?? doc["sessionId"]?.GetValue<string>();
                if (chat is null) { rest.Add(one); continue; }
                if (!perChat.TryGetValue(chat, out var sum)) perChat[chat] = sum = new Sum { Name = chat };
                sum.Add(one);
            }
        }
        var chats = new JsonArray();
        foreach (var sum in perChat.Values.OrderByDescending(s => s.Cost).ThenByDescending(s => s.Input + s.Output).Take(Math.Clamp(limit, 1, 50)))
        {
            var session = _ctx.Sessions.GetSession(sum.Name!);
            var o = sum.ToJson();
            o["sessionId"] = sum.Name;
            o["title"] = session?.Title;
            o["deleted"] = session is null;
            o["project"] = session?.ProjectId is { } pid ? _ctx.Sessions.GetProject(pid)?.Name : null;
            chats.Add(o);
        }
        return new JsonObject
        {
            ["period"] = key,
            ["chats"] = chats,
            ["chatCount"] = perChat.Count,
            ["noChat"] = rest.ToJson(),
            ["totals"] = total.ToJson(),
            ["truncated"] = truncated,
        };
    }
}
