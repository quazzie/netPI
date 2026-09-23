using System.Text;

namespace NetPI.Abstractions;

/// <summary>
/// Repairs persisted transcripts before they are sent to a provider (PLAN §46;
/// docs/plans/compaction-tool-history.md §3).
///
/// The scan is BATCH-LOCAL and ORDERED, not a global id set: each tool result is
/// matched only against the outstanding calls of its IMMEDIATELY PRECEDING
/// exchange (an assistant message with tool calls and its contiguous result
/// messages — <see cref="TranscriptExchanges"/>). A result is never accepted
/// because that id occurs somewhere else in the transcript.
///
/// Repairs (all deterministic and idempotent — running the sanitizer twice
/// yields identical output and stable fingerprints):
/// <list type="bullet">
/// <item><b>synthetic interrupted result</b> — an assistant call with no
///   result gets a synthetic <c>IsError</c> result inserted directly after the
///   assistant turn (the wires require the tool message to follow its call).
///   The text keeps the interrupted semantics: it never claims the tool did
///   not execute, and it never re-executes it.</item>
/// <item><b>historical-recovery note</b> — an irrecoverable ORPHAN (its call id
///   appears nowhere), DUPLICATE (already consumed once) or MISPLACED (its call
///   belongs to a different exchange) structured result is REMOVED from the
///   model input; its output is preserved as a clearly labeled, deterministic
///   user message ("outside the tool batch") so no information is silently
///   lost. No assistant call is ever manufactured.</item>
/// </list>
///
/// An assistant tool call with an EMPTY id cannot be repaired without guessing:
/// it yields a validation failure in the <see cref="TranscriptRepairReport"/>
/// (actionable, local — the caller decides how to surface it).
///
/// Original stored messages are never rewritten; repairs exist only in the
/// returned in-memory list (PLAN §46 / plan §3.4).
/// </summary>
public static class TranscriptSanitizer
{
    public const string InterruptedResultText =
        "Tool execution was interrupted (the run was cancelled or the host shut down). " +
        "The command may or may not have completed — re-check any side effects before retrying.";

    /// <summary>Prefix marking a sanitizer-produced historical-recovery note message.</summary>
    public const string RecoveryNotePrefix = "[historical-recovery]";

    /// <summary>Bounded length of output preserved in a recovery note (plan §3.6: no full dumps).</summary>
    public const int RecoveryNoteOutputLimit = 2_000;

    /// <summary>The result of one normalization pass over a complete transcript.</summary>
    public sealed record SanitizeResult(
        IReadOnlyList<AgentMessage> Messages,
        TranscriptRepairReport Report,
        /// <summary>True when the transcript needed repair (a new list was built);
        /// false → <see cref="Messages"/> is the input instance, unchanged.</summary>
        bool Changed);

    /// <summary>
    /// The one shared normalization policy (plan §3.5): applied to automatic
    /// compaction returns, manual compaction, checkpoint reconstruction and the
    /// runner's direct-store fallback. Operates on the COMPLETE transcript
    /// (never a chained wire delta — a call may legitimately live in server-held
    /// history). Clean transcripts come back unchanged (same list instance,
    /// zero report counts, no repair noise).
    /// </summary>
    public static SanitizeResult Normalize(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0)
            return new SanitizeResult(messages, new TranscriptRepairReport(0, 0, 0, 0, null), false);

        var report = new RepairReport();
        var outMessages = new List<AgentMessage>(messages.Count);
        var changed = false;

        // The current exchange: outstanding calls of the immediately preceding
        // assistant-with-calls message (each consumed at most once), plus the
        // output index where the synthetic results for this exchange land
        // (directly after the assistant message — the wires require tool
        // adjacency, so dangling results are inserted BEFORE any following
        // tool messages of the same batch).
        List<OutstandingCall>? outstanding = null;
        int? assistantOutIndex = null;
        string? assistantMessageId = null;

        void CloseExchange()
        {
            if (outstanding is null) return;
            var dangling = outstanding.Where(o => !o.Consumed).ToList();
            if (dangling.Count > 0)
            {
                var synthetics = dangling
                    .Select(o => (MessagePart)new ToolResultPart(
                        o.Id, o.Name, [new TextPart(InterruptedResultText)], IsError: true))
                    .ToList();
                // DIRECTLY AFTER the assistant message (the wires require the tool
                // message to follow its call) — and BEFORE any following tool
                // messages of the same batch, which already sit at higher indexes.
                outMessages.Insert(assistantOutIndex is { } idx ? idx + 1 : outMessages.Count,
                    new AgentMessage(
                        (assistantMessageId ?? "orphan") + "-interrupted", MessageRole.Tool,
                        synthetics, DateTimeOffset.UtcNow));
                report.Synthetic += dangling.Count;
                changed = true;
            }
            outstanding = null;
            assistantOutIndex = null;
            assistantMessageId = null;
        }

        foreach (var m in messages)
        {
            var calls = m.Role == MessageRole.Assistant
                ? m.Parts.OfType<ToolCallPart>().ToList()
                : null;

            if (calls is not null)
            {
                CloseExchange();
                outMessages.Add(m);
                assistantOutIndex = outMessages.Count - 1;
                assistantMessageId = m.Id;
                if (calls.Count > 0)
                {
                    outstanding = [];
                    foreach (var c in calls)
                    {
                        if (string.IsNullOrEmpty(c.Id))
                        {
                            report.FailOnce(
                                $"assistant message '{m.Id}' carries a tool call with an empty id — the transcript cannot be repaired without guessing");
                            continue; // empty ids cannot match any result; not an outstanding call
                        }
                        outstanding.Add(new OutstandingCall(c.Id, c.Name));
                        report.IssuedBefore.Add(c.Id);
                    }
                }
                continue;
            }

            if (m.Role == MessageRole.Tool)
            {
                // Match each result against the IMMEDIATELY PRECEDING exchange only.
                var keptParts = new List<MessagePart>(m.Parts.Count);
                var notes = new List<AgentMessage>();
                var removed = 0;
                foreach (var part in m.Parts)
                {
                    if (part is not ToolResultPart tr)
                    {
                        keptParts.Add(part);
                        continue;
                    }
                    var oc = !string.IsNullOrEmpty(tr.ToolCallId)
                        ? outstanding?.FirstOrDefault(o => o.Id == tr.ToolCallId && !o.Consumed)
                        : null;
                    if (oc is not null)
                    {
                        oc.Consumed = true;
                        keptParts.Add(part); // valid: keep identity, text and position
                    }
                    else
                    {
                        // Invalid: classify, remove the structured result, preserve
                        // the output as a labeled note outside the tool batch.
                        string category;
                        if (string.IsNullOrEmpty(tr.ToolCallId))
                        { category = "orphan with an empty call id"; report.Orphans++; }
                        else if (outstanding?.Any(o => o.Id == tr.ToolCallId && o.Consumed) == true)
                        { category = "duplicate (already consumed once)"; report.Duplicates++; }
                        else if (report.IssuedBefore.Contains(tr.ToolCallId))
                        { category = "misplaced (call belongs to a different exchange)"; report.Misplaced++; }
                        else
                        { category = "orphan (call id not found in the transcript)"; report.Orphans++; }
                        removed++;
                        notes.Add(new AgentMessage(
                            MessageIdentity.DeterministicId("recovery",
                                $"{m.Id}|{tr.ToolCallId}|{category.Split(' ')[0]}"),
                            MessageRole.User,
                            [new TextPart(RecoveryNoteText(category, tr))],
                            m.CreatedAt));
                    }
                }

                if (removed == 0)
                    outMessages.Add(m); // every part was kept as-is → message unchanged
                else
                {
                    if (keptParts.Count > 0)
                        outMessages.Add(new AgentMessage(m.Id, m.Role, keptParts, m.CreatedAt));
                    // else: a tool message whose parts were all invalid is dropped
                    outMessages.AddRange(notes);
                    changed = true;
                }
                continue;
            }

            // A conversation message (user/system) closes the exchange.
            CloseExchange();
            outMessages.Add(m);
        }

        CloseExchange();

        if (!changed && !report.Any)
            return new SanitizeResult(messages, report.ToReport(), false);
        return new SanitizeResult(outMessages, report.ToReport(), changed || report.Any);
    }

    private sealed class OutstandingCall(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool Consumed;
    }

    private sealed class RepairReport
    {
        public int Synthetic;
        public int Orphans;
        public int Duplicates;
        public int Misplaced;
        public string? Failure;
        public readonly HashSet<string> IssuedBefore = new(StringComparer.Ordinal);
        public void FailOnce(string message) => Failure ??= message;
        public bool Any => Synthetic > 0 || Orphans > 0 || Duplicates > 0 || Misplaced > 0;
        public TranscriptRepairReport ToReport() => new(Synthetic, Orphans, Duplicates, Misplaced, Failure);
    }

    private static string RecoveryNoteText(string category, ToolResultPart tr)
    {
        var output = string.Join("\n", tr.Parts.OfType<TextPart>().Select(p => p.Text));
        if (output.Length > RecoveryNoteOutputLimit)
            output = output[..RecoveryNoteOutputLimit] + "…";
        var sb = new StringBuilder(RecoveryNotePrefix);
        sb.Append(" a stored tool result").Append(category).Append(" was removed from the model input.");
        sb.Append(" Do not assume the tool did not execute — verify side effects before retrying.");
        if (output.Length > 0) sb.Append(" Preserved output: ").Append(output);
        return sb.ToString();
    }
}

/// <summary>
/// What a sanitizer pass repaired, with counts per category and (when the
/// transcript is irreparably damaged) an actionable validation failure.
/// Counts are per message part; <see cref="SyntheticResults"/> counts calls
/// that received a synthetic interrupted result.
/// </summary>
public sealed record TranscriptRepairReport(
    int SyntheticResults,
    int OrphanResults,
    int DuplicateResults,
    int MisplacedResults,
    /// <summary>Non-null when the transcript could not be repaired without guessing
    /// (e.g. an assistant tool call with an empty id). Callers must surface this as
    /// a local validation failure — never send the transcript to a provider.</summary>
    string? ValidationFailure)
{
    public bool AnyRepairs => ValidationFailure is not null
        || SyntheticResults > 0 || OrphanResults > 0 || DuplicateResults > 0 || MisplacedResults > 0;
}
