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
    public double ThresholdPercent { get; init; } = 0.8;
    public int ReserveTokens { get; init; } = 16_384;
    public int KeepRecentTokens { get; init; } = 20_000;
    public string? Model { get; init; }
    public int MaxSummaryTokens { get; init; } = 8192;

    public static CompactionOptions From(ISettings? s)
    {
        if (s is null) return new CompactionOptions();
        var threshold = Get(s, "compaction.thresholdPercent", 0.8);
        if (threshold > 1) threshold /= 100; // "80" means 80%
        return new CompactionOptions
        {
            Enabled = Get(s, "compaction.enabled", true),
            DefaultContextWindow = Math.Max(1024, Get(s, "compaction.defaultContextWindow", 131_072)),
            ThresholdPercent = Math.Clamp(threshold, 0.1, 1.0),
            ReserveTokens = Math.Max(0, Get(s, "compaction.reserveTokens", 16_384)),
            KeepRecentTokens = Math.Max(0, Get(s, "compaction.keepRecentTokens", 20_000)),
            Model = Get<string?>(s, "compaction.model", null) is { Length: > 0 } m ? m.Trim() : null,
            MaxSummaryTokens = Math.Clamp(Get(s, "compaction.maxSummaryTokens", 8192), 256, 128_000),
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
    /// <summary>The caller already holds a lane of <see cref="Model"/>'s pool (the agent loop).</summary>
    public bool HoldsLane { get; init; }
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
    public const string SystemPrompt =
        """
        You are a precise summarizer for an AI coding agent. Your summary replaces the older part of the agent's conversation
        in its context window, so it must preserve everything the agent needs to continue the work seamlessly: the user's
        goal and requirements, constraints and preferences, decisions and their reasons, exact file paths, identifiers,
        commands, error messages and how they were resolved, and the current state of the work.
        Be factual and specific. Never invent anything that is not in the transcript. Leave out pleasantries, repetition
        and details that no longer matter (e.g. superseded attempts), but keep facts that were learned the hard way.
        Write in the language the user writes in. Output only the summary, in Markdown.
        """;

    public const string Structure =
        """
        ## Goal
        What the user wants to achieve overall (and the current sub-task).
        ## Constraints & preferences
        Requirements, conventions, things to avoid, the user's stated preferences.
        ## Progress
        ### Done
        ### In progress
        ## Key decisions
        Decisions made and why.
        ## Files & code touched
        Paths with what was changed or learned about each (functions, classes, config keys).
        ## Current state & next steps
        Where the work stands right now and the concrete next steps.
        ## Open questions
        Unresolved questions, blockers, things to verify.
        """;

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
        if (req.Mode == CompactionMode.Overflow) keep /= 2;
        if (req.Mode == CompactionMode.Manual) keep = Math.Min(keep, Math.Max(1, msgTokens * 2 / 5));
        var maxKeep = Math.Max(keep, Math.Min(keep * 3 / 2, (long)(window * 0.35)));
        var minGain = req.Mode == CompactionMode.Auto ? Math.Max(1000, window / 50) : 1;

        var plan = CompactionPlanner.Plan(context, keep, maxKeep);
        var tokensBefore = req.TokensBefore ?? req.OverheadTokens + msgTokens;
        if (plan is null || plan.SummarizeTokens < minGain)
            return new CompactionResult { Message = $"Nothing to compact (context ≈ {Fmt(tokensBefore)} tokens).", TokensBefore = tokensBefore, TokensAfter = tokensBefore };

        Notice(req.SessionId, "info", $"Compacting context (~{Fmt(tokensBefore)} tokens, {plan.SummarizeCount} messages)…");

        var summarizer = req.Model;
        if (o.Model is { } modelRef)
        {
            var m = await ctx.Models.FindAsync(modelRef, ct).ConfigureAwait(false);
            if (m is not null) summarizer = m;
            else ctx.Logger.LogWarning("compaction.model '{Model}' not found; using {Fallback}", modelRef, req.Model.Ref);
        }

        ILaneLease? lease = null;
        var lanes = ctx.Services.Get<ILaneScheduler>();
        if (lanes is not null)
        {
            var pool = lanes.ResolvePool(summarizer);
            if (!req.HoldsLane || pool != lanes.ResolvePool(req.Model))
            {
                lease = await lanes.AcquireAsync(new LaneRequest
                {
                    PoolKey = pool, AgentId = req.AgentId ?? $"compaction:{req.SessionId}", SessionId = req.SessionId,
                    Label = "compaction", Priority = req.HoldsLane ? 100 : 0, Provider = summarizer.Provider,
                }, ct).ConfigureAwait(false);
            }
        }

        string summary;
        int calls;
        try
        {
            (summary, calls) = await SummarizeAsync(plan.ToSummarize.ToList(), summarizer, req, o, ct).ConfigureAwait(false);
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
        Notice(req.SessionId, "info", message);
        ctx.Logger.LogInformation("Session {Session}: {Message} (mode {Mode}, {Calls} summarizer call(s), up to seq {Seq})",
            req.SessionId, message, req.Mode, calls, plan.UpToSeq);

        return new CompactionResult
        {
            Compacted = true, Message = message, TokensBefore = tokensBefore, TokensAfter = tokensAfter,
            MessagesSummarized = plan.SummarizeCount, UpToSeq = plan.UpToSeq, SummarizerCalls = calls, Summary = appended,
        };
    }

    private async Task<(string Summary, int Calls)> SummarizeAsync(List<ChatMessage> messages, ModelInfo model,
        CompactionRequest req, CompactionOptions o, CancellationToken ct)
    {
        var window = model.ContextWindow is > 0 and var w ? w : o.DefaultContextWindow;
        var maxOut = Math.Min(o.MaxSummaryTokens, model.MaxOutputTokens is > 0 and var mo ? mo : int.MaxValue);
        maxOut = Math.Max(256, Math.Min(maxOut, window / 4));
        // Room for the system prompt, instructions, the rolling summary (≤ maxOut) and the answer (maxOut).
        var budgetTokens = (long)((window - 2L * maxOut - 1500) * 0.85);
        var budgetChars = (int)Math.Clamp(budgetTokens * 4, 2000, 4_000_000);

        var chunks = TranscriptSerializer.Chunk(messages.Select(TranscriptSerializer.Serialize), budgetChars);
        if (chunks.Count == 0) throw new InvalidOperationException("Nothing to summarize.");

        string? summary = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var prompt = BuildPrompt(chunks[i], summary, i, chunks.Count, req.Instructions);
            var request = new ModelRequest
            {
                Model = model,
                SystemPrompt = SystemPrompt,
                Messages = [ChatMessage.UserText(prompt)],
                ReasoningEffort = LowestEffort(model),
                MaxOutputTokens = maxOut,
                SessionId = req.SessionId,
                AgentId = req.AgentId,
                Purpose = "compaction",
            };
            var response = await ctx.Models.CompleteAsync(request, ct).ConfigureAwait(false);
            var text = response.Text.Trim();
            if (text.Length == 0)
                throw new InvalidOperationException($"The summarizer ({model.Ref}) returned an empty response (stop reason: {response.StopReason ?? "?"}).");
            if (response.StopReason == "length")
                ctx.Logger.LogWarning("Compaction summary hit the output limit ({Max} tokens); it may be incomplete", maxOut);
            summary = text;
        }
        return (summary!, chunks.Count);
    }

    public static string BuildPrompt(string transcript, string? previousSummary, int index, int total, string? instructions)
    {
        var sb = new StringBuilder();
        if (previousSummary is null)
        {
            sb.Append(total > 1
                ? $"Summarize the conversation transcript below (part 1 of {total}; the following parts will be merged into your summary later).\n\n"
                : "Summarize the conversation transcript below.\n\n");
        }
        else
        {
            sb.Append($"Below is the summary of the earlier part of the conversation, followed by the next part of the transcript (part {index + 1} of {total}). ")
              .Append("Produce one updated summary that merges both.\n\n")
              .Append("<previous-summary>\n").Append(previousSummary.Trim()).Append("\n</previous-summary>\n\n");
        }
        sb.Append("Use exactly this structure (write \"(none)\" for empty sections):\n\n").Append(Structure).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(instructions))
            sb.Append("Additional focus requested by the user: ").Append(instructions.Trim()).Append("\n\n");
        sb.Append("<transcript>\n").Append(transcript).Append("\n</transcript>");
        return sb.ToString();
    }

    /// <summary>The cheapest reasoning effort the model offers (null when reasoning is not configurable).</summary>
    public static string? LowestEffort(ModelInfo model)
    {
        var r = model.Reasoning;
        if (r is null || !r.Supported || r.Efforts.Count == 0) return null;
        foreach (var candidate in new[] { "none", "off", "minimal", "low", "medium", "high" })
        {
            var hit = r.Efforts.FirstOrDefault(e => string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return r.Efforts[0];
    }

    public void Notice(string sessionId, string level, string text) =>
        ctx.Events.Publish(EventTypes.AgentNotice, new JsonObject { ["sessionId"] = sessionId, ["level"] = level, ["text"] = text }, sessionId);

    public static string Fmt(long tokens) =>
        tokens < 1000 ? tokens.ToString(CultureInfo.InvariantCulture)
        : (tokens / 1000.0).ToString(tokens < 100_000 ? "0.#" : "0", CultureInfo.InvariantCulture) + "k";
}
