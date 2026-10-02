using System.Globalization;
using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// The collections this migration creates (plugin netpi.agents) — the shape the Agents plugin's Ledger reads and writes,
// kept here for the one-off store migration:
//   usage_calls — one document per model call: reserved when the call starts, settled in place when it ends.
//     key:   the call id, a decimal string allocated from the "usage_calls" counter in "counters" (never reused;
//            the old ids are migrated, and the counter is set to the old maximum)
//     doc:   { ts, day, sessionId?, rootSessionId?, agentId?, lane?, provider, model, purpose,
//               inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, costUsd, costSource }
//             (the old usage_calls columns one-to-one; costSource: "reserved", "reported", "estimated", "free",
//              "unknown", "rejected", "interrupted-estimate")
//     index: ts Integer, day Text, sessionId Text, rootSessionId Text, lane Text, costUsd Real, costSource Text
//
//   lanes_usage — tokens per (day, provider, model), upserted with every recorded call.
//     key:   <day>|<provider>|<model>
//     doc:   { day, provider, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, calls,
//              budgetTokens }            (budgetTokens = inputTokens + outputTokens + cacheWriteTokens)
//     index: day Text, provider Text, model Text, budgetTokens Integer
//
//   lane_usage — the cost per (day, lane): built here as the roll-up of the converted calls (in the product it is
//     upserted as calls are recorded).
//     key:   <day>|<laneKey>             (laneKey = the lane lower-cased; a call with no lane has no document)
//     doc:   { day, lane, laneKey, costUsd }
//     index: day Text, laneKey Text, costUsd Real
//
//   counters — the plugin's sequences.
//     key:   the counter name ("usage_calls")
//     doc:   { value }
//     index: none
internal static class AgentsMigration
{
    public const string PluginId = "netpi.agents";

    public static CollectionSpec CallsSpec() => new CollectionSpec()
        .Integer("ts").Text("day").Text("sessionId").Text("rootSessionId").Text("lane")
        .Real("costUsd").Text("costSource");
    public static CollectionSpec LanesSpec() => new CollectionSpec()
        .Text("day").Text("provider").Text("model").Integer("budgetTokens");
    public static CollectionSpec LaneSpec() => new CollectionSpec()
        .Text("day").Text("laneKey").Real("costUsd");
    public static CollectionSpec CountersSpec() => new();

    /// <summary>Every converted call, id order — the input of the lane_usage roll-up.</summary>
    public record Call(long Id, long Ts, string Day, string? SessionId, string? RootSessionId, string? AgentId, string? Lane,
        string Provider, string Model, string Purpose, long In, long Out, long CacheRead, long CacheWrite, double Cost, string CostSource);

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        var data = storage.Plugins.For(PluginId);
        var callsCol = data.Collection("usage_calls", CallsSpec());
        var lanesCol = data.Collection("lanes_usage", LanesSpec());
        var laneCol = data.Collection("lane_usage", LaneSpec());
        var countersCol = data.Collection("counters", CountersSpec());

        var calls = ReadCalls(old, notes);
        var lanes = ReadLanes(old, notes);
        // The roll-up is built in the calls' id order (the order the recording wrote them), the way Settle would have
        // grown the documents: same groupings, same totals.
        var byLane = new Dictionary<(string Day, string Key), (string Lane, double Cost)>();
        foreach (var c in calls)
        {
            if (c.Lane is null) continue;   // a call with no lane has no document
            var key = c.Lane.ToLowerInvariant();
            if (byLane.TryGetValue((c.Day, key), out var sum))
                byLane[(c.Day, key)] = (sum.Lane, sum.Cost + c.Cost);
            else
                byLane[(c.Day, key)] = (c.Lane, c.Cost);
        }

        data.Transaction(() =>
        {
            foreach (var c in calls)
            {
                var doc = new JsonObject
                {
                    ["ts"] = c.Ts,
                    ["day"] = c.Day,
                    ["provider"] = c.Provider,
                    ["model"] = c.Model,
                    ["purpose"] = c.Purpose,
                    ["inputTokens"] = c.In,
                    ["outputTokens"] = c.Out,
                    ["cacheReadTokens"] = c.CacheRead,
                    ["cacheWriteTokens"] = c.CacheWrite,
                    ["costUsd"] = c.Cost,
                    ["costSource"] = c.CostSource,
                };
                if (c.SessionId is not null) doc["sessionId"] = c.SessionId;
                if (c.RootSessionId is not null) doc["rootSessionId"] = c.RootSessionId;
                if (c.AgentId is not null) doc["agentId"] = c.AgentId;
                if (c.Lane is not null) doc["lane"] = c.Lane;
                if (!callsCol.Insert(c.Id.ToString(CultureInfo.InvariantCulture), doc))
                    notes.Add($"usage_calls id {c.Id}: the document already exists; not replaced");
            }
            foreach (var (day, provider, model, inT, outT, crT, cwT, callsCount) in lanes)
            {
                var doc = new JsonObject
                {
                    ["day"] = day,
                    ["provider"] = provider,
                    ["model"] = model,
                    ["inputTokens"] = inT,
                    ["outputTokens"] = outT,
                    ["cacheReadTokens"] = crT,
                    ["cacheWriteTokens"] = cwT,
                    ["calls"] = callsCount,
                };
                doc["budgetTokens"] = inT + outT + cwT;
                lanesCol.Put(day + "|" + provider + "|" + model, doc);
            }
            foreach (var ((day, laneKey), (lane, cost)) in byLane)
                laneCol.Put(day + "|" + laneKey, new JsonObject
                {
                    ["day"] = day,
                    ["lane"] = lane,
                    ["laneKey"] = laneKey,
                    ["costUsd"] = cost,
                });
            if (calls.Count > 0)
                countersCol.Put("usage_calls", new JsonObject { ["value"] = calls.Max(c => c.Id) });
        });
    }

    private static List<Call> ReadCalls(Database old, List<string> notes)
    {
        try
        {
            return old.Query("""
                SELECT id, ts, day, session_id, root_session_id, agent_id, lane, provider, model, purpose,
                    input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd, cost_source
                FROM old.usage_calls ORDER BY id
                """, null, r => new Call(
                    r.GetInt64("id"), r.GetInt64("ts"), r.GetString("day"), r.GetStringOrNull("session_id"),
                    r.GetStringOrNull("root_session_id"), r.GetStringOrNull("agent_id"), r.GetStringOrNull("lane"),
                    r.GetString("provider"), r.GetString("model"), r.GetString("purpose"),
                    r.GetInt64("input_tokens"), r.GetInt64("output_tokens"), r.GetInt64("cache_read_tokens"),
                    r.GetInt64("cache_write_tokens"), r.GetDouble("cost_usd"), r.GetString("cost_source"))).ToList();
        }
        catch (SqliteException)
        {
            notes.Add("old database has no usage_calls table: nothing to migrate for it");
            return [];
        }
    }

    private static List<(string, string, string, long, long, long, long, long)> ReadLanes(Database old, List<string> notes)
    {
        try
        {
            return old.Query("""
                SELECT day, provider, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, calls
                FROM old.lanes_usage
                """, null, r => (
                    r.GetString("day"), r.GetString("provider"), r.GetString("model"), r.GetInt64("input_tokens"),
                    r.GetInt64("output_tokens"), r.GetInt64("cache_read_tokens"), r.GetInt64("cache_write_tokens"),
                    r.GetInt64("calls"))).ToList();
        }
        catch (SqliteException)
        {
            notes.Add("old database has no lanes_usage table: nothing to migrate for it");
            return [];
        }
    }
}
