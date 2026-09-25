using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Diagnostics;

/// <summary>
/// What happened lately, kept in memory for <c>diag.*</c>: every model call (the outermost model middleware, so a call's
/// retries are part of it), every tool call, and a journal of the bus events that matter (not the per-token ones, which
/// push everything else out of the host's own buffer within seconds).
/// </summary>
public sealed class Recorder
{
    public const int CallCapacity = 300;
    public const int ToolCapacity = 500;
    public const int JournalCapacity = 3000;

    /// <summary>Events too frequent to keep (every token, every output chunk).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        EventTypes.StreamDelta, EventTypes.StreamTool, EventTypes.ToolOutput, EventTypes.ProcessOutput,
    };

    private readonly IPluginContext _ctx;
    private readonly Lock _gate = new();
    private readonly LinkedList<CallRecord> _calls = new();
    private readonly LinkedList<ToolRecord> _tools = new();
    private readonly Dictionary<string, ToolRecord> _toolsByCall = new(StringComparer.Ordinal);
    private readonly LinkedList<JournalEntry> _journal = new();
    private long _callSeq;

    public Recorder(IPluginContext ctx)
    {
        _ctx = ctx;
        Middleware = new CallMiddleware(this);
    }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public IModelMiddleware Middleware { get; }

    // ---------------------------------------------------------------- model calls

    internal CallRecord Begin(ModelRequest request)
    {
        string? agent = null;
        try { agent = request.SessionId is null ? null : SessionAgent.Of(_ctx.Sessions.GetSession(request.SessionId)); } catch { }
        var record = new CallRecord
        {
            Id = Interlocked.Increment(ref _callSeq),
            StartedAt = DateTimeOffset.UtcNow,
            Provider = request.Model.Provider,
            Model = request.Model.Id,
            Purpose = request.Purpose,
            SessionId = request.SessionId,
            RunId = request.AgentId,
            Agent = agent,
            ReasoningEffort = request.ReasoningEffort,
            Messages = request.Messages.Count,
            Tools = request.Tools.Count,
            SystemPromptChars = request.SystemPrompt?.Length ?? 0,
            InputChars = request.Messages.Sum(m => m.Parts.Sum(PartChars)),
            // the person's latest message, not a harness notice (they travel as user messages too)
            LastUser = Preview(request.Messages.Where(m => m.Role == MessageRole.User)
                .Select(m => m.Parts.OfType<TextPart>().FirstOrDefault()?.Text)
                .LastOrDefault(t => t is not null && !t.TrimStart().StartsWith("<system-notice", StringComparison.Ordinal)), 160),
        };
        lock (_gate)
        {
            _calls.AddFirst(record);
            // finished calls fall out first; running ones stay until they end
            for (var node = _calls.Last; _calls.Count > CallCapacity && node is not null;)
            {
                var prev = node.Previous;
                if (node.Value.State != "running") _calls.Remove(node);
                node = prev;
            }
        }
        return record;
    }

    private static int PartChars(MessagePart p) => p switch
    {
        TextPart t => t.Text.Length,
        ThinkingPart t => t.Text.Length,
        ToolCallPart c => c.Arguments.Length,
        ToolResultPart r => r.Content.Length,
        _ => 0,
    };

    public IReadOnlyList<CallRecord> Calls()
    {
        lock (_gate) return [.. _calls];
    }

    public CallRecord? Call(long id)
    {
        lock (_gate) return _calls.FirstOrDefault(c => c.Id == id);
    }

    private sealed class CallMiddleware(Recorder recorder) : IModelMiddleware
    {
        /// <summary>Outermost: around the budget check and the retries, so one record is one call as its caller saw it.</summary>
        public int Order => -1000;

        public async IAsyncEnumerable<ModelStreamEvent> InvokeAsync(ModelRequest request, ModelCallDelegate next, [EnumeratorCancellation] CancellationToken ct)
        {
            var record = recorder.Begin(request);
            var clock = Stopwatch.StartNew();
            var stream = next(request, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool more;
                    try { more = await stream.MoveNextAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        record.End("cancelled", clock, null);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        record.End("error", clock, ex);
                        throw;
                    }
                    if (!more) break;
                    record.Observe(stream.Current, clock);
                    yield return stream.Current;
                }
                record.End(record.Completed ? "ok" : "incomplete", clock, null);
            }
            finally
            {
                // the caller stopped reading (a steer, an abort): the call ends here
                if (record.State == "running") record.End("cancelled", clock, null);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------- tool calls and the journal

    /// <summary>Called for every bus event (a "*" subscription).</summary>
    public void OnEvent(BusEvent e)
    {
        if (Skipped.Contains(e.Type)) return;
        JsonObject? data = null;
        try { data = NetPiJson.ToNode(e.Data) as JsonObject; } catch { }
        if (e.Type == EventTypes.ToolStart) ToolStarted(e, data);
        else if (e.Type == EventTypes.ToolEnd) ToolEnded(e, data);
        // the chat: on the event, or in its data (agent.status carries it in the agent, message.added in the message)
        var sessionId = e.SessionId ?? Str(data, "sessionId") ?? Str(data?["agent"] as JsonObject, "sessionId") ?? Str(data?["message"] as JsonObject, "sessionId");
        var entry = new JournalEntry(e.Seq, e.Time, e.Type, sessionId, e.Source, Summarize(e.Type, data));
        lock (_gate)
        {
            _journal.AddFirst(entry);
            while (_journal.Count > JournalCapacity) _journal.RemoveLast();
        }
    }

    private void ToolStarted(BusEvent e, JsonObject? d)
    {
        var callId = Str(d, "callId");
        if (callId is null) return;
        var record = new ToolRecord
        {
            CallId = callId,
            Name = Str(d, "name") ?? "?",
            SessionId = e.SessionId ?? Str(d, "sessionId"),
            RunId = Str(d, "agentId"),
            StartedAt = e.Time,
            Arguments = Preview(d?["arguments"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : d?["arguments"]?.ToJsonString(), 300),
        };
        lock (_gate)
        {
            _tools.AddFirst(record);
            _toolsByCall[callId] = record;
            for (var node = _tools.Last; _tools.Count > ToolCapacity && node is not null;)
            {
                var prev = node.Previous;
                if (node.Value.EndedAt is not null) { _tools.Remove(node); _toolsByCall.Remove(node.Value.CallId); }
                node = prev;
            }
        }
    }

    private void ToolEnded(BusEvent e, JsonObject? d)
    {
        var callId = Str(d, "callId");
        if (callId is null) return;
        lock (_gate)
        {
            if (!_toolsByCall.TryGetValue(callId, out var record)) return;
            record.EndedAt = e.Time;
            record.IsError = d?["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            record.DurationMs = Long(d?["durationMs"]) ?? (long)(e.Time - record.StartedAt).TotalMilliseconds;
        }
    }

    public IReadOnlyList<ToolRecord> Tools()
    {
        lock (_gate) return [.. _tools];
    }

    public IReadOnlyList<JournalEntry> Journal()
    {
        lock (_gate) return [.. _journal];
    }

    /// <summary>A short, readable form of an event's data (whole messages and agent lists are too big to keep).</summary>
    internal static JsonNode? Summarize(string type, JsonObject? d)
    {
        if (d is null) return null;
        switch (type)
        {
            case EventTypes.MessageAdded:
            case EventTypes.MessageUpdated:
                if (d["message"] is JsonObject m)
                {
                    var parts = m["parts"] as JsonArray;
                    return new JsonObject
                    {
                        ["role"] = m["role"]?.DeepClone(),
                        ["seq"] = m["seq"]?.DeepClone(),
                        ["kind"] = (m["meta"] as JsonObject)?["kind"]?.DeepClone(),
                        ["stopReason"] = m["stopReason"]?.DeepClone(),
                        ["parts"] = parts is null ? null : string.Join(",", parts.Select(p => (string?)p?["type"])),
                        ["text"] = Preview(string.Join(" ", parts?.Select(p => (string?)p?["text"] ?? (string?)p?["content"]).Where(t => t is not null) ?? []), 160),
                    };
                }
                break;
            case EventTypes.AgentsChanged:
                if (d["agents"] is JsonArray agents)
                    return JsonValue.Create(string.Join(" · ", agents.Select(a => a is JsonObject o
                        ? $"{o["key"]} {o["busy"]}/{o["capacity"]}{(Long(o["queued"]) is > 0 ? $"+{o["queued"]}" : "")}{(o["available"] is JsonValue av && av.TryGetValue<bool>(out var ok) && !ok ? " (" + ((string?)o["unavailable"] ?? "inactive") + ")" : "")}"
                        : "?")));
                break;
            case EventTypes.AgentStatus:
                if (d["agent"] is JsonObject a)
                    return new JsonObject
                    {
                        ["id"] = a["id"]?.DeepClone(), ["name"] = a["name"]?.DeepClone(), ["status"] = a["status"]?.DeepClone(),
                        ["activity"] = a["activity"]?.DeepClone(), ["agent"] = a["agent"]?.DeepClone(), ["model"] = a["model"]?.DeepClone(),
                    };
                break;
            case EventTypes.ToolStart:
                return new JsonObject { ["name"] = d["name"]?.DeepClone(), ["callId"] = d["callId"]?.DeepClone(), ["agentId"] = d["agentId"]?.DeepClone() };
        }
        var json = d.ToJsonString();
        return json.Length <= 400 ? d.DeepClone() : JsonValue.Create(json[..400] + "…");
    }

    /// <summary>A number however it was stored (a JsonValue holding an int doesn't hand itself out as a long).</summary>
    internal static long? Long(JsonNode? n) =>
        n is JsonValue v && long.TryParse(v.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;

    internal static string? Str(JsonObject? d, string key) => d?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string? Preview(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}

/// <summary>One model call as its caller saw it (retries included). Fields change while it runs: read under the lock.</summary>
public sealed class CallRecord
{
    private readonly Lock _gate = new();
    public long Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string? SessionId { get; init; }
    /// <summary>The run (AgentInfo id) that made the call.</summary>
    public string? RunId { get; init; }
    /// <summary>The agent (agents.&lt;id&gt;) of the chat.</summary>
    public string? Agent { get; init; }
    public string? ReasoningEffort { get; init; }
    public int Messages { get; init; }
    public int Tools { get; init; }
    public int SystemPromptChars { get; init; }
    public int InputChars { get; init; }
    public string? LastUser { get; init; }

    public string State { get; private set; } = "running";
    public bool Completed { get; private set; }
    public long? FirstTokenMs { get; private set; }
    public long? DurationMs { get; private set; }
    private readonly List<string> _resets = [];
    private readonly List<string> _notices = [];
    private readonly List<string> _toolCalls = [];
    private int _textChars, _thinkingChars;
    private Usage? _usage;
    private string? _stopReason, _error, _errorType;
    private int? _status;
    private bool? _transient, _contextOverflow;

    internal void Observe(ModelStreamEvent e, Stopwatch clock)
    {
        lock (_gate)
        {
            switch (e)
            {
                case TextDelta t:
                    FirstTokenMs ??= clock.ElapsedMilliseconds;
                    _textChars += t.Text.Length;
                    break;
                case ThinkingDelta t:
                    FirstTokenMs ??= clock.ElapsedMilliseconds;
                    _thinkingChars += t.Text.Length;
                    break;
                case ToolCallStarted:
                    FirstTokenMs ??= clock.ElapsedMilliseconds;
                    break;
                case UsageUpdate u:
                    _usage = u.Usage;
                    break;
                case StreamReset r:
                    if (_resets.Count < 20) _resets.Add($"{clock.ElapsedMilliseconds} ms: {r.Reason}");
                    break;
                case StreamNotice n:
                    if (_notices.Count < 20) _notices.Add($"{clock.ElapsedMilliseconds} ms: {n.Text}");
                    break;
                case StreamCompleted c:
                    Completed = true;
                    _usage = c.Message.Usage ?? _usage;
                    _stopReason = c.Message.StopReason;
                    _toolCalls.Clear();
                    _toolCalls.AddRange(c.Message.ToolCalls.Select(x => x.Name));
                    break;
            }
        }
    }

    internal void End(string state, Stopwatch clock, Exception? error)
    {
        lock (_gate)
        {
            if (State != "running") return;
            State = state;
            DurationMs = clock.ElapsedMilliseconds;
            if (error is null) return;
            _error = error.Message;
            _errorType = error.GetType().Name;
            if (error is ModelException m)
            {
                _status = m.StatusCode;
                _transient = m.Transient;
                _contextOverflow = m.ContextOverflow;
                _errorType = m.ErrorType ?? _errorType;
            }
        }
    }

    public JsonObject ToJson(bool detail = false)
    {
        lock (_gate)
        {
            var o = new JsonObject
            {
                ["id"] = Id,
                ["startedAt"] = StartedAt.ToString("O"),
                ["state"] = State,
                ["model"] = $"{Provider}/{Model}",
                ["purpose"] = Purpose,
                ["agent"] = Agent,
                ["sessionId"] = SessionId,
                ["runId"] = RunId,
                ["firstTokenMs"] = FirstTokenMs,
                ["durationMs"] = DurationMs ?? (long)(DateTimeOffset.UtcNow - StartedAt).TotalMilliseconds,
                ["attempts"] = 1 + _resets.Count,
                ["inputTokens"] = _usage?.InputTokens,
                ["cacheReadTokens"] = _usage?.CacheReadTokens,
                ["outputTokens"] = _usage?.OutputTokens,
                ["stopReason"] = _stopReason,
                ["error"] = _error,
            };
            if (!detail) return o;
            o["reasoningEffort"] = ReasoningEffort;
            o["request"] = new JsonObject
            {
                ["messages"] = Messages, ["tools"] = Tools, ["systemPromptChars"] = SystemPromptChars, ["inputChars"] = InputChars,
                ["lastUser"] = LastUser,
            };
            o["response"] = new JsonObject
            {
                ["textChars"] = _textChars, ["thinkingChars"] = _thinkingChars,
                ["toolCalls"] = new JsonArray([.. _toolCalls.Select(n => (JsonNode?)n)]),
                ["cacheWriteTokens"] = _usage?.CacheWriteTokens, ["reasoningTokens"] = _usage?.ReasoningTokens, ["costUsd"] = _usage?.CostUsd,
            };
            o["resets"] = new JsonArray([.. _resets.Select(r => (JsonNode?)r)]);
            o["notices"] = new JsonArray([.. _notices.Select(n => (JsonNode?)n)]);
            if (_error is not null)
                o["errorDetail"] = new JsonObject { ["type"] = _errorType, ["status"] = _status, ["transient"] = _transient, ["contextOverflow"] = _contextOverflow };
            return o;
        }
    }
}

/// <summary>One tool call (tool.start → tool.end).</summary>
public sealed class ToolRecord
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Arguments { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public bool IsError { get; set; }

    public JsonObject ToJson() => new()
    {
        ["callId"] = CallId, ["name"] = Name, ["sessionId"] = SessionId, ["runId"] = RunId, ["startedAt"] = StartedAt.ToString("O"),
        ["state"] = EndedAt is null ? "running" : IsError ? "error" : "ok",
        ["durationMs"] = DurationMs ?? (long)(DateTimeOffset.UtcNow - StartedAt).TotalMilliseconds,
        ["arguments"] = Arguments,
    };
}

/// <summary>One journal line: a bus event with a short summary of its data.</summary>
public sealed record JournalEntry(long Seq, DateTimeOffset Time, string Type, string? SessionId, string? Source, JsonNode? Summary)
{
    public JsonObject ToJson() => new()
    {
        ["seq"] = Seq, ["time"] = Time.ToString("O"), ["type"] = Type, ["sessionId"] = SessionId, ["source"] = Source,
        ["data"] = Summary?.DeepClone(),
    };
}
