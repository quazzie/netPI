using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Daily token usage per provider/model (from <c>usage.recorded</c>), persisted in the <c>lanes_usage</c> table, and
/// optional daily budgets: <c>lanes.budgets.&lt;provider&gt;.dailyTokens</c>. Budget accounting counts input + output +
/// cache-write tokens; cache reads are excluded (they are re-sent every turn and billed at a fraction).
/// </summary>
internal sealed class UsageTracker
{
    private readonly IPluginContext _ctx;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Day, string Provider, string Model), Totals> _totals = [];
    private bool _dbReady;

    public sealed class Totals
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long Calls;
        public long BudgetTokens => InputTokens + OutputTokens + CacheWriteTokens;
    }

    public UsageTracker(IPluginContext ctx)
    {
        _ctx = ctx;
    }

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
                """);
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
            _ctx.Logger.LogWarning(ex, "Lane usage table unavailable; usage is tracked in memory only");
        }
    }

    /// <summary>Handle a <c>usage.recorded</c> event: <c>{ provider, model, usage: { inputTokens, ... } }</c>.</summary>
    public void Record(BusEvent evt)
    {
        try
        {
            var data = evt.Data is JsonElement je ? je : NetPiJson.ToElement(evt.Data);
            if (data.ValueKind != JsonValueKind.Object) return;
            var provider = Str(data, "provider");
            var model = Str(data, "model") ?? "";
            if (string.IsNullOrEmpty(provider)) return;
            if (!data.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
            // "model" may be a full ref; keep the id part
            if (model.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase)) model = model[(provider.Length + 1)..];
            Record(provider, model, Num(u, "inputTokens"), Num(u, "outputTokens"), Num(u, "cacheReadTokens"), Num(u, "cacheWriteTokens"));
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogDebug(ex, "Bad usage.recorded payload");
        }
    }

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
            _ctx.Logger.LogWarning(ex, "Failed to persist lane usage");
        }
    }

    public long? Budget(string provider)
    {
        try
        {
            var node = _ctx.Settings.GetNode("lanes.budgets") as JsonObject;
            if (node is null) return null;
            foreach (var (key, cfg) in node)
            {
                if (!string.Equals(key, provider, StringComparison.OrdinalIgnoreCase)) continue;
                var v = cfg is JsonObject o ? o["dailyTokens"] : cfg;
                if (v is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out var l)) return l > 0 ? l : null;
                    if (jv.TryGetValue<double>(out var d)) return d > 0 ? (long)d : null;
                    if (jv.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l > 0 ? l : null;
                }
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
                  $"Raise lanes.budgets.{provider}.dailyTokens in the settings, pick a model from another provider, or wait until tomorrow.";
        return true;
    }

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
            if (_ctx.Settings.GetNode("lanes.budgets") is JsonObject budgets)
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
        return new JsonObject { ["day"] = day, ["providers"] = arr };
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
