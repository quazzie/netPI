using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// The owner's Claude subscription usage (the 5-hour session window and the weekly windows), as the Claude CLI itself
/// reports it: every <c>claude -p</c> run prints <c>rate_limit_event</c> lines with the utilization of each window.
/// This reads nothing but the CLI's own output: no credentials file, no token, no call to Anthropic. The reading is
/// as fresh as the last call (the pill says when it was taken), and the last one is kept across restarts.
/// </summary>
public sealed class ClaudeUsageTracker(Action<JsonObject>? persist = null, ILogger? logger = null)
{
    private readonly Lock _gate = new();
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private JsonObject? _latest;
    private string? _plan;

    /// <summary>Fires when a reading changed (the plugin publishes it on the bus as <c>usage.claude</c>).</summary>
    public Action<JsonObject>? OnChanged { get; set; }

    /// <summary>The subscription plan (<c>max</c>, <c>pro</c>…), when known: the CLI's <c>auth status</c> names it.</summary>
    public void SetPlan(string? plan)
    {
        lock (_gate) _plan = string.IsNullOrWhiteSpace(plan) ? null : plan;
    }

    /// <summary>The last stored reading (a restart), shown until a call brings a fresher one.</summary>
    public void Restore(JsonObject? stored)
    {
        if (stored is null) return;
        lock (_gate) _latest ??= stored;
    }

    /// <summary>The snapshot: <c>{ plan, limits:[{key,label,percent,resetsAt,active,severity}], fetchedAt, stale }</c>; no limits before any call.</summary>
    public JsonObject Get()
    {
        lock (_gate)
        {
            var snap = (_latest ?? new JsonObject { ["limits"] = new JsonArray() }).DeepClone().AsObject();
            if (_plan is not null) snap["plan"] = _plan;
            // a reading older than a window's worth of hours says nothing about now
            if (DateTimeOffset.TryParse(snap.Str("fetchedAt"), out var at) && DateTimeOffset.UtcNow - at > TimeSpan.FromHours(6)) snap["stale"] = true;
            return snap;
        }
    }

    /// <summary>One <c>rate_limit_info</c> object of a <c>rate_limit_event</c> line.</summary>
    internal void Observe(JsonObject info)
    {
        var snap = Normalize(info);
        if (snap is null) return;
        lock (_gate) _latest = snap;
        try { persist?.Invoke(snap); }
        catch (Exception ex) { _log.LogDebug(ex, "claude-cli: could not store the usage reading"); }
        OnChanged?.Invoke(Get());
    }

    /// <summary>
    /// <c>{ status, resetsAt, rateLimitType, unifiedWindows: { five_hour: { utilization, resetsAt }, seven_day: {…}, seven_day_opus: {…} } }</c>
    /// to the snapshot the UI renders. <c>utilization</c> is a fraction of the window (0.24 = 24 %). Tolerant: a window it
    /// does not recognise is left out, and an event with no windows yields no reading.
    /// </summary>
    internal static JsonObject? Normalize(JsonObject info)
    {
        if (info.Obj("unifiedWindows") is not { } windows) return null;
        var status = info.Str("status");
        var severity = status switch { "rejected" => "limit", "allowed_warning" => "warning", _ => "normal" };
        var limits = new JsonArray();
        foreach (var (name, node) in windows)
        {
            if (node is not JsonObject w) continue;
            var (key, label) = name switch
            {
                "five_hour" => ("session", "Session (5-hour window)"),
                "seven_day" => ("weekly_all", "Week (all models)"),
                _ when name.StartsWith("seven_day_", StringComparison.Ordinal) =>
                    ($"weekly_{name[10..]}", $"Week ({char.ToUpperInvariant(name[10])}{name[11..]})"),
                _ => (null, null),
            };
            if (key is null || w["utilization"] is not JsonValue u || !u.TryGetValue<double>(out var fraction)) continue;
            var resets = w["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("o") : null;
            limits.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = label,
                ["percent"] = Math.Round(fraction * 100, 1),
                ["resetsAt"] = resets,
                ["active"] = info.Str("rateLimitType") == name,
                ["severity"] = severity,
            });
        }
        if (limits.Count == 0) return null;
        return new JsonObject { ["limits"] = limits, ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("o"), ["stale"] = false };
    }
}
