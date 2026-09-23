using System.Text;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.AutoCompact;

/// <summary>
/// Configuration for the AutoCompact plugin (PLAN §32), read from the
/// <c>netpi.autoCompact</c> section of ~/.netpi/config.json.
/// </summary>
public sealed record AutoCompactConfig(
    bool Enabled,
    int ReserveTokens,
    int KeepRecentTokens,

    int DefaultContextWindow,
    int MaxContextMessages)
{
    public static AutoCompactConfig FromJson(JsonElement cfg)
    {
        bool enabled = cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("enabled", out var e) && e.ValueKind != JsonValueKind.False
            ? e.GetBoolean() : false;
        return new AutoCompactConfig(
            Enabled: enabled,
            ReserveTokens: Get(cfg, "reserveTokens", 16_384),
            KeepRecentTokens: Get(cfg, "keepRecentTokens", 20_000),
            DefaultContextWindow: Get(cfg, "defaultContextWindow", 131_072),
            MaxContextMessages: Get(cfg, "maxContextMessages", 4_096));
    }

    private static int Get(JsonElement cfg, string name, int fallback)
        => cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : fallback;
}

/// <summary>
/// Coarse, tokenizer-free token estimator (PLAN §32: "Avoid bringing in
/// model-specific tokenizers initially"). Estimates tokens as chars/4 across
/// text, reasoning, tool-call arguments and tool results.
/// </summary>
internal static class TokenEstimator
{
    public static int Estimate(AgentMessage msg)
    {
        int chars = 0;
        foreach (var part in msg.Parts)
        {
            switch (part)
            {
                case TextPart t: chars += t.Text.Length; break;
                case ThinkingPart t: chars += t.Text.Length; break;
                case ToolCallPart c: chars += c.Name.Length + c.Arguments.GetRawText().Length; break;
                case ToolResultPart r: chars += r.ToolName.Length + PartsChars(r.Parts); break;
                case ImagePart: chars += 1_000; break; // rough image allowance
            }
        }
        return EstimateFromChars(chars);
    }

    private static int PartsChars(IReadOnlyList<MessagePart> parts)
    {
        int chars = 0;
        foreach (var p in parts)
            if (p is TextPart t) chars += t.Text.Length;
        return chars;
    }

    public static int EstimateFromChars(int chars) => chars / 4 + 1;

    public static int EstimateList(IReadOnlyList<AgentMessage> messages)
    {
        int sum = 0;
        foreach (var m in messages) sum += Estimate(m);
        return sum;
    }

    /// <summary>PLAN §32: context measurement = last provider-reported prompt
    /// usage (authoritative) + estimate of the messages added SINCE that request.
    /// Without a usage base (no model call yet) falls back to a full estimate.</summary>
    public static int EstimateContext(
        IReadOnlyList<AgentMessage> messages, int lastPromptTokens, int lastUsageMessageCount)
    {
        if (lastPromptTokens > 0 && lastUsageMessageCount > 0)
        {
            int covered = Math.Min(lastUsageMessageCount, messages.Count);
            return lastPromptTokens + EstimateList(messages.Skip(covered).ToList());
        }
        return EstimateList(messages);
    }
}

/// <summary>
/// The AutoCompact service (PLAN §32/§33). Registered under id "compaction".
/// On trigger it summarizes the older messages (tools disabled) via the active
/// provider, persists a compaction entry, and returns the reconstructed active
/// context (summary + retained recent tail).
/// </summary>
public sealed class AutoCompactService : ICompaction
{
    private readonly IPluginContext _ctx;
    private AutoCompactConfig _config;

    public AutoCompactService(IPluginContext ctx, AutoCompactConfig config)
    {
        _ctx = ctx;
        _config = config;
    }

    public bool IsAvailable => _config.Enabled;

    /// <summary>
    /// astra-1 G2 (F contract): publish the policy the context meter surfaces —
    /// availability (enabled) + the configured reserve (the trigger for a
    /// window <c>W</c> is <c>W − ReserveTokens</c>). Reflects the LIVE config
    /// (config updates re-read this).
    /// </summary>
    public CompactionPolicy? ContextPolicy => new(IsAvailable, _config.ReserveTokens);

    internal void UpdateConfig(AutoCompactConfig config) => _config = config;

    public async ValueTask<CompactionResult> CompactAsync(
        CompactionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return new CompactionResult(null, null);

        var store = ResolveStore();
        var catalog = ResolveCatalog();
        var provider = ResolveProvider();
        if (store is null || provider is null) return new CompactionResult(null, null);

        // astra-1 A: measure/retain over the ACTIVE context — from the latest
        // checkpoint's RETAINED tail on (the tail starts BEFORE the checkpoint
        // entry's own sequence), or the bounded recent tail when there is no
        // checkpoint. Never the oldest-first window, and never history that an
        // earlier checkpoint already folded into a summary.
        SessionEntry? latest = await store.LatestCompactionAsync(request.SessionId, cancellationToken);
        var oldPayload = latest?.Payload is { } el2
            ? JsonSerializer.Deserialize<CompactionEntryPayload>(el2.GetRawText(), WireOpts)
            : null;
        int oldRetainedFrom = oldPayload?.RetainedFromSequence ?? 0;
        var entries = oldPayload is not null && oldRetainedFrom > 0
            ? await ReadFullActiveRangeAsync(store, request.SessionId, oldRetainedFrom - 1, cancellationToken)
            : await store.ReadRecentAsync(request.SessionId, _config.MaxContextMessages, cancellationToken);

        var messages = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null)
            .Select(e => e.Message!)
            .ToList();

        if (messages.Count < 2) return new CompactionResult(null, null);

        // ---- estimate + trigger (PLAN §32) --------------------------------
        var contextWindow = (catalog is not null
                              ? catalog.Models.FirstOrDefault(m => m.ModelId == request.ModelId)?.ContextWindowTokens
                              : null)
            ?? _config.DefaultContextWindow;
        int threshold = contextWindow - _config.ReserveTokens;
        int estimated = TokenEstimator.EstimateContext(messages, request.LastPromptTokens, request.LastUsageMessageCount);
        if (estimated <= threshold)
            return new CompactionResult(null, null);

        _ctx.Log.Information(
            $"AutoCompact: estimated {estimated} tokens > threshold {threshold} " +
            $"(window {contextWindow}, reserve {_config.ReserveTokens}) — compacting.");

        // ---- pick the retained recent tail (PLAN §32) ----------------------
        int retainedFrom = ChooseRetainedFrom(entries, request.KeepRecentTokens > 0 ? request.KeepRecentTokens : _config.KeepRecentTokens);
        var retained = RetainedMessages(entries, retainedFrom);

        // Only messages NEWER than the previous checkpoint's retained tail are
        // summarized — anything older is already inside the existing summary.
        int summarizeFrom = Math.Max(oldRetainedFrom, 1);
        var toSummarize = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null
                        && e.Sequence >= summarizeFrom && e.Sequence < retainedFrom)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Message!)
            .ToList();
        if (toSummarize.Count == 0) return new CompactionResult(null, null);

        int summarizedThrough = FirstMessageSeqBefore(entries, retainedFrom);

        // ---- summarize (tools disabled, PLAN §33) -------------------------
        // docs/plans/compaction-tool-history.md §4.3: do NOT concatenate the
        // previous summary onto the fresh one — it grows without bound across
        // repeated compactions. When a prior summary exists, fold it into the
        // material being summarized so the model returns ONE bounded summary of
        // everything (old + new), replacing the old rather than appending to it.
        var previous = oldPayload is not null && !string.IsNullOrWhiteSpace(oldPayload.Summary)
            ? oldPayload.Summary
            : null;
        string summary;
        if (previous is null)
            summary = await SummarizeAsync(provider, request.ModelId, toSummarize, cancellationToken);
        else
        {
            var material = new List<AgentMessage>(toSummarize.Count + 1)
            {
                new("prior-summary", MessageRole.User,
                    [new TextPart("Carry this summary of earlier work forward:\n" + previous)],
                    DateTimeOffset.UtcNow),
            };
            material.AddRange(toSummarize);
            summary = await SummarizeAsync(provider, request.ModelId, material, cancellationToken);
        }

        // ---- reconstruct active context (PLAN §31/§33) ---------------------
        var activeContext = BuildActiveContext(retained, summary);
        // docs/plans/compaction-tool-history.md §3.5: the SAME normalization
        // policy that protects run-start reconstruction also protects the
        // compaction RETURN — a boundary that straddled a tool exchange would
        // otherwise leave a dangling call in the rebuilt context.
        var normalizedActive = TranscriptSanitizer.Normalize(activeContext);
        if (normalizedActive.Report.ValidationFailure is { } validationFailure)
            throw new InvalidOperationException($"compacted transcript cannot be repaired: {validationFailure}");
        if (normalizedActive.Report.AnyRepairs)
            LogRepairs(normalizedActive.Report);
        activeContext = normalizedActive.Messages.ToList();

        // ---- persist compaction entry (PLAN §31) ---------------------------
        var payload = new CompactionEntryPayload
        {
            Summary = summary,
            SummarizedThroughSequence = summarizedThrough,
            RetainedFromSequence = retainedFrom,
            EstimatedTokensBefore = estimated,
            EstimatedTokensAfter = TokenEstimator.EstimateList(activeContext),
            ModelId = request.ModelId,
        };
        // astra-1 A (message identity): the checkpoint entry's identity is derived
        // from its persisted payload (deterministic, not a fresh GUID), so the
        // compaction-summary identity derived from it stays stable across runs.
        var payloadElement = JsonSerializer.SerializeToElement(payload, WireOpts);
        var entryId = MessageIdentity.DeterministicId("checkpoint", payloadElement.GetRawText());
        var compactionEntry = new SessionEntry(
            entryId, request.SessionId, EntryKind.Compaction, null,
            payloadElement, DateTimeOffset.UtcNow,
            Sequence: 0); // store assigns the real seq on append

        await store.AppendAsync(compactionEntry, cancellationToken);

        _ctx.Log.Information(
            $"AutoCompact: summarized through seq {summarizedThrough}, retained from seq {retainedFrom} " +
            $"({activeContext.Count} messages); est {estimated} → {payload.EstimatedTokensAfter} tokens.");

        return new CompactionResult(payload, activeContext);
    }

    public async ValueTask<IReadOnlyList<AgentMessage>> BuildActiveContextAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        var store = ResolveStore();
        if (store is null) return [];

        // astra-1 A: the latest compaction checkpoint is found by sequence ALONE
        // (LatestCompactionAsync), independent of any history window — a bounded
        // ReadAsync window must never substitute old history for the current one
        // (a checkpoint beyond the old 200/MaxContextMessages read limit was
        // invisible before; a session longer than the window lost its newest
        // messages to the oldest-first window).

        SessionEntry? latestCompaction = await store.LatestCompactionAsync(sessionId, cancellationToken);

        var payload = latestCompaction?.Payload is { } el
            ? JsonSerializer.Deserialize<CompactionEntryPayload>(el.GetRawText(), WireOpts)
            : null;

        IReadOnlyList<SessionEntry> entries;
        if (payload is not null && payload.RetainedFromSequence > 0)
        {
            // Active context = summary + everything from the retained tail on.
            // The tail starts at RetainedFromSequence — BEFORE the checkpoint
            // entry's own sequence — and the read also covers everything appended
            // after the checkpoint. Reading from the checkpoint's own sequence
            // would silently drop the retained tail.
            entries = await ReadFullActiveRangeAsync(store, sessionId, payload.RetainedFromSequence - 1, cancellationToken);
        }
        else
        {
            // No checkpoint (or a corrupt one): the bounded RECENT tail — never an
            // oldest-first window.
            entries = await store.ReadRecentAsync(sessionId, _config.MaxContextMessages, cancellationToken);
        }

        var messages = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Message!)
            .ToList();
        if (messages.Count == 0) return [];

        // docs/plans/compaction-tool-history.md §3.3: a legacy (pre-§2) checkpoint
        // may have set RetainedFromSequence in the MIDDLE of a tool exchange —
        // the assistant call summarized, its result(s) retained — so the tail
        // leads with an orphan result. Recover the owning call from durable
        // history when it is still there, so the exchange is whole instead of
        // degrading to a recovery note. Only the checkpoint path (the tail starts
        // at a boundary); a no-checkpoint recent read has no such boundary.
        if (payload is not null && payload.RetainedFromSequence > 0)
            messages = await RecoverBoundaryCallsAsync(
                store, sessionId, messages, payload.RetainedFromSequence, cancellationToken);

        // PLAN §46 / docs/plans/compaction-tool-history.md §3: a run that died
        // mid-batch leaves tool calls without results; the provider rejects such
        // history. Normalize it with the one shared policy before it is sent to a
        // model; an irreparable transcript is a local validation failure (the
        // runner fails the run rather than shipping a broken transcript).
        var sanitized = TranscriptSanitizer.Normalize(messages);
        if (sanitized.Report.ValidationFailure is { } validationFailure)
            throw new InvalidOperationException($"transcript cannot be repaired: {validationFailure}");
        if (sanitized.Report.AnyRepairs)
            LogRepairs(sanitized.Report);
        messages = sanitized.Messages.ToList();

        if (payload is null)
            return messages; // no compaction yet → recent history

        // The read was already scoped to the retained tail (seq >=
        // RetainedFromSequence), so the repaired messages ARE the tail.
        return BuildActiveContext(messages, payload.Summary);
    }


    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Walk backwards from the newest message, accumulating estimated tokens,
    /// until the keep budget is reached; return the seq of the first message to
    /// retain (everything at or after that seq is kept intact, PLAN §32).
    ///
    /// Batch-aware (docs/plans/compaction-tool-history.md §2.2): the token
    /// candidate may land INSIDE a tool exchange (its assistant call is kept
    /// but its result is summarized, or vice-versa). A complete exchange — the
    /// assistant call message plus its contiguous matching result messages — is
    /// an INDIVISIBLE retention unit, so the boundary is moved backward to the
    /// exchange's start whenever it would split one. The keep-recent token target
    /// is soft; transcript validity (a whole exchange on one side of every cut)
    /// takes precedence.
    /// </summary>
    private static int ChooseRetainedFrom(IReadOnlyList<SessionEntry> entries, int keepRecentTokens)
    {
        var seqMessages = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null && e.Sequence > 0)
            .OrderByDescending(e => e.Sequence)
            .ToList();
        if (seqMessages.Count == 0) return 1;

        int budget = keepRecentTokens, running = 0;
        int retainedFrom = seqMessages[0].Sequence;
        foreach (var e in seqMessages)
        {
            retainedFrom = e.Sequence;
            running += TokenEstimator.Estimate(e.Message!);
            if (running >= budget) break;
        }

        // Never let the retained tail straddle a tool exchange: if the candidate
        // split one, step the boundary back to that exchange's first message so
        // the whole call/result batch is retained together.
        var splitting = TranscriptExchanges.Splitting(TranscriptExchanges.Group(entries), retainedFrom);
        if (splitting is not null)
            retainedFrom = splitting.Headless ? splitting.FirstResultSequence : splitting.CallSequence;

        return retainedFrom;
    }

    private static List<AgentMessage> RetainedMessages(IReadOnlyList<SessionEntry> entries, int boundary)
        => entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null && e.Sequence >= boundary)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Message!)
            .ToList();

    private static int FirstMessageSeqBefore(IReadOnlyList<SessionEntry> entries, int boundary)
    {
        var before = entries.Where(e => e.Kind == EntryKind.Message && e.Sequence > 0 && e.Sequence < boundary).ToList();
        return before.Count == 0 ? 0 : before.Max(e => e.Sequence);
    }

    /// <summary>
    /// docs/plans/compaction-tool-history.md §4.1: read the COMPLETE active range
    /// after a sequence. <c>ReadAfterAsync</c> is an ASCENDING LIMIT, so a single
    /// bounded call would silently replace current history with its oldest bounded
    /// portion and DROP the newest entries — exactly the ones compaction (and the
    /// retained tail) needs. Page forward until a short page signals the end.
    /// </summary>
    private async Task<IReadOnlyList<SessionEntry>> ReadFullActiveRangeAsync(
        ISessionStore store, string sessionId, int afterSequence, CancellationToken ct)
    {
        var all = new List<SessionEntry>();
        var pageSize = Math.Max(256, _config.MaxContextMessages);
        var cursor = afterSequence;
        while (true)
        {
            var page = await store.ReadAfterAsync(sessionId, cursor, pageSize, ct);
            if (page.Count == 0) break;
            all.AddRange(page);
            if (page.Count < pageSize) break;           // short page → reached the end
            var next = page[^1].Sequence;
            if (next <= cursor) break;                  // non-advancing cursor (safety)
            cursor = next;
        }
        return all;
    }

    /// <summary>
    /// docs/plans/compaction-tool-history.md §3.3: for legacy damaged history,
    /// recover a boundary call from durable history when possible. An old
    /// (pre-§2) checkpoint may have set RetainedFromSequence in the MIDDLE of a
    /// tool exchange — the assistant call landed in the summarized region and its
    /// result(s) in the retained tail — so the reconstructed tail leads with an
    /// orphan result whose call id is absent from the tail. When that call is
    /// still in durable history just before the boundary, re-include it (filtered
    /// to the recovered call parts) so the exchange is whole again instead of
    /// degrading to a recovery note.
    ///
    /// Bounded and safe:
    /// - Only the LEADING run of tool messages at the very start of the tail is
    ///   inspected — an orphan can only exist before the first assistant-with-
    ///   calls message, which re-opens a valid exchange.
    /// - The look-back is bounded; if the owning call is not found, the tail is
    ///   returned unchanged and the shared sanitizer degrades the orphan to a
    ///   note (the §3 fallback). No assistant call is ever manufactured.
    /// - The recovered assistant message keeps only the call parts that match
    ///   the leading orphans (original order preserved); its other, summarized
    ///   calls are dropped so they are never faked into synthetic interrupts.
    ///   §3.4: a recovered call may overlap the checkpoint's summary — a
    ///   documented, bounded compatibility behavior that never re-executes.
    /// - A clean checkpoint (no leading orphan) returns the input unchanged.
    /// </summary>
    private async Task<List<AgentMessage>> RecoverBoundaryCallsAsync(
        ISessionStore store, string sessionId, List<AgentMessage> messages,
        int retainedFrom, CancellationToken ct)
    {
        // Call ids issued by any assistant message within the tail — a result is
        // only an orphan when its call id is not issued here.
        var issuedInTail = new HashSet<string>(
            messages.Where(m => m.Role == MessageRole.Assistant)
                    .SelectMany(m => m.Parts.OfType<ToolCallPart>())
                    .Where(c => !string.IsNullOrEmpty(c.Id))
                    .Select(c => c.Id));

        // Leading orphan result call ids: the leading run of tool messages at the
        // very start of the tail, whose result call id is not issued within it.
        var leading = new List<string>();
        foreach (var m in messages)
        {
            if (m.Role != MessageRole.Tool) break;
            foreach (var part in m.Parts.OfType<ToolResultPart>())
                if (!string.IsNullOrEmpty(part.ToolCallId) && !issuedInTail.Contains(part.ToolCallId))
                    leading.Add(part.ToolCallId);
        }
        if (leading.Count == 0) return messages;

        // Bounded look-back: the messages just before the retained boundary.
        const int Lookback = 16;
        var before = (await store.ReadBeforeAsync(sessionId, retainedFrom, Lookback, ct))
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null)
            .Select(e => e.Message!)
            .ToList();

        // The assistant message(s) whose calls own the leading orphans — the
        // boundary fell between them and their retained results. Reconstruct
        // each, keeping its non-call parts plus ONLY the recovered call parts.
        var recovered = new List<AgentMessage>();
        foreach (var a in before)
        {
            if (a.Role != MessageRole.Assistant) continue;
            var ownsAnOrphan = a.Parts.OfType<ToolCallPart>()
                .Any(c => !string.IsNullOrEmpty(c.Id) && leading.Contains(c.Id));
            if (!ownsAnOrphan) continue;
            var parts = a.Parts
                .Where(p => p is not ToolCallPart || leading.Contains(((ToolCallPart)p).Id))
                .ToList();
            recovered.Add(new AgentMessage(
                MessageIdentity.DeterministicId("boundary", a.Id),
                MessageRole.Assistant, parts, a.CreatedAt));
        }
        if (recovered.Count == 0) return messages; // call not in the bounded window → the sanitizer notes it

        _ctx.Log.Information(
            $"AutoCompact: recovered {recovered.Count} boundary call(s) into session {sessionId} " +
            $"(orphan result(s) at the retainedFrom={retainedFrom} boundary); overlap with the " +
            $"prior summary tolerated, no re-execution.");

        var combined = new List<AgentMessage>(recovered.Count + messages.Count);
        combined.AddRange(recovered);
        combined.AddRange(messages);
        return combined;
    }

    /// <summary>
    /// docs/plans/compaction-tool-history.md §3.6: log repair category and counts
    /// with session identity — but never dump full tool output (the recovery note
    /// already bounds any preserved output). Clean transcripts produce no noise.
    /// </summary>
    private void LogRepairs(TranscriptRepairReport r) =>
        _ctx.Log.Information(
            $"AutoCompact: transcript repaired — synthetic={r.SyntheticResults} " +
            $"orphans={r.OrphanResults} duplicates={r.DuplicateResults} misplaced={r.MisplacedResults}");

    private static List<AgentMessage> BuildActiveContext(List<AgentMessage> retained, string summary)
    {
        // astra-1 A (message identity): the summary message's id is derived from
        // the summary CONTENT (deterministic), not a fresh GUID — reconstructing
        // the same persisted checkpoint across runs must yield the same message
        // identity so provider fingerprints stay stable (legitimate cache reuse).
        var summaryBody = "Here is a summary of the earlier part of this conversation (auto-generated). "
            + "Treat the work described below as already done:\n\n" + summary;
        var summaryMsg = new AgentMessage(
            MessageIdentity.DeterministicId("summary", summaryBody), MessageRole.System,
            [new TextPart(summaryBody)],
            DateTimeOffset.UtcNow);
        var active = new List<AgentMessage>(retained.Count + 1) { summaryMsg };
        active.AddRange(retained);
        return active;
    }

    private async Task<string> SummarizeAsync(
        IModelProvider provider, string modelId, List<AgentMessage> toSummarize, CancellationToken ct)
    {
        var conversation = FormatConversation(toSummarize);
        var system = new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.System,
            [new TextPart(SummaryInstruction)], DateTimeOffset.UtcNow);
        var user = new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.User,
            [new TextPart(conversation)], DateTimeOffset.UtcNow);

        string best = string.Empty;
        await foreach (var ev in provider.RunAsync(new ModelRequest
        {
            ModelId = modelId,
            Messages = [system, user],
            Tools = [],           // tools disabled (PLAN §33)
            Temperature = 0.3f,
        }, ct))
        {
            if (ev is ModelCompleted mc)
                best = string.Join("\n", mc.Message.Parts.OfType<TextPart>().Select(p => p.Text)).Trim();
            else if (ev is ModelFailed mf)
                throw new InvalidOperationException($"Compaction summarization failed: {mf.Error}");
        }
        return string.IsNullOrWhiteSpace(best) ? "(empty summary)" : best;
    }

    private static string FormatConversation(IReadOnlyList<AgentMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var role = m.Role switch
            {
                MessageRole.System => "system",
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                _ => "tool",
            };
            sb.Append(role).Append(": ");
            foreach (var part in m.Parts)
            {
                switch (part)
                {
                    case TextPart t: sb.Append(t.Text); break;
                    case ThinkingPart t: sb.Append("[reasoning] ").Append(t.Text); break;
                    case ToolCallPart c: sb.Append($"[tool call {c.Name}]").Append(c.Arguments.GetRawText()); break;
                    case ToolResultPart r: sb.Append($"[tool result {r.ToolName}]")
                        .Append(string.Join(" ", r.Parts.OfType<TextPart>().Select(p => p.Text))); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private IModelProvider? ResolveProvider() => TryResolve<IModelProvider>("provider");
    private IModelCatalog? ResolveCatalog() => TryResolve<IModelCatalog>("catalog");
    private ISessionStore? ResolveStore() => TryResolve<ISessionStore>("sessions");

    private T? TryResolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }

    }

    private static readonly JsonSerializerOptions WireOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string SummaryInstruction =
        "You are summarizing a conversation for context compaction. " +
        "Produce a concise, faithful summary that preserves the current coding state. " +
        "Use exactly these section headers and fill them in (omit a section only if truly nothing to say):\n" +
        "## Goal\n" +
        "## User instructions\n" +
        "## Important decisions\n" +
        "## Work completed\n" +
        "## Current implementation state\n" +
        "## Errors/issues\n" +
        "## Files read\n" +
        "## Files modified\n" +
        "## Commands/results\n" +
        "## Next actions\n" +
        "Be specific about file paths, function names, and decisions. Do not invent details.";
}

/// <summary>
/// The reloadable AutoCompact plugin (PLAN §32/§33). Registers the
/// <see cref="ICompaction"/> service under id "compaction".
/// </summary>
public sealed class AutoCompactPlugin : INetPiPlugin
{
    private ICompaction? _service;

    public PluginInfo Info { get; } = new("netPI.AutoCompact", "Context Auto-Compaction", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _service = new AutoCompactService(context, AutoCompactConfig.FromJson(context.OwnConfig));
        context.Services.Register<ICompaction>("compaction", _service);
        // PLAN §37: register the /compact slash command — it belongs to this
        // plugin (AutoCompact), not the Web surface.
        context.Commands.Register(new CommandDefinition("/compact", "Compact context now"));
        context.Log.Information("AutoCompact ready");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _service = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
