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

    internal void UpdateConfig(AutoCompactConfig config) => _config = config;

    public async ValueTask<CompactionResult> CompactAsync(
        CompactionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled) return new CompactionResult(null, null);

        var store = ResolveStore();
        var catalog = ResolveCatalog();
        var provider = ResolveProvider();
        if (store is null || provider is null) return new CompactionResult(null, null);

        var entries = await store.ReadAsync(request.SessionId, 0, _config.MaxContextMessages, cancellationToken);

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

        var toSummarize = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null
                        && e.Sequence > 0 && e.Sequence < retainedFrom)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Message!)
            .ToList();
        if (toSummarize.Count == 0) return new CompactionResult(null, null);

        int summarizedThrough = FirstMessageSeqBefore(entries, retainedFrom);

        // ---- summarize (tools disabled, PLAN §33) -------------------------
        string summary = await SummarizeAsync(provider, request.ModelId, toSummarize, cancellationToken);

        // ---- reconstruct active context (PLAN §31/§33) ---------------------
        var activeContext = BuildActiveContext(retained, summary);

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
        var compactionEntry = new SessionEntry(
            Guid.NewGuid().ToString("n"), request.SessionId, EntryKind.Compaction, null,
            JsonSerializer.SerializeToElement(payload, WireOpts), DateTimeOffset.UtcNow,
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
        var entries = await store.ReadAsync(sessionId, 0, _config.MaxContextMessages, cancellationToken);

        SessionEntry? latestCompaction = null;
        int latestSeq = 0;
        foreach (var e in entries)
            if (e.Kind == EntryKind.Compaction && e.Sequence > latestSeq) { latestSeq = e.Sequence; latestCompaction = e; }

        var messages = entries
            .Where(e => e.Kind == EntryKind.Message && e.Message is not null)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Message!)
            .ToList();
        if (messages.Count == 0) return [];

        // PLAN §46: a run that died mid-batch leaves tool calls without results;
        // the provider rejects such history ("function_call_output must contain a
        // non-empty call_id"). Repair it before it is ever sent to a model.
        messages = TranscriptSanitizer.Sanitize(messages).ToList();

        if (latestCompaction?.Payload is not { } payload)
            return messages; // no compaction yet → full history

        var pl = JsonSerializer.Deserialize<CompactionEntryPayload>(payload.GetRawText(), WireOpts)
            ?? new CompactionEntryPayload();

        if (pl.RetainedFromSequence <= 0)
            return messages;

        var retained = RetainedMessages(entries, pl.RetainedFromSequence);
        return BuildActiveContext(retained, pl.Summary);
    }


    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Walk backwards from the newest message, accumulating estimated tokens,
    /// until the keep budget is reached; return the seq of the first message to
    /// retain (everything at or after that seq is kept intact, PLAN §32).
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

    private static List<AgentMessage> BuildActiveContext(List<AgentMessage> retained, string summary)
    {
        var summaryMsg = new AgentMessage(
            Guid.NewGuid().ToString("n"), MessageRole.System,
            [new TextPart(
                "Here is a summary of the earlier part of this conversation (auto-generated). " +
                "Treat the work described below as already done:\n\n" + summary)],
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
