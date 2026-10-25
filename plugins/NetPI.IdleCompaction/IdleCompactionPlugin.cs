using Microsoft.Extensions.Logging;
using NetPI.IdleCompaction;

namespace NetPI.IdleCompaction;

/// <summary>
/// Idle compaction: a sweep every 15 s over the open sessions (a window shows them, or they wait on the user)
/// compacts the ones idle for the lead share of their provider's prompt-cache TTL, in the idle mode, so the next
/// turn re-reads a short, cheap context before the cache dies. Settings: <c>idleCompaction.enabled</c>,
/// <c>idleCompaction.ttlSeconds</c> (provider → seconds, e.g. <c>{"anthropic": 3600}</c>),
/// <c>idleCompaction.leadFraction</c> (0.8), <c>idleCompaction.minWindowShare</c> (0.5), <c>idleCompaction.minTokens</c> (30000),
/// <c>idleCompaction.tickSeconds</c> (15, the sweep's interval).
/// The sweep talks to the compaction plugin over its <c>compaction.run</c> RPC (mode "idle") and to the host's
/// <c>ISessionPresence</>; it never touches their state.
/// </summary>
[NetPiPlugin("netpi.idlecompaction", Name = "Idle Compaction", Description = "Compacts open idle chats before their prompt cache expires", Order = 75)]
public sealed class IdleCompactionPlugin : INetPiPlugin
{
    /// <summary>The sweep's clock: the wall clock in the host, a test's hand for the tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private IdleCompactionEngine? _engine;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "idleCompaction", Title = "Idle compaction", Group = "Context", Order = 35,
            Settings =
            [
                SettingInfo.Bool("idleCompaction.enabled", "Compact open chats while they are idle", true,
                    "A sweep every 15 s compacts an open chat that has been idle for the lead share of its provider's prompt cache TTL, so the next turn re-reads a short context before the cache expires."),
                SettingInfo.Text("idleCompaction.ttlSeconds", "Prompt cache TTL by provider",
                    "JSON map: provider → seconds, e.g. {\"anthropic\": 3600} (300 for the 5-minute cache, 3600 for the 1-hour one); a bare number applies to every provider. A provider not listed is never idle-compacted.",
                    IdleCompactionEngine.DefaultTtls),
                SettingInfo.Number("idleCompaction.leadFraction", "Compact when idle for this share of the TTL", 0.8,
                    "Between this share of the TTL and the TTL the chat is compacted; after the TTL the cache is gone and there is nothing to beat.", 0, 1),
                SettingInfo.Number("idleCompaction.minWindowShare", "Only chats at least this full", 0.5,
                    "Share of the context window; smaller chats are not worth a summarizer call.", 0, 1) with { Advanced = true },
                SettingInfo.Int("idleCompaction.minTokens", "Only chats of at least", 30_000,
                    "Tokens of context; smaller chats are not worth a summarizer call.", 0, null, "tokens") with { Advanced = true },
                SettingInfo.Int("idleCompaction.tickSeconds", "Sweep interval", 15,
                    "Seconds between sweeps; the host's presence marks expire after 90 s, so a window's open sessions are re-marked fast enough to stay open.", 2, null, "seconds") with { Advanced = true },
            ],
        });

        _engine = new IdleCompactionEngine(context, Clock);
        _loop = new CancellationTokenSource();
        context.Track(_loop);
        _loopTask = Task.Run(() => LoopAsync(context, _engine, _loop.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _loop?.Cancel();
        if (_loopTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { }   // a sweep stuck on a slow RPC: the plugin is going away, and the compaction's per-session lock releases with it
        }
    }

    /// <summary>One sweep now (a test drives the ticks; the host's loop does not).</summary>
    public Task TickAsync(CancellationToken ct = default) => _engine?.TickAsync(ct) ?? Task.CompletedTask;

    private static async Task LoopAsync(IPluginContext ctx, IdleCompactionEngine engine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // the interval is a setting (a test shortens it), re-read per sweep
            var tick = TimeSpan.FromSeconds(Math.Max(2, ctx.Settings.GetInt("idleCompaction.tickSeconds", 15)));
            try { await Task.Delay(tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { await engine.TickAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { ctx.Logger.LogDebug(ex, "The idle compaction sweep failed"); }
        }
    }
}
