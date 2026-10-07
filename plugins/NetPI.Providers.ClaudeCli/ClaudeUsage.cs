using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// The owner's Claude subscription usage (the 5-hour session window and the weekly windows), as the Claude CLI itself
/// reports it: every <c>claude -p</c> run prints <c>rate_limit_event</c> lines with the utilization of each window.
/// A reading is as fresh as the last call (the popout says when it was taken), and the last one is kept across restarts.
/// Pressing the context ring may also ask Anthropic for it (<see cref="RefreshAsync"/>, <see cref="ClaudeUsageApi"/>),
/// at most once a minute.
/// </summary>
public sealed class ClaudeUsageTracker(Action<JsonObject>? persist = null, ILogger? logger = null)
{
    /// <summary>The least time between two asks to Anthropic.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private JsonObject? _latest;
    private string? _plan;
    private long _lastAsk; // Environment.TickCount64 of the last ask; 0 = never
    private Task? _asking;
    private Exception? _lastError; // how the last ask failed; null after a good one

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
        if (Normalize(info) is { } snap) Apply(snap);
    }

    private void Apply(JsonObject snap)
    {
        lock (_gate) _latest = snap;
        try { persist?.Invoke(snap); }
        catch (Exception ex) { _log.LogDebug(ex, "claude-cli: could not store the usage reading"); }
        OnChanged?.Invoke(Get());
    }

    /// <summary>
    /// Asks Anthropic for the reading (<paramref name="fetch"/>) and keeps it, unless that was asked within
    /// <see cref="RefreshEvery"/>: then the reading it has is the answer — or, when that ask failed, the same failure again,
    /// so a press inside the minute does not pass an old reading off as the live one (idea-irqqkj). A caller that arrives
    /// while a fetch runs waits for it. A failed fetch throws to everyone waiting on it.
    /// </summary>
    public async Task<JsonObject> RefreshAsync(Func<CancellationToken, Task<JsonObject>> fetch, CancellationToken ct)
    {
        Task run;
        lock (_gate)
        {
            if (_asking is { } running) run = running;
            else if (_lastAsk != 0 && Environment.TickCount64 - _lastAsk < RefreshEvery.TotalMilliseconds)
            {
                if (_lastError is { } failed)
                    throw failed as RpcException is { } rpc ? new RpcException(rpc.Code, rpc.Message) : new RpcException("unavailable", failed.Message);
                return Get();
            }
            else
            {
                _lastAsk = Environment.TickCount64;
                run = _asking = Task.Run(async () =>
                {
                    try
                    {
                        Apply(await fetch(CancellationToken.None).ConfigureAwait(false));
                        lock (_gate) _lastError = null;
                    }
                    catch (Exception ex)
                    {
                        lock (_gate) _lastError = ex;
                        throw;
                    }
                    finally { lock (_gate) _asking = null; }
                });
            }
        }
        await run.WaitAsync(ct).ConfigureAwait(false);
        return Get();
    }

    /// <summary>
    /// Anthropic's usage answer, <c>{ limits: [{ kind: session | weekly_all | weekly_scoped, percent, severity, resets_at, is_active,
    /// scope: { model: { display_name } } }] }</c>, as the snapshot the UI renders. Tolerant like <see cref="Normalize"/>.
    /// </summary>
    internal static JsonObject? NormalizeApi(JsonObject body)
    {
        if (body["limits"] is not JsonArray list) return null;
        var limits = new JsonArray();
        foreach (var node in list)
        {
            if (node is not JsonObject l || l["percent"] is not JsonValue pv || !pv.TryGetValue<double>(out var percent)) continue;
            var model = l.Obj("scope")?.Obj("model")?.Str("display_name");
            var (key, label) = l.Str("kind") switch
            {
                "session" => ("session", "Session (5-hour window)"),
                "weekly_all" => ("weekly_all", "Week (all models)"),
                "weekly_scoped" when !string.IsNullOrWhiteSpace(model) => ($"weekly_{model.ToLowerInvariant().Replace(' ', '_')}", $"Week ({model})"),
                _ => (null, null),
            };
            if (key is null) continue;
            limits.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = label,
                ["percent"] = Math.Round(percent, 1),
                ["resetsAt"] = DateTimeOffset.TryParse(l.Str("resets_at"), out var at) ? at.ToString("o") : null,
                ["active"] = l["is_active"] is JsonValue a && a.TryGetValue<bool>(out var on) && on,
                ["severity"] = l.Str("severity") ?? "normal",
            });
        }
        if (limits.Count == 0) return null;
        return new JsonObject { ["limits"] = limits, ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("o"), ["stale"] = false };
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
