using Microsoft.Extensions.Logging;

namespace NetPI.Compaction;

/// <summary>
/// Auto-compaction (agent hook, runs first), overflow recovery, the <c>compaction.run</c> RPC and the <c>/compact</c>
/// slash command. Settings: <c>compaction.enabled</c>, <c>compaction.defaultContextWindow</c>, <c>compaction.thresholdPercent</c>,
/// <c>compaction.reserveTokens</c>, <c>compaction.keepRecentTokens</c>, <c>compaction.model</c>, <c>compaction.maxSummaryTokens</c>.
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
                SettingInfo.ModelRef("compaction.model", "Summarizer model", "It summarizes at the chat's reasoning effort when it offers it.", "the session's model"),
                SettingInfo.Int("compaction.maxSummaryTokens", "Summary length", 13107, "Output budget of a summary, thinking included (80 % of the reserve); a summary cut off at it is not used.", 256, null, "tokens"),
                SettingInfo.Int("compaction.defaultContextWindow", "Context window when unknown", 131072, null, 1024, null, "tokens"),
            ],
        });
        var service = new CompactionService(context);
        context.Services.Register<IAgentHook>(new CompactionHook(service, context.Logger));

        context.Rpc.Register("compaction.run", (req, rpcCt) => RunAsync(context, service, req, rpcCt),
            "Summarize older messages of a session: { sessionId, args? } → status string");

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

    public int Order => -100;

    public async ValueTask OnBeforeModelCallAsync(AgentTurnContext turn)
    {
        var o = service.Options();
        if (!o.Enabled) return;
        var run = turn.Run;
        var window = CompactionService.WindowFor(run.Model, o);
        var overhead = ModelMessages.EstimateTokens(turn.SystemPrompt) + CompactionPlanner.EstimateTools(turn.Tools);
        var estimate = EstimateContext(turn, overhead);
        if (!CompactionService.ShouldCompact(estimate, window, o)) return;

        try
        {
            var result = await service.CompactAsync(new CompactionRequest
            {
                SessionId = run.Session.Id, Model = run.Model, Mode = CompactionMode.Auto, OverheadTokens = overhead,
                TokensBefore = estimate, AgentId = run.Agent.Id, HoldsSlot = true,
            }, run.CancellationToken).ConfigureAwait(false);
            if (!result.Compacted) return;
            await turn.ReloadMessagesAsync().ConfigureAwait(false);
            turn.LastContextTokens = result.TokensAfter;
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never break the run: the model call may still fit, and the overflow path is a second chance.
            logger?.LogWarning(ex, "Auto-compaction of {Session} failed", run.Session.Id);
            service.Notice(run.Session.Id, "warn", "Auto-compaction failed: " + ex.Message, "failed", CompactionMode.Auto);
        }
    }

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
    /// max(last usage + estimate of messages added since, full chars/4 estimate). Usage recorded before the latest
    /// compaction summary is stale and ignored.
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
        return Math.Max(Full(), fromUsage);
    }
}
