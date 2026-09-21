using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Nudge;

/// <summary>
/// Cut-off guard (empty-turn nudge): when a model turn ends with no assistant
/// text and no tool calls — the runtime's classic dead end after a
/// provider-truncated thinking stream — this plugin steers the run back to
/// life with a nudge user message, bounded to <c>maxNudges</c> per run.
///
/// The Web surface owns the cut-off <em>notice</em> (it sees
/// <see cref="AgentEventType.TurnEmpty"/> and persists/broadcasts it), so this
/// plugin only adds the continuation: it enqueues a nudge steering message on
/// the same event. The handler runs synchronously inside that publish (the bus
/// dispatches inline), so the enqueue is visible to the runtime's
/// <c>PendingCount()</c> check immediately after.
/// </summary>
public sealed class NudgePlugin : INetPiPlugin
{
    private const string DefaultNudgeText =
        "Your previous turn ended without an answer or tool call — it was cut off " +
        "(usually a truncated reasoning stream). Continue now: finish what you were " +
        "doing and emit your answer text or a tool call. Do not only think.";

    private IPluginContext? _ctx;
    private ISteeringQueue? _steering;
    private readonly object _gate = new();
    private readonly System.Collections.Generic.Dictionary<string, int> _nudgedThisRun = new();

    public PluginInfo Info { get; } = new("netpi.nudge", "Empty-Turn Nudge", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _steering = context.Services.Resolve<ISteeringQueue>("steering");
        context.Events.Subscribe<AgentEvent>(OnAgentStarting, new EventSubscriptionOptions { Priority = 100 });
        context.Events.Subscribe<AgentEvent>(OnAgentEvent, new EventSubscriptionOptions { Priority = 100 });
        context.Log.Information($"Empty-turn nudge active (steering available: {_steering is not null})");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _steering = null;
        _ctx = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Per-session nudge counter resets when a new run starts.</summary>
    private void OnAgentStarting(AgentEvent e)
    {
        if (e.Type != AgentEventType.AgentStarting) return;
        lock (_gate) _nudgedThisRun[e.SessionId ?? "_"] = 0;
    }

    private void OnAgentEvent(AgentEvent e)
    {
        if (e.Type != AgentEventType.TurnEmpty) return;
        if (_ctx?.Log is not { } log) return;
        if (!Enabled()) return;
        if (_steering is not { } steering)
        {
            log.Warning("nudge: steering queue unavailable, cannot continue");
            return;
        }

        var runKey = e.SessionId ?? "_";
        var maxNudges = MaxNudges();
        int number;
        lock (_gate)
        {
            var nudged = _nudgedThisRun.TryGetValue(runKey, out var n) ? n : 0;
            if (nudged >= maxNudges)
            {
                log.Information($"nudge: empty turn {nudged + 1} in {runKey} exceeds max ({maxNudges}), run ends");
                return;
            }
            number = nudged + 1;
            _nudgedThisRun[runKey] = number;
        }

        var text = NudgeText();
        try
        {
            // Synchronous by necessity: the runtime reads PendingCount() right
            // after this event's publish completes (bus dispatch is inline),
            // so the message must already be queued.
            steering.EnqueueAsync(text, e.SessionId, CancellationToken.None).GetAwaiter().GetResult();

            // Lightweight live event for diagnostics; the Web surface owns the
            // persisted cut-off notice, so this plugin does not persist it.
            var evt = new AgentEvent(
                Guid.NewGuid().ToString("n"), AgentEventType.Nudged, DateTimeOffset.UtcNow,
                e.SessionId,
                JsonSerializer.SerializeToElement(new { kind = "nudge", number, text }, CamelCase));
            _ctx!.Events.PublishAsync(evt, CancellationToken.None).GetAwaiter().GetResult();
            log.Information($"nudge {number}/{maxNudges} queued for {runKey}");
        }
        catch (Exception ex)
        {
            log.Error($"nudge failed: {ex.Message}", ex);
        }
    }

    // Config under plugins.netpi.nudge in ~/.netpi/config.json:
    //   enabled (default true), maxNudges (default 2), nudgeText (override).
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private bool Enabled()
    {
        var cfg = _ctx?.OwnConfig;
        if (cfg is not JsonElement ce || ce.ValueKind != JsonValueKind.Object) return true;
        return !ce.TryGetProperty("enabled", out var en) || en.ValueKind == JsonValueKind.True;
    }

    private int MaxNudges()
    {
        var cfg = _ctx?.OwnConfig;
        if (cfg is JsonElement ce && ce.ValueKind == JsonValueKind.Object && ce.TryGetProperty("maxNudges", out var m) && m.ValueKind == JsonValueKind.Number)
            return Math.Max(0, m.GetInt32());
        return 2;
    }

    private string NudgeText()
    {
        var cfg = _ctx?.OwnConfig;
        if (cfg is JsonElement ce && ce.ValueKind == JsonValueKind.Object && ce.TryGetProperty("nudgeText", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString()!;
        return DefaultNudgeText;
    }
}
