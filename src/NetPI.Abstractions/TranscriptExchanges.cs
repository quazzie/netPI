using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// One tool exchange (docs/plans/compaction-tool-history.md §2): an assistant
/// message carrying tool calls plus its contiguous matching result messages,
/// as an indivisible unit of the persisted transcript. A batch is several
/// calls/results per message and several result messages; non-message storage
/// entries (compaction checkpoints, metadata, project-context) do not break
/// an exchange's contiguity.
/// </summary>
public sealed record ToolExchange(
    /// <summary>Sequence of the assistant call message (0 = the call message is NOT
    /// inside the analyzed window — the window began mid-batch; see <see cref="Headless"/>).</summary>
    int CallSequence,
    /// <summary>Sequence of the first result message (0 = no result message was observed).</summary>
    int FirstResultSequence,
    /// <summary>Sequence of the last result message in the contiguous run (0 = none observed).</summary>
    int LastResultSequence,
    /// <summary>Call ids issued by the assistant message (empty ids excluded;
    /// empty list for a headless exchange).</summary>
    IReadOnlyList<string> CallIds,
    /// <summary>True when every call id is matched by a result within the exchange
    /// and at least one result was observed. A headless exchange is never Complete
    /// (its call set is unknown).</summary>
    bool Complete)
{
    /// <summary>True when the call message is outside the analyzed window (window began mid-batch).</summary>
    public bool Headless => CallSequence == 0;

    /// <summary>
    /// True when a retained-tail boundary <c>B</c> (entries with sequence >= B are
    /// retained) cuts INSIDE this exchange: some of its messages would be retained
    /// and some summarized. A legal boundary is one where no exchange straddles it —
    /// either the whole exchange is retained (B <= CallSequence) or summarized
    /// (B > LastResultSequence).
    /// </summary>
    public bool Straddles(int boundary)
    {
        var start = Headless ? FirstResultSequence : CallSequence;
        if (start <= 0 || LastResultSequence <= 0) return false;
        return start < boundary && boundary <= LastResultSequence;
    }
}

/// <summary>
/// Groups persisted transcript entries into tool exchanges (PLAN §31–§33;
/// docs/plans/compaction-tool-history.md §2). The boundary computation (which
/// side of an exchange a retained-tail cut falls) and the transcript sanitizer
/// share this definition so a "legal cut" means the same thing everywhere.
/// </summary>
public static class TranscriptExchanges
{
    private sealed class Accumulator
    {
        public int CallSequence;
        public int FirstResultSequence;
        public int LastResultSequence;
        public List<string> CallIds = [];
        public HashSet<string> Resolved = [];
        public bool Headless;
    }

    /// <summary>
    /// Group <paramref name="entries"/> (any order; they are sorted by sequence)
    /// into exchanges. Non-message entries are skipped without breaking
    /// contiguity. A Tool message with results whose ids are not issued by the
    /// current assistant exchange starts a new HEADLESS exchange (the call
    /// message lies before the window, or the history is damaged).
    /// </summary>
    public static IReadOnlyList<ToolExchange> Group(IReadOnlyList<SessionEntry> entries)
    {
        var result = new List<ToolExchange>();
        var cur = new Accumulator();
        var hasExchange = false;

        void Close()
        {
            if (!hasExchange) return;
            var complete = !cur.Headless
                && cur.LastResultSequence > 0
                && (cur.CallIds.Count == 0 || cur.Resolved.Count == cur.CallIds.Count);
            result.Add(new ToolExchange(
                cur.CallSequence,
                cur.FirstResultSequence,
                cur.LastResultSequence,
                cur.CallIds,
                complete));
            hasExchange = false;
        }

        foreach (var e in entries.OrderBy(x => x.Sequence))
        {
            if (e.Kind != EntryKind.Message || e.Message is null) continue;
            var m = e.Message;

            if (m.Role == MessageRole.Assistant)
            {
                var calls = m.Parts.OfType<ToolCallPart>()
                    .Where(c => !string.IsNullOrEmpty(c.Id))
                    .Select(c => c.Id)
                    .Distinct()
                    .ToList();
                // A new assistant turn CLOSES the previous exchange (a tool result
                // must belong to the IMMEDIATELY PRECEDING exchange) and starts the
                // new one. An assistant message with no (valid) calls closes without
                // opening.
                Close();
                if (calls.Count > 0)
                {
                    cur = new Accumulator { CallSequence = e.Sequence, CallIds = calls };
                    hasExchange = true;
                }
                continue;
            }

            if (m.Role is MessageRole.User or MessageRole.System)
            {
                Close(); // a conversation message ends the exchange
                continue;
            }

            if (m.Role != MessageRole.Tool) continue;
            var results = m.Parts.OfType<ToolResultPart>()
                .Where(r => !string.IsNullOrEmpty(r.ToolCallId))
                .ToList();

            if (results.Count == 0)
            {
                // A tool message carrying no result parts is part of the contiguous
                // run (damaged/batch shape); it extends the span but resolves nothing.
                if (hasExchange)
                    cur.LastResultSequence = e.Sequence;
                continue;
            }

            if (hasExchange && !cur.Headless && cur.CallIds.Count > 0)
            {
                var valid = results.Where(r => cur.CallIds.Contains(r.ToolCallId)).ToList();
                if (valid.Count > 0)
                {
                    if (cur.FirstResultSequence == 0) cur.FirstResultSequence = e.Sequence;
                    cur.LastResultSequence = e.Sequence;
                    foreach (var r in valid) cur.Resolved.Add(r.ToolCallId);
                    continue;
                }
                // all foreign → headless batch; close the current exchange and fall through
                Close();
            }
            // Headless: results with no visible outstanding call (window began
            // mid-batch or history damaged).
            cur = new Accumulator
            {
                CallSequence = 0,
                Headless = true,
                FirstResultSequence = e.Sequence,
                LastResultSequence = e.Sequence,
            };
            hasExchange = true;
        }

        Close();
        return result;
    }

    /// <summary>
    /// The exchange whose span an interior boundary cut would split (retained
    /// tail = sequences >= <paramref name="boundary"/>), or null when the cut
    /// is legal as-is.
    /// </summary>
    public static ToolExchange? Splitting(IReadOnlyList<ToolExchange> exchanges, int boundary)
    {
        foreach (var e in exchanges)
            if (e.Straddles(boundary)) return e;
        return null;
    }

    /// <summary>
    /// The last exchange in sequence order (the most recent one), or null.
    /// </summary>
    public static ToolExchange? Last(IReadOnlyList<ToolExchange> exchanges)
        => exchanges.Count == 0 ? null : exchanges[^1];
}
