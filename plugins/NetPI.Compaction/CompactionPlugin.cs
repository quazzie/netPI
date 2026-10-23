using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Compaction;

/// <summary>
/// Auto-compaction (agent hook, runs first), overflow recovery, the <c>compaction.run</c> RPC and the <c>/compact</c>
/// slash command. Settings: <c>compaction.enabled</c>, <c>compaction.defaultContextWindow</c>, <c>compaction.thresholdPercent</c>,
/// <c>compaction.reserveTokens</c>, <c>compaction.keepRecentTokens</c>, <c>compaction.model</c>, <c>compaction.effort</c>, <c>compaction.maxSummaryTokens</c>.
/// </summary>
[NetPiPlugin("netpi.compaction", Name = "Compaction", Description = "Summarizes older messages when the context fills up", Order = 70)]
public sealed class CompactionPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "compaction", Title = "Compaction", Group = "Context", Order = 30,
            Settings =
            [
                SettingInfo.Bool("compaction.enabled", "Compact long conversations", true),
                SettingInfo.Int("compaction.reserveTokens", "Compact when fewer tokens are left", 16384, "Room kept for the next answer.", 0, null, "tokens"),
                SettingInfo.Number("compaction.thresholdPercent", "Also compact at", 1.0, "Share of the context window; 1 = only by the reserve above.", 0.3, 1.0),
                SettingInfo.Int("compaction.keepRecentTokens", "Recent tokens kept verbatim", 20000, null, 0, null, "tokens"),
                SettingInfo.ModelRef("compaction.model", "Summarizer model", null, "the session's model"),
                SettingInfo.Choice("compaction.effort", "Summarizer reasoning", "low", ["none", "low", "medium", "high", "chat"],
                    "Its thinking counts against the summary length. chat: the chat's own effort. An effort the model does not offer means its default."),
                SettingInfo.Int("compaction.maxSummaryTokens", "Summary length", 13107, "Output budget of a summary, thinking included (80 % of the reserve); a summary cut off at it is tried once more with up to twice the room, then not used.", 256, null, "tokens"),
                SettingInfo.Int("compaction.defaultContextWindow", "Context window when unknown", 131072, null, 1024, null, "tokens"),
            ],
        });
        var service = new CompactionService(context);
        var hook = new CompactionHook(service, context.Logger);
        context.Services.Register<IAgentHook>(hook);
        context.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if (e.As<JsonObject>()?["id"]?.GetValue<string>() is { Length: > 0 } id) hook.Forget(id);
        });

        context.Rpc.Register(new RpcMethod("compaction.run", "Summarize older messages of a session: { sessionId, args? } → status string", ReadOnly: false, Params: [RpcParam.Req("sessionId"), RpcParam.Opt("args", RpcParamType.String, "/compact's text: instructions for the summary"), RpcParam.Opt("instructions")]),
            (req, rpcCt) => RunAsync(context, service, req, rpcCt));

        var stats = new CompactionStats(context);
        context.Rpc.Register(new RpcMethod("compaction.stats", "How often a chat was compacted, over its whole history: { sessionId } → { count, last: { tokensBefore, tokensAfter, mode, createdAt, seq } | null }", ReadOnly: true, Params: [RpcParam.Req("sessionId")]),
            (req, _) => Task.FromResult<object?>(stats.Get(context.Sessions.Require(req.Required("sessionId")).Id)));

        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "compact",
            Description = "Summarize older messages to free context",
            Rpc = "compaction.run",
            ArgsHint = "[focus]",
        });
        return Task.CompletedTask;
    }

    internal static async Task<object?> RunAsync(IPluginContext context, CompactionService service, RpcRequest req, CancellationToken ct)
    {
        var sessionId = req.Required("sessionId");
        var session = context.Sessions.Require(sessionId);
        var agent = context.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        if (agent is { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded })
            throw new RpcException("busy", "The agent is running in this session. Compaction happens automatically while it runs; wait for it to finish (or abort it) to compact manually.");

        var model = await service.ResolveSessionModelAsync(session, ct).ConfigureAwait(false)
                    ?? throw new RpcException("no_model", "No model is available for this session.");
        if (model.ManagesOwnContext)
            return $"{model.Provider} keeps and compacts this chat's context itself, so there is nothing to compact here.";
        try
        {
            var result = await service.CompactAsync(new CompactionRequest
            {
                SessionId = sessionId,
                Model = model,
                Mode = CompactionMode.Manual,
                Instructions = req.Str("args") ?? req.Str("instructions"),
                AgentId = agent?.Id,
                HoldsSlot = false,
                TokensBefore = session.ContextTokens > 0
                    ? Math.Max(session.ContextTokens, CompactionPlanner.Estimate(context.Sessions.GetContextMessages(sessionId)))
                    : null,
                OverheadTokens = OverheadGuess(context, session),
            }, ct).ConfigureAwait(false);
            return result.Message;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RpcException))
        {
            context.Logger.LogWarning(ex, "Manual compaction of {Session} failed", sessionId);
            service.Notice(sessionId, "error", "Compaction failed: " + ex.Message, "failed", CompactionMode.Manual);
            throw new RpcException("compaction_failed", "Compaction failed: " + ex.Message);
        }
    }

    /// <summary>System prompt + tools are not known outside a run: derive them from the last reported context size.</summary>
    private static long OverheadGuess(IPluginContext context, SessionInfo session)
    {
        if (session.ContextTokens <= 0) return 0;
        var msgs = CompactionPlanner.Estimate(context.Sessions.GetContextMessages(session.Id));
        return Math.Max(0, session.ContextTokens - msgs);
    }
}

/// <summary>Compacts before a model call when the context is nearly full, and after a context-overflow error.</summary>
public sealed class CompactionHook(CompactionService service, ILogger? logger = null) : IAgentHook
{
    public const string OverflowTurnKey = "netpi.compaction.overflowTurn";
    public const string OverflowCountKey = "netpi.compaction.overflowCount";
    public const int MaxOverflowCompactionsPerRun = 3;
    /// <summary>After a failed auto-compaction, the model calls that pass before it is tried again…</summary>
    public const int BackoffCalls = 5;
    /// <summary>…unless the context grew by this share of the window meanwhile.</summary>
    public const double BackoffGrowth = 0.05;

    // A failed auto-compaction per session: the calls still to skip and the context size it failed at. Without it every
    // model call tried again (a full summarizer pass over ~250k tokens each, six in four minutes on one chat).
    private readonly ConcurrentDictionary<string, (int Skip, long Estimate)> _backoff = new();

    /// <summary>The session is gone: its wait goes with it.</summary>
    public void Forget(string sessionId) => _backoff.TryRemove(sessionId, out _);

    public int Order => -100;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var o = service.Options();
        if (!o.Enabled) return;
        var run = turn.Run;
        if (run.Model.ManagesOwnContext) return; // the backend compacts its own session
        var window = CompactionService.WindowFor(run.Model, o);
        var overhead = ModelMessages.EstimateTokens(turn.SystemPrompt) + CompactionPlanner.EstimateTools(turn.Tools);
        var estimate = EstimateContext(turn, overhead);
        if (!CompactionService.ShouldCompact(estimate, window, o)) return;

        var sessionId = run.Session.Id;
        if (_backoff.TryGetValue(sessionId, out var b) && b.Skip > 0 && estimate < b.Estimate + (long)(window * BackoffGrowth))
        {
            _backoff[sessionId] = (b.Skip - 1, b.Estimate);
            return;
        }

        try
        {
            var result = await service.CompactAsync(new CompactionRequest
            {
                SessionId = sessionId, Model = run.Model, Mode = CompactionMode.Auto, OverheadTokens = overhead,
                TokensBefore = estimate, AgentId = run.Agent.Id, HoldsSlot = true,
            }, run.CancellationToken).ConfigureAwait(false);
            _backoff.TryRemove(sessionId, out _);
            if (!result.Compacted) return;
            await turn.ReloadMessagesAsync().ConfigureAwait(false);
            turn.LastContextTokens = result.TokensAfter;
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never break the run: the model call may still fit, and the overflow path is a second chance. The next
            // attempt waits a few calls (or a real growth of the context), since the same input fails the same way.
            // a session that is not waiting any more has nothing to lose; the ones that wait keep their backoff
            if (_backoff.Count > 1000) foreach (var (id, entry) in _backoff) if (entry.Skip <= 0) _backoff.TryRemove(id, out _);
            _backoff[sessionId] = (BackoffCalls, estimate);
            logger?.LogWarning(ex, "Auto-compaction of {Session} failed", sessionId);
            service.Notice(sessionId, "warn", $"Auto-compaction failed: {ex.Message} It is tried again after {BackoffCalls} model calls.", "failed", CompactionMode.Auto);
        }
    }

    /// <summary>Whether auto-compaction of a session is waiting after a failure (tests).</summary>
    public bool BackingOff(string sessionId) => _backoff.TryGetValue(sessionId, out var b) && b.Skip > 0;

    public async ValueTask<ModelErrorDecision?> OnModelErrorAsync(AgentTurnContext turn, Exception error)
    {
        if (!IsContextOverflow(error)) return null;
        var o = service.Options();
        if (!o.Enabled) return null;
        var run = turn.Run;
        if (run.Items.TryGetValue(OverflowTurnKey, out var t) && t is int ti && ti == turn.TurnIndex) return null;
        var count = run.Items.TryGetValue(OverflowCountKey, out var c) && c is int ci ? ci : 0;
        if (count >= MaxOverflowCompactionsPerRun) return null;
        run.Items[OverflowTurnKey] = turn.TurnIndex;
        run.Items[OverflowCountKey] = count + 1;

        try
        {
            var overhead = ModelMessages.EstimateTokens(turn.SystemPrompt) + CompactionPlanner.EstimateTools(turn.Tools);
            var result = await service.CompactAsync(new CompactionRequest
            {
                SessionId = run.Session.Id, Model = run.Model, Mode = CompactionMode.Overflow, OverheadTokens = overhead,
                TokensBefore = Math.Max(EstimateContext(turn, overhead), CompactionService.WindowFor(run.Model, o)),
                AgentId = run.Agent.Id, HoldsSlot = true,
            }, run.CancellationToken).ConfigureAwait(false);
            if (!result.Compacted) return null;
            await turn.ReloadMessagesAsync().ConfigureAwait(false);
            turn.LastContextTokens = result.TokensAfter;
            return new ModelErrorDecision { Retry = true };
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Overflow compaction of {Session} failed", run.Session.Id);
            service.Notice(run.Session.Id, "warn", "Compaction after a context overflow failed: " + ex.Message, "failed", CompactionMode.Overflow);
            return null;
        }
    }

    public static bool IsContextOverflow(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 5; depth++)
        {
            if (ex is ModelException { ContextOverflow: true }) return true;
            ex = ex is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : ex.InnerException;
        }
        return false;
    }

    /// <summary>
    /// The last usage the model reported plus the estimate of the messages added since. Usage recorded before the
    /// latest compaction summary is stale and ignored. The full chars/4 estimate is only the answer when there is no
    /// usable usage: a usage number is the model's own count, and forcing the expensive full walk to stand as a floor
    /// for it would defeat the laziness the walk is written for.
    /// </summary>
    public static long EstimateContext(AgentTurnContext turn, long overheadTokens)
    {
        var msgs = turn.Messages;
        // The full chars/4 estimate walks the whole history, so it is worked out only on a path that needs it.
        var full = -1L;
        long Full() => full >= 0 ? full : full = overheadTokens + CompactionPlanner.Estimate(msgs);
        if (turn.LastContextTokens <= 0) return Full();

        // One pass for both: the last assistant message that reported a context size, and the newest summary.
        var last = -1;
        long summarySeq = 0;
        for (var i = msgs.Count - 1; i >= 0; i--)
        {
            if (msgs[i].Role == MessageRole.Summary) summarySeq = Math.Max(summarySeq, msgs[i].Seq);
            else if (last < 0 && msgs[i].Role == MessageRole.Assistant && msgs[i].Usage is { ContextTokens: > 0 }) last = i;
        }
        if (last < 0) return Full();
        if (summarySeq > 0 && msgs[last].Seq > 0 && msgs[last].Seq < summarySeq) return Full();

        var fromUsage = turn.LastContextTokens + CompactionPlanner.Estimate(msgs.Skip(last + 1));
        return fromUsage;
    }
}
