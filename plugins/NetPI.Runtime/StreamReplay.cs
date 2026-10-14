using System.Text.Json.Nodes;

namespace NetPI.Runtime;

/// <summary>
/// What a chat's answer has streamed so far, for a page that opens the chat while it streams, or comes back from a lost
/// connection, and so has missed the start (<c>agent.stream</c>): the <c>stream.*</c> events of the model call in flight, and
/// the <c>tool.*</c> events of the calls still running (their output so far as one event, its tail). Each keeps the seq the
/// bus gave it, and the answer says up to which seq it is complete: the page puts what it receives itself meanwhile after
/// it, by seq, and drops what the answer already holds.
/// <para>
/// It is a subscriber of the bus like the page's socket, so it sees the events as the page does, in seq order and only
/// those meant for the UI. A chat's entries go when its stream ends (a tool call's when it ends, unless the model call it
/// belongs to still streams: a provider's own tools are only stored with the answer), when its run is over, and with the
/// chat.
/// </para>
/// </summary>
internal sealed class StreamReplay
{
    /// <summary>The tail of a running call's output that is kept: what the page keeps of it (chat.svelte.js OUTPUT_CAP).</summary>
    public const int MaxToolOutput = 200_000;

    private sealed record Entry(string Type, JsonObject Data, long Seq)
    {
        /// <summary>When the bus event happened (epoch ms): a replayed tool.start tells the page when the call really began.</summary>
        public long Ts { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private sealed class Chat
    {
        public readonly List<Entry> Stream = [];
        public readonly Dictionary<string, List<Entry>> Tools = new(StringComparer.Ordinal);
        public bool Streaming;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Chat> _chats = new(StringComparer.Ordinal);
    private long _seq;

    // One subscription to everything, not one per type: each has its own queue and worker, and only one line sees the
    // events in the order the page does, which the seq the snapshot is complete up to depends on.
    public StreamReplay(IPluginContext ctx) => ctx.Events.Subscribe("*", On);

    /// <summary>The last seq this line has seen.</summary>
    public long Seq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary><c>agent.stream</c>: the chat's stream so far, and the seq it is complete up to.</summary>
    public JsonObject Snapshot(string sessionId)
    {
        lock (_gate)
        {
            var events = new JsonArray();
            if (_chats.TryGetValue(sessionId, out var chat))
                foreach (var e in chat.Stream.Concat(chat.Tools.Values.SelectMany(t => t)).OrderBy(e => e.Seq))
                    events.Add(new JsonObject { ["type"] = e.Type, ["d"] = e.Data.DeepClone(), ["seq"] = e.Seq, ["ts"] = e.Ts });
            // `now`: the server's clock, so the page can correct the ts values for a clock that differs from its own
            return new JsonObject { ["seq"] = _seq, ["now"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["events"] = events };
        }
    }

    private void On(BusEvent e)
    {
        lock (_gate)
        {
            _seq = Math.Max(_seq, e.Seq);
            if (!e.Type.StartsWith("stream.", StringComparison.Ordinal) && !e.Type.StartsWith("tool.", StringComparison.Ordinal)
                && e.Type != EventTypes.AgentStatus && e.Type != EventTypes.SessionDeleted) return;
            if (e.Type == EventTypes.SessionDeleted)
            {
                if ((e.Data as JsonObject)?["id"]?.GetValue<string>() is { } gone) _chats.Remove(gone);
                return;
            }
            if (e.Type == EventTypes.AgentStatus)
            {
                // a run that is over has nothing in flight; a stream.end lost to a failure must not keep a stale answer
                var agent = (e.Data as JsonObject)?["agent"] as JsonObject;
                var status = agent?["status"]?.GetValue<string>();
                if (agent?["sessionId"]?.GetValue<string>() is { } sid && status is "idle" or "completed" or "failed" or "cancelled") _chats.Remove(sid);
                return;
            }
            if (!e.Ui || e.SessionId is not { } sessionId || e.Data is not JsonObject data) return;
            if (!_chats.TryGetValue(sessionId, out var chat)) _chats[sessionId] = chat = new Chat();
            switch (e.Type)
            {
                case EventTypes.StreamStart:
                    chat.Stream.Clear();
                    chat.Streaming = true;
                    chat.Stream.Add(new Entry(e.Type, data, e.Seq));
                    break;
                case EventTypes.StreamDelta:
                    // consecutive text of one kind is one entry: the page appends it the same way
                    if (chat.Stream is [.., { Type: EventTypes.StreamDelta } last] && Str(last.Data, "kind") == Str(data, "kind"))
                        chat.Stream[^1] = new Entry(e.Type, new JsonObject { ["sessionId"] = sessionId, ["kind"] = Str(data, "kind"), ["text"] = Str(last.Data, "text") + Str(data, "text") }, e.Seq);
                    else chat.Stream.Add(new Entry(e.Type, data, e.Seq));
                    break;
                case EventTypes.StreamTool or EventTypes.StreamReset:
                    chat.Stream.Add(new Entry(e.Type, data, e.Seq));
                    break;
                case EventTypes.StreamEnd:
                    chat.Stream.Clear();
                    chat.Streaming = false;
                    chat.Tools.Clear();   // what the model call's own tools did is stored with its answer now
                    break;
                case EventTypes.ToolStart when Str(data, "callId") is { } id:
                    chat.Tools[id] = [new Entry(e.Type, data, e.Seq)];
                    break;
                case EventTypes.ToolOutput when Str(data, "callId") is { } id && chat.Tools.TryGetValue(id, out var call):
                    // the output so far is one event, its tail: the page keeps no more of it either
                    if (call is [.., { Type: EventTypes.ToolOutput } before])
                    {
                        var text = Str(before.Data, "chunk") + Str(data, "chunk");
                        if (text.Length > MaxToolOutput) text = text[^MaxToolOutput..];
                        call[^1] = new Entry(e.Type, new JsonObject { ["sessionId"] = sessionId, ["callId"] = id, ["chunk"] = text }, e.Seq);
                    }
                    else call.Add(new Entry(e.Type, data, e.Seq));
                    break;
                case EventTypes.ToolResult when Str(data, "callId") is { } id && chat.Tools.TryGetValue(id, out var call):
                    call.Add(new Entry(e.Type, data, e.Seq));
                    break;
                case EventTypes.ToolEnd when Str(data, "callId") is { } id:
                    if (chat.Streaming && chat.Tools.TryGetValue(id, out var running)) running.Add(new Entry(e.Type, data, e.Seq));
                    else chat.Tools.Remove(id);   // its result is a stored message now
                    break;
            }
            if (chat.Stream.Count == 0 && chat.Tools.Count == 0 && !chat.Streaming) _chats.Remove(sessionId);
        }
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
