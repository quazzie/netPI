using System.Diagnostics;
using System.Text;

// NOTE: mirrored in NetPI.Providers.Anthropic/Common (plugins cannot share code). Keep both copies in sync.
namespace NetPI.Providers.AiProxy;

/// <summary>
/// Accumulates streamed deltas into message parts (in emission order) and buffers the
/// <see cref="ModelStreamEvent"/>s to yield. Parsers call it synchronously; the provider drains <see cref="Pending"/>.
/// </summary>
internal sealed class MessageAssembler
{
    internal sealed class Slot(MessagePart part)
    {
        public MessagePart Part { get; } = part;
        public StringBuilder Text { get; } = new();
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long LastDelta { get; set; } = Stopwatch.GetTimestamp();
    }

    private readonly List<Slot> _slots = [];
    private readonly Dictionary<MessagePart, Slot> _byPart = new(ReferenceEqualityComparer.Instance);
    private readonly List<ModelStreamEvent> _pending = [];
    private Usage? _lastEmittedUsage;

    public Usage? Usage { get; private set; }
    public string? StopReason { get; set; }
    public int ToolCallCount { get; private set; }
    public bool HasContent => _slots.Count > 0;

    public List<ModelStreamEvent> Pending => _pending;

    public IEnumerable<ModelStreamEvent> Drain()
    {
        if (_pending.Count == 0) return [];
        var copy = _pending.ToArray();
        _pending.Clear();
        return copy;
    }

    private Slot? Last => _slots.Count > 0 ? _slots[^1] : null;

    private Slot Add(MessagePart part)
    {
        var s = new Slot(part);
        _slots.Add(s);
        _byPart[part] = s;
        return s;
    }

    public string CurrentText(MessagePart part) => _byPart.TryGetValue(part, out var s) ? s.Text.ToString() : "";

    // ---------------------------------------------------------------- thinking

    public ThinkingPart BeginThinking() => (ThinkingPart)Add(new ThinkingPart()).Part;

    /// <summary>Append to the trailing thinking part (or start a new one).</summary>
    public void AddThinking(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var part = Last?.Part as ThinkingPart ?? BeginThinking();
        AppendThinking(part, text);
    }

    public void AppendThinking(ThinkingPart part, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var s = _byPart[part];
        s.Text.Append(text);
        s.LastDelta = Stopwatch.GetTimestamp();
        _pending.Add(new ThinkingDelta(text));
    }

    // ---------------------------------------------------------------- text

    public TextPart BeginText() => (TextPart)Add(new TextPart()).Part;

    /// <summary>The trailing text part, or a new one (consecutive text blocks merge).</summary>
    public TextPart TextTarget() => Last?.Part as TextPart ?? BeginText();

    /// <summary>Append to the trailing text part (or start a new one).</summary>
    public void AddText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AppendText(TextTarget(), text);
    }

    public void AppendText(TextPart part, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var s = _byPart[part];
        s.Text.Append(text);
        s.LastDelta = Stopwatch.GetTimestamp();
        _pending.Add(new TextDelta(text));
    }

    /// <summary>
    /// For servers that only send "done" events (or send them in addition to deltas): emit whatever part of
    /// <paramref name="full"/> has not been streamed yet.
    /// </summary>
    public void CatchUp(MessagePart part, string? full)
    {
        if (string.IsNullOrEmpty(full)) return;
        var current = CurrentText(part);
        if (full.Length <= current.Length || !full.StartsWith(current, StringComparison.Ordinal)) return;
        var rest = full[current.Length..];
        switch (part)
        {
            case TextPart t: AppendText(t, rest); break;
            case ThinkingPart th: AppendThinking(th, rest); break;
            case ToolCallPart c: AppendToolArgs(c, rest); break;
        }
    }

    // ---------------------------------------------------------------- tool calls

    public ToolCallPart StartToolCall(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id)) id = "call_" + Ids.Short(24);
        var part = new ToolCallPart { Id = id, Name = name ?? "", Arguments = "" };
        Add(part);
        ToolCallCount++;
        _pending.Add(new ToolCallStarted(part.Id, part.Name));
        return part;
    }

    public void AppendToolArgs(ToolCallPart part, string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        var s = _byPart[part];
        s.Text.Append(delta);
        s.LastDelta = Stopwatch.GetTimestamp();
        _pending.Add(new ToolCallArgsDelta(part.Id, delta));
    }

    // ---------------------------------------------------------------- usage / completion

    public void SetUsage(Usage usage)
    {
        Usage = usage;
        if (_lastEmittedUsage is { } l && l.InputTokens == usage.InputTokens && l.OutputTokens == usage.OutputTokens
            && l.CacheReadTokens == usage.CacheReadTokens && l.CacheWriteTokens == usage.CacheWriteTokens
            && l.ReasoningTokens == usage.ReasoningTokens) return;
        _lastEmittedUsage = Clone(usage);
        _pending.Add(new UsageUpdate(Clone(usage)));
    }

    public static Usage Clone(Usage u) => new()
    {
        InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CacheReadTokens = u.CacheReadTokens,
        CacheWriteTokens = u.CacheWriteTokens, ReasoningTokens = u.ReasoningTokens,
    };

    /// <summary>Build the final assistant message.</summary>
    public ChatMessage Build(string provider, string model, string? sessionId, long durationMs)
    {
        var parts = new List<MessagePart>(_slots.Count);
        foreach (var s in _slots)
        {
            switch (s.Part)
            {
                case TextPart t:
                    t.Text = s.Text.ToString();
                    if (t.Text.Length > 0) parts.Add(t);
                    break;
                case ThinkingPart th:
                    th.Text = s.Text.ToString();
                    if (th.Text.Length > 0 || th.Signature is not null || th.Redacted is not null || th.ProviderData is not null)
                    {
                        if (th.Text.Length > 0)
                            th.DurationMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(s.Started, s.LastDelta).TotalMilliseconds);
                        parts.Add(th);
                    }
                    break;
                case ToolCallPart c:
                    var args = s.Text.ToString();
                    c.Arguments = string.IsNullOrWhiteSpace(args) ? "{}" : args;
                    parts.Add(c);
                    break;
                default:
                    parts.Add(s.Part);
                    break;
            }
        }

        var stop = StopReason ?? (ToolCallCount > 0 ? "tool_use" : "stop");
        return new ChatMessage
        {
            Role = MessageRole.Assistant,
            SessionId = sessionId ?? "",
            Parts = parts,
            Provider = provider,
            Model = model,
            Usage = Usage is null ? null : Clone(Usage),
            StopReason = stop,
            DurationMs = durationMs,
        };
    }
}
