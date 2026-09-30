using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Compaction;

public enum CompactionMode { Auto, Overflow, Manual }

public sealed class CompactionOptions
{
    public bool Enabled { get; init; } = true;
    public int DefaultContextWindow { get; init; } = 131_072;
    /// <summary>Also compact at this share of the window; 1 (the default) = only when fewer than the reserve tokens are left.</summary>
    public double ThresholdPercent { get; init; } = 1.0;
    public int ReserveTokens { get; init; } = 16_384;
    public int KeepRecentTokens { get; init; } = 20_000;
    public string? Model { get; init; }
    /// <summary>The summary's output budget (thinking included): by default 80 % of the reserve.</summary>
    public int MaxSummaryTokens { get; init; } = 13_107;

    public static CompactionOptions From(ISettings? s)
    {
        if (s is null) return new CompactionOptions();
        var threshold = Get(s, "compaction.thresholdPercent", 1.0);
        var reserve = Math.Max(0, Get(s, "compaction.reserveTokens", 16_384));
        if (threshold > 1) threshold /= 100; // "80" means 80%
        return new CompactionOptions
        {
            Enabled = Get(s, "compaction.enabled", true),
            DefaultContextWindow = Math.Max(1024, Get(s, "compaction.defaultContextWindow", 131_072)),
            ThresholdPercent = Math.Clamp(threshold, 0.1, 1.0),
            ReserveTokens = reserve,
            KeepRecentTokens = Math.Max(0, Get(s, "compaction.keepRecentTokens", 20_000)),
            Model = Get<string?>(s, "compaction.model", null) is { Length: > 0 } m ? m.Trim() : null,
            MaxSummaryTokens = Math.Clamp(Get(s, "compaction.maxSummaryTokens", (int)(reserve * 0.8)), 256, 128_000),
        };
    }

    private static T Get<T>(ISettings s, string path, T fallback)
    {
        try { return s.Get(path, fallback) ?? fallback; } catch { return fallback; }
    }
}

public sealed class CompactionRequest
{
    public required string SessionId { get; init; }
    /// <summary>The session's/agent's model (context window; default summarizer).</summary>
    public required ModelInfo Model { get; init; }
    public CompactionMode Mode { get; init; } = CompactionMode.Auto;
    /// <summary>System prompt + tool definitions (estimated tokens), counted in the before/after figures.</summary>
    public long OverheadTokens { get; init; }
    /// <summary>Context size that triggered the compaction (null = estimate from the messages).</summary>
    public long? TokensBefore { get; init; }
    /// <summary>Extra focus instructions for the summary (/compact args).</summary>
    public string? Instructions { get; init; }
    public string? AgentId { get; init; }
    /// <summary>The caller already holds a slot for <see cref="Model"/> (the agent loop).</summary>
    public bool HoldsSlot { get; init; }
}

public sealed class CompactionResult
{
    public bool Compacted { get; init; }
    public required string Message { get; init; }
    public long TokensBefore { get; init; }
    public long TokensAfter { get; init; }
    public int MessagesSummarized { get; init; }
    public long UpToSeq { get; init; }
    public int SummarizerCalls { get; init; }
    public ChatMessage? Summary { get; init; }
}

/// <summary>Plans, summarizes and applies a compaction (shared by the hook and the compaction.run RPC).</summary>
public sealed class CompactionService(IPluginContext ctx)
{
    public const string SystemPrompt = SummaryPrompts.System;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public CompactionOptions Options() => CompactionOptions.From(ctx.Settings);

    public static int WindowFor(ModelInfo model, CompactionOptions o) =>
        model.ContextWindow is > 0 and var w ? w : o.DefaultContextWindow;

    /// <summary>Compact when the estimate is above threshold × window or within the reserve of the window.</summary>
    public static bool ShouldCompact(long estimate, int window, CompactionOptions o)
    {
        var reserve = Math.Min(o.ReserveTokens, window / 2);
        return estimate > window * o.ThresholdPercent || estimate > window - reserve;
    }

    public async Task<ModelInfo?> ResolveSessionModelAsync(SessionInfo session, CancellationToken ct)
    {
        var models = ctx.Models;
        foreach (var r in new[] { session.Model, models.DefaultModelRef })
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var m = await models.FindAsync(r, ct).ConfigureAwait(false);
            if (m is not null) return m;
        }
        return models.Cached.FirstOrDefault();
    }

    public async Task<CompactionResult> CompactAsync(CompactionRequest req, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(req.SessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await CompactCoreAsync(req, Options(), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CompactionResult> CompactCoreAsync(CompactionRequest req, CompactionOptions o, CancellationToken ct)
    {
        var sessions = ctx.Sessions;
        var window = WindowFor(req.Model, o);
        var context = sessions.GetContextMessages(req.SessionId);
        var msgTokens = CompactionPlanner.Estimate(context);

        long keep = Math.Min(o.KeepRecentTokens, (long)(window * 0.3));
        // What every call sends (system prompt, tool schemas) takes room too: with the largest summary, what is kept must
        // leave the context at most 60 % full, or on a small window the next tool result overflows it. Keep at least a
        // twentieth of the window (the latest exchange).
        var summaryCap = Math.Max(256, Math.Min(o.MaxSummaryTokens, window / 4));
        var room = (long)(window * 0.6) - req.OverheadTokens - summaryCap;
        keep = Math.Min(keep, Math.Max(room, Math.Min(keep, window / 20)));
        // Overflow: the backend rejected what was actually sent, so its real window may be smaller than the catalog says
        // (e.g. a model loaded with a smaller n_ctx than its static capability entry). Size the cut from the messages that
        // were sent, not from the advertised window, or the retry fails again.
        if (req.Mode == CompactionMode.Overflow) keep = Math.Min(keep / 2, Math.Max(1, msgTokens * 2 / 5));
        if (req.Mode == CompactionMode.Manual) keep = Math.Min(keep, Math.Max(1, msgTokens * 2 / 5));
        var maxKeep = Math.Max(keep, Math.Min(keep * 3 / 2, (long)(window * 0.35)));
        var minGain = req.Mode == CompactionMode.Auto ? Math.Max(1000, window / 50) : 1;

        var plan = CompactionPlanner.Plan(context, keep, maxKeep);
        var tokensBefore = req.TokensBefore ?? req.OverheadTokens + msgTokens;
        if (plan is null || plan.SummarizeTokens < minGain || !plan.ToSummarize.Any(IsConversation))
            return new CompactionResult { Message = $"Nothing to compact (context ≈ {Fmt(tokensBefore)} tokens).", TokensBefore = tokensBefore, TokensAfter = tokensBefore };

        Notice(req.SessionId, "info", $"Compacting context (~{Fmt(tokensBefore)} tokens, {plan.SummarizeCount} messages)…", "start", req.Mode);

        var summarizer = req.Model;
        if (o.Model is { } modelRef)
        {
            var m = await ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false);
            if (m is not null) summarizer = m;
            else ctx.Logger.LogWarning("compaction.model '{Model}' not found; using {Fallback}", modelRef, req.Model.Ref);
        }

        IAgentSlot? lease = null;
        var scheduler = ctx.Services.Get<IAgentScheduler>();
        if (scheduler is not null)
        {
            // the chat's own model: its run already holds the slot (another slot on the same local model could wait forever)
            if (!req.HoldsSlot || !string.Equals(summarizer.Ref, req.Model.Ref, StringComparison.OrdinalIgnoreCase))
            {
                var pool = scheduler.Resolve(summarizer);
                lease = await scheduler.AcquireAsync(new AgentSlotRequest
                {
                    Key = pool, AgentId = req.AgentId ?? $"compaction:{req.SessionId}", SessionId = req.SessionId,
                    Label = "compaction", Priority = req.HoldsSlot ? 100 : 0, Provider = summarizer.Provider,
                }, ct).ConfigureAwait(false);
            }
        }

        string summary;
        int calls;
        List<string> readFiles, modifiedFiles;
        try
        {
            (summary, calls, readFiles, modifiedFiles) = await SummarizePlanAsync(plan, summarizer, req, o, ct).ConfigureAwait(false);
        }
        finally { lease?.Dispose(); }

        var summaryMsg = new ChatMessage
        {
            SessionId = req.SessionId,
            Role = MessageRole.Summary,
            Parts = [new TextPart { Text = summary }],
            Model = summarizer.Id,
            Provider = summarizer.Provider,
        };
        var tokensAfter = req.OverheadTokens + CompactionPlanner.Estimate(summaryMsg) + plan.KeptTokens;
        summaryMsg.Meta = new JsonObject
        {
            ["kind"] = "compaction",
            ["coversUpToSeq"] = plan.UpToSeq,
            ["tokensBefore"] = tokensBefore,
            ["tokensAfter"] = tokensAfter,
            ["messages"] = plan.SummarizeCount,
            ["mode"] = req.Mode.ToString().ToLowerInvariant(),
            ["summarizer"] = summarizer.Ref,
            ["readFiles"] = new JsonArray([.. readFiles.Select(f => (JsonNode?)f)]),
            ["modifiedFiles"] = new JsonArray([.. modifiedFiles.Select(f => (JsonNode?)f)]),
        };

        // Append first: if anything fails afterwards the old messages are still in the context.
        var appended = sessions.AppendMessage(req.SessionId, summaryMsg);
        sessions.MarkCompacted(req.SessionId, plan.UpToSeq);
        // An earlier summary is folded into the new one; its seq may lie after the cut.
        foreach (var old in plan.ToSummarize)
        {
            if (old.Role != MessageRole.Summary || old.Seq <= plan.UpToSeq || old.Compacted) continue;
            try { old.Compacted = true; sessions.UpdateMessage(old); }
            catch (Exception ex) { ctx.Logger.LogWarning(ex, "Could not mark the previous summary {Seq} as compacted", old.Seq); }
        }

        try { sessions.UpdateSession(req.SessionId, s => s.ContextTokens = tokensAfter); }
        catch (Exception ex) { ctx.Logger.LogDebug(ex, "Could not update the session context size"); }
        ctx.Events.Publish(EventTypes.SessionContext, new JsonObject
        {
            ["sessionId"] = req.SessionId, ["used"] = tokensAfter, ["window"] = window,
        });

        var message = $"Context compacted: ~{Fmt(tokensBefore)} → ~{Fmt(tokensAfter)} tokens ({plan.SummarizeCount} messages summarized).";
        Notice(req.SessionId, "info", message, "done", req.Mode);
        ctx.Logger.LogInformation("Session {Session}: {Message} (mode {Mode}, {Calls} summarizer call(s), up to seq {Seq})",
            req.SessionId, message, req.Mode, calls, plan.UpToSeq);

        return new CompactionResult
        {
            Compacted = true, Message = message, TokensBefore = tokensBefore, TokensAfter = tokensAfter,
            MessagesSummarized = plan.SummarizeCount, UpToSeq = plan.UpToSeq, SummarizerCalls = calls, Summary = appended,
        };
    }

    /// <summary>
    /// The summary of a plan, after pi: an earlier summary is merged by rule (`<previous-summary>`), not re-read as part
    /// of the conversation; when the kept part starts inside a turn, that turn's start gets its own summary; the files
    /// read and modified come from the tool calls. Returns the summary text with its file lists.
    /// </summary>
    private async Task<(string Summary, int Calls, List<string> Read, List<string> Modified)> SummarizePlanAsync(
        CompactionPlan plan, ModelInfo model, CompactionRequest req, CompactionOptions o, CancellationToken ct)
    {
        var toSummarize = plan.ToSummarize.ToList();
        var previous = toSummarize.FirstOrDefault(m => m.Role == MessageRole.Summary);
        var previousText = previous is null ? null : FileLists.Strip(previous.Text);
        var fresh = toSummarize.Where(m => m.Role != MessageRole.Summary).ToList();
        if (fresh.Count == 0) throw new InvalidOperationException("Nothing to summarize.");

        // a split turn: the first kept message is not the start of a turn, and that turn's user message is summarized
        var turnStart = -1;
        if (plan.CutIndex < plan.Messages.Count && plan.Messages[plan.CutIndex].Role != MessageRole.User)
            turnStart = fresh.FindLastIndex(m => m.Role == MessageRole.User);
        var history = turnStart >= 0 ? fresh[..turnStart] : fresh;
        var prefix = turnStart >= 0 ? fresh[turnStart..] : [];

        var sessionEffort = ctx.Sessions.GetSession(req.SessionId)?.Reasoning;
        var effort = EffortFor(model, sessionEffort);
        var calls = 0;
        string summary;
        if (prefix.Count > 0)
        {
            var historyText = previousText ?? "No earlier history.";
            if (history.Any(IsConversation))
            {
                (historyText, var n) = await RollAsync(history, previousText, false, model, effort, req, o, o.MaxSummaryTokens, ct).ConfigureAwait(false);
                calls += n;
            }
            var (prefixText, m) = await RollAsync(prefix, null, true, model, effort, req, o, o.MaxSummaryTokens / 2, ct).ConfigureAwait(false);
            calls += m;
            summary = historyText + SummaryPrompts.SplitTurnHeading + prefixText;
        }
        else
        {
            (summary, calls) = await RollAsync(fresh, previousText, false, model, effort, req, o, o.MaxSummaryTokens, ct).ConfigureAwait(false);
        }

        var (read, modified) = FileLists.Collect(fresh, previous?.Meta);
        return (FileLists.Strip(summary) + FileLists.Format(read, modified), calls, read, modified);
    }

    /// <summary>The summarizer's fixed prompt parts (system prompt, conversation tags, instructions), in tokens (chars/4).</summary>
    public const int FixedPromptTokens = 1500;
    /// <summary>The smallest chunk a roll can use: the chunker's 2000-char floor, in tokens.</summary>
    public const int MinChunkTokens = 500;
    /// <summary>The chunk keeps this share of the room, so the request never runs the full window.</summary>
    public const double HeadroomShare = 0.85;
    /// <summary>The largest chunk a roll builds (chars); beyond it the summarizer's window is the binding limit anyway.</summary>
    public const int MaxChunkChars = 4_000_000;

    /// <summary>The summarizer's window, its output allowance for this budget and the room left for a chunk (no previous summary).</summary>
    private static (int Window, int MaxOut, int BudgetChars) RollBudget(ModelInfo model, CompactionOptions o, int budget)
    {
        var window = model.ContextWindow is > 0 and var w ? w : o.DefaultContextWindow;
        var maxOut = Math.Max(256, Math.Min(Math.Min(budget, model.MaxOutputTokens is > 0 and var mo ? mo : int.MaxValue), window / 4));
        // Room for the fixed prompt, the summary so far (≤ maxOut) and the answer (maxOut).
        var budgetTokens = (long)((window - 2L * maxOut - FixedPromptTokens) * HeadroomShare);
        return (window, maxOut, (int)Math.Clamp(budgetTokens * 4, MinChunkTokens * 4, MaxChunkChars));
    }

    /// <summary>
    /// Summarize messages in one call, or in rolling parts when they do not fit the summarizer's window (each part merged
    /// into the summary so far). A previous summary larger than this summarizer can carry is condensed first; a summary
    /// cut off at the output limit is refused: the context stays as it was.
    /// </summary>
    private async Task<(string Summary, int Calls)> RollAsync(List<ChatMessage> messages, string? previousSummary, bool turnPrefix,
        ModelInfo model, string? effort, CompactionRequest req, CompactionOptions o, int budget, CancellationToken ct)
    {
        var (window, maxOut, _) = RollBudget(model, o, budget);
        string? summary = previousSummary;
        int calls = 0;
        // The previous summary is embedded in the request in full and was written for whatever summarizer produced it,
        // so it is not bounded by this summarizer's output allowance: budget its real size.
        var priorTokens = summary is null ? 0 : ModelMessages.EstimateTokens(summary);
        if (summary is not null && priorTokens > (long)window - maxOut - FixedPromptTokens - MinChunkTokens)
        {
            // It stops fitting: even a minimal chunk plus the fixed prompt and the answer no longer do. Condense it
            // first, rolling over it as one message (Chunk splits an entry that does not fit), until it is at most what
            // a summary so far may be.
            var shrinkBudget = Math.Max(256, (int)(window - 2L * maxOut - FixedPromptTokens - MinChunkTokens));
            (summary, calls) = await RollAsync([new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = summary }] }],
                null, false, model, effort, req, o, shrinkBudget, ct).ConfigureAwait(false);
            priorTokens = ModelMessages.EstimateTokens(summary);
        }
        // Room for the fixed prompt, the summary so far (its real size, ≤ maxOut after a condense) and the answer.
        var room = (long)window - maxOut - Math.Max(priorTokens, maxOut) - FixedPromptTokens;
        var budgetTokens = (long)Math.Max(0, room * HeadroomShare);
        var budgetChars = (int)Math.Clamp(budgetTokens * 4, MinChunkTokens * 4, MaxChunkChars);

        var chunks = TranscriptSerializer.Chunk(messages.Select(TranscriptSerializer.Serialize), budgetChars);
        if (chunks.Count == 0) throw new InvalidOperationException("Nothing to summarize.");

        foreach (var chunk in chunks)
        {
            var request = new ModelRequest
            {
                Model = model,
                SystemPrompt = SystemPrompt,
                Messages = [ChatMessage.UserText(SummaryPrompts.Build(chunk, summary, turnPrefix, req.Instructions))],
                ReasoningEffort = effort,
                MaxOutputTokens = maxOut,
                SessionId = req.SessionId,
                AgentId = req.AgentId,
                Purpose = "compaction",
            };
            var response = await ctx.Models.CompleteAsync(request, ct).ConfigureAwait(false);
            if (response.StopReason == "length")
                throw new InvalidOperationException($"The summary hit the output limit ({maxOut} tokens) and would be incomplete; the context was left as it was (setting compaction.maxSummaryTokens).");
            var text = response.Text.Trim();
            if (text.Length == 0)
                throw new InvalidOperationException($"The summarizer ({model.Ref}) returned an empty response (stop reason: {response.StopReason ?? "?"}).");
            summary = text;
        }
        return (summary!, chunks.Count + calls);
    }

    /// <summary>
    /// Something to summarize. Notices alone are not: the project, instruction files, skills and the like are announced
    /// again by their plugins once their notices are compacted away, and a summary of them only invents a task ("no
    /// explicit task yet") that contradicts the turn that follows.
    /// </summary>
    private static bool IsConversation(ChatMessage m) => m.Role is MessageRole.User or MessageRole.Assistant;

    /// <summary>The chat's reasoning effort when the summarizer offers it, else the model's default (null).</summary>
    public static string? EffortFor(ModelInfo model, string? sessionEffort)
    {
        var r = model.Reasoning;
        if (r is null || !r.Supported || string.IsNullOrWhiteSpace(sessionEffort)) return null;
        return r.Efforts.FirstOrDefault(e => string.Equals(e, sessionEffort, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A compaction's transient notice for the chat (<c>agent.notice</c> with <c>kind: "compaction"</c>, <c>phase</c>
    /// start | done | failed and <c>mode</c>): the UI keeps its banner while the summary is written, and after one
    /// inside a run until the model answers again.
    /// </summary>
    public void Notice(string sessionId, string level, string text, string phase, CompactionMode mode) =>
        ctx.Events.Publish(EventTypes.AgentNotice, new JsonObject
        {
            ["sessionId"] = sessionId, ["level"] = level, ["text"] = text,
            ["kind"] = "compaction", ["phase"] = phase, ["mode"] = mode.ToString().ToLowerInvariant(),
        }, sessionId);

    public static string Fmt(long tokens) =>
        tokens < 1000 ? tokens.ToString(CultureInfo.InvariantCulture)
        : (tokens / 1000.0).ToString(tokens < 100_000 ? "0.#" : "0", CultureInfo.InvariantCulture) + "k";
}
