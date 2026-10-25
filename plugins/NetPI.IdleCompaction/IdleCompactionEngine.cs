using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI;

namespace NetPI.IdleCompaction;

/// <summary>
/// The sweep: over the open sessions (a window shows them, or they wait on the user), find the ones that have been
/// idle for at least the lead share of their provider's prompt-cache TTL and less than the TTL itself — the window in
/// which compacting beats the cache dying — and compact them in the idle mode, so the next turn re-reads a short,
/// cheap context. One attempt per idle period (a new message starts a new one); a failure or a no-op backs off until
/// near where the cache would have expired anyway. A run that starts cancels the in-flight compaction of its session
/// (<c>agent.status</c>), so a compacting context is never stolen out from under a running one.
/// </summary>
public sealed class IdleCompactionEngine
{
    /// <summary>What <c>idleCompaction.ttlSeconds</c> says when it is unset: Anthropic's 1-hour cache (300 is its 5-minute one).</summary>
    internal const string DefaultTtls = "{\"anthropic\": 3600}";

    private readonly IPluginContext _ctx;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SessionState<Tracker> _state;
    private readonly SessionState<CancellationTokenSource> _active;

    public IdleCompactionEngine(IPluginContext ctx, Func<DateTimeOffset>? clock = null)
    {
        _ctx = ctx;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _state = new SessionState<Tracker>(ctx.Events);
        _active = new SessionState<CancellationTokenSource>(ctx.Events);
        // A run that starts cancels the in-flight compaction of its session: a compacting context is never stolen
        // out from under a running one. The subscription is the plugin's own (the scoped bus), so a reload takes it away.
        ctx.Events.Subscribe(EventTypes.AgentStatus, OnAgentStatus);
    }

    /// <summary>One sweep: every open session that is due gets its idle compaction (at most one attempt per idle period).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var settings = _ctx.Settings;
        if (!settings.GetBool("idleCompaction.enabled", true)) return;
        var ttls = Ttls(settings);
        if (ttls.Count == 0) return;
        var lead = Math.Clamp(settings.GetOr("idleCompaction.leadFraction", 0.8), 0.05, 1.0);
        var minShare = Math.Clamp(settings.GetOr("idleCompaction.minWindowShare", 0.5), 0.0, 1.0);
        var minTokens = Math.Max(0, settings.GetInt("idleCompaction.minTokens", 30_000));
        var now = _clock();

        var presence = _ctx.Services.Get<ISessionPresence>();
        if (presence is null) return;
        foreach (var sessionId in presence.OpenSessions())
        {
            ct.ThrowIfCancellationRequested();
            try { await ConsiderAsync(sessionId, now, ttls, lead, minShare, minTokens, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _ctx.Logger.LogDebug(ex, "Idle compaction sweep: {Session} failed", sessionId); }
        }
    }

    private async Task ConsiderAsync(string sessionId, DateTimeOffset now, Dictionary<string, double> ttls, double lead, double minShare, int minTokens, CancellationToken ct)
    {
        var sessions = _ctx.Sessions;
        if (sessions.GetSession(sessionId) is not { } session) return;
        if (session.Kind != "chat") return;   // a subagent's chat is short-lived; its parent reads the report, not its context

        // A run in this session: its context is in use, and the cancel handler covers the in-flight case.
        var runtime = _ctx.Services.Get<IAgentRuntime>();
        if (runtime?.GetBySession(sessionId) is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded }) return;

        // An open goal: the agent is about to run again in this session; its context is about to be used, not stale.
        if (session.Meta?["goal"] is JsonObject goal && goal["status"]?.GetValue<string>() is "active" or "paused" or "blocked" or "execution-unavailable") return;

        // The last activity: the newest message of any role. Its age is the idle period.
        var page = sessions.GetMessages(sessionId, null, 30);
        if (page.Count == 0) return;
        var last = page[^1];
        var idleFor = now - last.CreatedAt;

        // The model: its provider's TTL and window are the rest of the check.
        var modelRef = await SessionModel.ResolveRefAsync(session, _ctx.Models, _ctx.Settings, _ctx.Services.Get<IAgentScheduler>(), ct).ConfigureAwait(false);
        if (modelRef is null || await _ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false) is not { } model) return;
        if (model.ManagesOwnContext) return;   // the backend compacts its own session
        if (TtlFor(ttls, model.Provider) is not { } ttl) return;   // a provider with no TTL has no cache to beat

        // The context must be worth a summarizer call: at least minTokens, and a real share of the window.
        var window = model.ContextWindow is > 0 and var w ? w : _ctx.Settings.GetInt("compaction.defaultContextWindow", 131_072);
        if (session.ContextTokens <= 0 || session.ContextTokens < minTokens || session.ContextTokens < (long)(window * minShare)) return;

        // The window: at the lead share the cache is due to die, before the TTL there is still something to beat;
        // after the TTL the cache is gone and a compaction would only cost a summarizer call for nothing.
        if (idleFor < TimeSpan.FromSeconds(ttl * lead) || idleFor >= TimeSpan.FromSeconds(ttl)) return;

        // A provider that reports no cache at all for this chat (neither reads nor writes) is not caching it:
        // there is no TTL to beat, and a short context is what a manual compaction is for.
        var lastUsage = page.LastOrDefault(m => m.Role == MessageRole.Assistant && m.Usage is not null)?.Usage;
        if (lastUsage is { CacheReadTokens: 0, CacheWriteTokens: 0 }) return;

        // One attempt per idle period (a new message starts a new one), and the backoff of a failed last attempt.
        var tracker = _state.GetOrAdd(sessionId, _ => new Tracker())!;
        if (tracker.AttemptedSeq == last.Seq || now < tracker.BackoffUntil) return;

        // The budget: a spent budget stops paid calls, and a summarizer call is one.
        if (await BudgetExhaustedAsync(ct).ConfigureAwait(false)) return;

        // In flight: a sweep from the previous tick is still compacting this session.
        if (_active.Contains(sessionId)) return;

        var cts = new CancellationTokenSource();
        _active[sessionId] = cts;
        try
        {
            var result = await _ctx.Rpc.InvokeAsync("compaction.run", new { sessionId, mode = "idle" }, cts.Token).ConfigureAwait(false);
            var text = result?.ToString() ?? "";
            tracker.AttemptedSeq = last.Seq;
            if (!text.Contains("compacted", StringComparison.OrdinalIgnoreCase))
                tracker.BackoffUntil = now + Backoff(idleFor, ttl);   // a no-op: the next attempt is not earlier than the half-remaining
        }
        catch (OperationCanceledException)
        {
            // A run started (the agent.status handler cancelled it) or the plugin stopped: the new message starts a
            // new idle period, so nothing is recorded.
        }
        catch (RpcException ex) when (ex.Code == "busy")
        {
            // A run started between the check and the call: same as above.
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Idle compaction of {Session} failed", sessionId);
            tracker.AttemptedSeq = last.Seq;
            tracker.BackoffUntil = now + Backoff(idleFor, ttl);
        }
        finally
        {
            _active.Forget(sessionId);
        }
    }

    /// <summary>How long to wait after a failed or no-op attempt: half of what is left of the TTL, clamped —
    /// the cache dies where it dies, and hammering a failing summarizer until then is not the point.</summary>
    public static TimeSpan Backoff(TimeSpan idleFor, double ttlSeconds)
    {
        var remaining = Math.Max(0.0, ttlSeconds - idleFor.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(remaining / 2, 30, 1800));
    }

    private async Task<bool> BudgetExhaustedAsync(CancellationToken ct)
    {
        try
        {
            var budget = await _ctx.Rpc.InvokeAsync("budget.status", null, ct).ConfigureAwait(false);
            if (budget is null) return false;
            // The in-process registry hands over the handler's own object (the UI would see its JSON), so read it as JSON.
            var node = budget is JsonNode n ? n : NetPiJson.ToNode(budget);
            return node?["exhausted"] is JsonValue v && v.TryGetValue<bool>(out var spent) && spent;
        }
        catch (RpcException) { return false; }   // no budget (the agents plugin is not loaded): nothing is exhausted
    }

    /// <summary>The provider's TTL from <c>idleCompaction.ttlSeconds</c> (a JSON map, provider → seconds; a bare number
    /// applies to every provider, as does the key <c>*</c>), or null when the provider has none.</summary>
    public static double? TtlFor(Dictionary<string, double> ttls, string provider) =>
        ttls.TryGetValue(provider, out var own) ? own : ttls.TryGetValue("*", out var all) ? all : null;

    /// <summary>The map itself: the setting's JSON (the default when unset), case-insensitive by provider.</summary>
    public static Dictionary<string, double> Ttls(ISettings settings)
    {
        var raw = settings.Get<string>("idleCompaction.ttlSeconds", null)?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) raw = DefaultTtls;
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (double.TryParse(p.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                        map[p.Name] = s;
            }
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var all) && all > 0)
            {
                map["*"] = all;
            }
        }
        catch (JsonException) { }   // an unreadable map is none: idle compaction stays off rather than guessing
        return map;
    }

    private void OnAgentStatus(BusEvent e)
    {
        var agent = e.As<JsonObject>()?["agent"] as JsonObject;
        if (agent?["sessionId"]?.GetValue<string>() is not { Length: > 0 } sessionId) return;
        if (agent?["status"]?.GetValue<string>() is not ("running" or "queued")) return;
        if (_active.TryGetValue(sessionId, out var cts)) cts.Cancel();
    }

    private sealed class Tracker
    {
        /// <summary>The last message's seq that an attempt covered: a new message (a new seq) starts a new idle period.</summary>
        public long AttemptedSeq;
        /// <summary>When a failed or no-op attempt's backoff ends.</summary>
        public DateTimeOffset BackoffUntil;
    }
}
