using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

// The collection this plugin keeps in its own storage (ctx.Data); AgentStore is the only code that touches it. A one-off
// migration of an existing home is written from this block: name, key, document fields, index fields.
//   agent_records  key: the agent id
//       index fields: sessionId (text), createdAt (text, the ISO timestamp it is written as), status (text)
//       { id, sessionId, parentAgentId, name, status, task, result, error, model, createdAt, finishedAt,
//         stats: { info: the record's AgentInfo (task and result stored once, at the top level),
//                  instructions: string|null, notifyParent: bool },
//         queue: [ { mode: "steer"|"queue", input: UserInput } ],
//         pending: null | the tool batch that waits for the user (PendingBatch; status stays "yielded" while it does):
//           { assistantId, open: [ { id, resultName } ],
//             waits: [ { id, kind, activity, callId, tool, data, since, ifSteered: Result, ifCancelled: Result|null,
//                        resolution: null | { run: bool, result: Result|null } } ] } }
//       Result: { content, isError, details }

/// <summary>Persisted agent record (so recent agents survive restarts).</summary>
internal sealed record AgentRecord(AgentInfo Info, string? Instructions, bool NotifyParent, List<QueuedRecord> Queue, PendingBatch? Pending = null);

/// <summary>One input in the agent's queue, with how it was offered (a steer or a follow-up).</summary>
internal sealed record QueuedRecord(string Mode, UserInput Input);

/// <summary>
/// Agent records in the plugin's own <c>agent_records</c> collection (see the file's header for its shape). Every call is
/// best effort: a failing store only costs what it wrote.
/// </summary>
internal sealed class AgentStore(IPluginContext ctx)
{
    private IDataCollection _records = null!;
    /// <summary>Per record: the version of the last write and the lock that writes in version order (saves are snapshotted under the agent's gate, written outside it).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WriteSlot> _written = new();
    private sealed class WriteSlot { public readonly object Lock = new(); public long Version; }

    public void Initialize() =>
        _records = ctx.Data.Collection("agent_records", new CollectionSpec().Text("sessionId").Text("createdAt").Text("status"));

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string StatusText(AgentStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    private static string? Text(JsonObject doc, string field) =>
        doc[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The record's <see cref="AgentInfo"/> as stored: the object minus the fields the record keeps at the top
    /// level (task and result, the two that can be large), so each is stored once.</summary>
    private static JsonObject? InfoJson(AgentInfo info)
    {
        var doc = JsonSerializer.SerializeToNode(info, NetPiJson.Options)?.AsObject();
        doc?.Remove("task");
        doc?.Remove("result");
        return doc;
    }

    private static DateTimeOffset? Time(JsonObject doc, string field) =>
        Text(doc, field) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>Mark records left running/queued/yielded by a previous process as failed ("interrupted"). Returns the count.</summary>
    public int MarkInterrupted()
    {
        try
        {
            return ctx.Data.Transaction(() =>
            {
                var now = Iso(DateTimeOffset.UtcNow);
                // a record that waits for the user is not interrupted: its run ended at the call that waits (no run to lose)
                var rows = _records.Find(new DataQuery().In("status", ["running", "queued", "yielded"])).Where(r => r.Doc["pending"] is not JsonObject).ToList();
                foreach (var row in rows)
                {
                    row.Doc["status"] = "failed";
                    row.Doc["error"] = "interrupted";
                    row.Doc["finishedAt"] = now;
                    _records.Put(row.Key, row.Doc);
                }
                return rows.Count;
            });
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to mark interrupted agents");
            return 0;
        }
    }

    /// <summary>How long a finished agent's record is kept (its task and result text) before <see cref="PruneOld"/> deletes it.</summary>
    public const int KeepFinishedDays = 30;
    private long _prunedAt;

    /// <summary>Delete the finished (completed, failed, cancelled) records older than <see cref="KeepFinishedDays"/>; the count. Records that wait or run are never touched.</summary>
    public int PruneOld()
    {
        Interlocked.Exchange(ref _prunedAt, Environment.TickCount64);
        try
        {
            var cutoff = Iso(DateTimeOffset.UtcNow.AddDays(-KeepFinishedDays));
            return _records.DeleteWhere(new DataQuery().In("status", ["completed", "failed", "cancelled"]).Lt("createdAt", cutoff));
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to prune old agent records");
            return 0;
        }
    }

    /// <summary><see cref="PruneOld"/> at most once an hour, off the caller's thread (it is called where an agent finishes).</summary>
    public void PruneOldSometimes()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _prunedAt);
        if (last != 0 && now - last < 3_600_000 || Interlocked.CompareExchange(ref _prunedAt, now, last) != last) return;
        _ = Task.Run(() => PruneOld());
    }

    /// <summary>The agent left the in-memory registry: its write-order slot goes with it.</summary>
    public void Forget(string id) => _written.TryRemove(id, out _);

    /// <param name="pending">The batch that waits for the user (<see cref="PendingJson"/>, made under the agent's gate), or null.</param>
    /// <param name="version">The agent's save counter when the snapshot was taken (0: unversioned, always written). A write older than the last one made for the record is dropped.</param>
    public void Save(AgentInfo info, string? instructions, bool notifyParent, IReadOnlyList<QueuedRecord> queue, JsonObject? pending = null, long version = 0)
    {
        try
        {
            var slot = _written.GetOrAdd(info.Id, _ => new WriteSlot());
            lock (slot.Lock)
            {
                if (version != 0)
                {
                    if (version < slot.Version) return;
                    slot.Version = version;
                }
                Write(info, instructions, notifyParent, queue, pending);
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist agent {Agent}", info.Id);
        }
    }

    private void Write(AgentInfo info, string? instructions, bool notifyParent, IReadOnlyList<QueuedRecord> queue, JsonObject? pending)
    {
        {
            _records.Put(info.Id, new JsonObject
            {
                ["id"] = info.Id,
                ["sessionId"] = info.SessionId,
                ["parentAgentId"] = info.ParentAgentId,
                ["name"] = info.Name,
                ["status"] = StatusText(info.Status),
                ["task"] = info.Task,
                ["result"] = info.Result,
                ["error"] = info.Error,
                ["model"] = info.Model,
                ["createdAt"] = Iso(info.CreatedAt),
                ["finishedAt"] = info.FinishedAt is { } f ? Iso(f) : null,
                ["queue"] = new JsonArray(queue.Select(q => new JsonObject
                {
                    ["mode"] = q.Mode,
                    ["input"] = NetPiJson.ToNode(q.Input),
                }).ToArray()),
                ["stats"] = new JsonObject
                {
                    ["info"] = InfoJson(info),
                    ["instructions"] = instructions,
                    ["notifyParent"] = notifyParent,
                },
                ["pending"] = pending,
            });
        }
    }

    public List<AgentRecord> LoadRecent(int limit)
    {
        try
        {
            return _records.Find(new DataQuery().Order("createdAt", descending: true).Take(limit)).Select(d => Map(d.Doc)).ToList();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to load agent records");
            return [];
        }
    }

    /// <summary>The records that wait for the user, however old: they stay in memory until they are answered.</summary>
    public List<AgentRecord> LoadWaiting()
    {
        try
        {
            return _records.Find(new DataQuery().Eq("status", "yielded")).Where(d => d.Doc["pending"] is JsonObject).Select(d => Map(d.Doc)).ToList();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to load the agents that wait for the user");
            return [];
        }
    }

    public AgentRecord? LoadBySession(string sessionId)
    {
        try
        {
            return _records.Find(new DataQuery().Eq("sessionId", sessionId).Order("createdAt", descending: true).Take(1))
                .Select(d => Map(d.Doc)).FirstOrDefault();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to load agent for session {Session}", sessionId);
            return null;
        }
    }

    private AgentRecord Map(JsonObject doc)
    {
        AgentInfo? info = null;
        string? instructions = null;
        var notify = true;
        if (doc["stats"] is JsonObject stats)
        {
            try
            {
                if (stats["info"] is JsonObject io) info = io.Deserialize<AgentInfo>(NetPiJson.Options);
                if (stats["instructions"] is JsonValue iv && iv.TryGetValue<string>(out var s)) instructions = s;
                if (stats["notifyParent"] is JsonValue nv && nv.TryGetValue<bool>(out var b)) notify = b;
            }
            catch { /* corrupt stats: rebuild from the record's own fields */ }
        }
        info ??= new AgentInfo();
        info.Id = Text(doc, "id") ?? "";
        info.SessionId = Text(doc, "sessionId") ?? "";
        info.ParentAgentId = Text(doc, "parentAgentId");
        info.Name = Text(doc, "name") ?? "";
        info.Task = Text(doc, "task");
        info.Result = Text(doc, "result");
        info.Error = Text(doc, "error");
        info.Model = Text(doc, "model");
        if (Enum.TryParse<AgentStatus>(Text(doc, "status"), ignoreCase: true, out var st)) info.Status = st;
        if (Time(doc, "createdAt") is { } c) info.CreatedAt = c;
        if (Time(doc, "finishedAt") is { } f) info.FinishedAt = f;
        info.Activity = null;
        var queue = new List<QueuedRecord>();
        if (doc["queue"] is JsonArray arr)
            foreach (var e in arr)
                if (e is JsonObject q && q["mode"] is JsonValue mv && mv.TryGetValue<string>(out var mode) && q["input"] is JsonObject iq)
                {
                    try { queue.Add(new QueuedRecord(mode, iq.Deserialize<UserInput>(NetPiJson.Options) ?? new UserInput())); }
                    catch { /* one corrupt entry: drop it, keep the rest */ }
                }
        info.QueuedMessages = queue.Count;
        PendingBatch? pending = null;
        if (doc["pending"] is JsonObject po)
        {
            try { pending = ReadPending(po); }
            catch (Exception ex)
            {
                // the agent no longer waits, and its open calls are closed as not executed at the next model call
                ctx.Logger.LogWarning(ex, "The batch that agent {Agent} waits on could not be read", info.Id);
                info.Error = "Its waiting tool batch could not be read from storage, so it no longer waits for your answer.";
            }
        }
        return new AgentRecord(info, instructions, notify, queue, pending);
    }

    // ---------------------------------------------------------------- the batch that waits for the user

    internal static JsonObject PendingJson(PendingBatch b) => new()
    {
        ["assistantId"] = b.AssistantId,
        ["open"] = new JsonArray([.. b.Open.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["resultName"] = o.ResultName })]),
        ["waits"] = new JsonArray([.. b.Waits.Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Id, ["kind"] = w.Kind, ["activity"] = w.Activity, ["callId"] = w.CallId, ["tool"] = w.Tool,
            ["data"] = w.Data?.DeepClone(), ["since"] = Iso(w.Since),
            ["ifSteered"] = ResultJson(w.IfSteered),
            ["ifCancelled"] = w.IfCancelled is null ? null : ResultJson(w.IfCancelled),
            ["resolution"] = w.Resolution is null ? null : new JsonObject
            {
                ["run"] = w.Resolution.Run,
                ["result"] = w.Resolution.Result is null ? null : ResultJson(w.Resolution.Result),
            },
        })]),
    };

    private static PendingBatch ReadPending(JsonObject o)
    {
        var b = new PendingBatch { AssistantId = o["assistantId"]!.GetValue<long>() };
        foreach (var e in o["open"] as JsonArray ?? [])
            if (e is JsonObject x && Text(x, "id") is { } id) b.Open.Add(new OpenCall(id, Text(x, "resultName") ?? ""));
        foreach (var e in o["waits"] as JsonArray ?? [])
        {
            if (e is not JsonObject w || Text(w, "id") is not { } id || Text(w, "callId") is not { } callId) continue;
            b.Waits.Add(new PendingWait
            {
                Id = id, Kind = Text(w, "kind") ?? "", Activity = Text(w, "activity") ?? "waiting for you", CallId = callId,
                Tool = Text(w, "tool") ?? "", Data = w["data"]?.DeepClone() as JsonObject, Since = Time(w, "since") ?? DateTimeOffset.UtcNow,
                IfSteered = ReadResult(w["ifSteered"] as JsonObject) ?? ToolResult.Ok("No answer: the user wrote a new message instead; it follows."),
                IfCancelled = ReadResult(w["ifCancelled"] as JsonObject),
                Resolution = w["resolution"] is JsonObject r
                    ? new WaitResolution { Run = r["run"] is JsonValue rv && rv.TryGetValue<bool>(out var run) && run, Result = ReadResult(r["result"] as JsonObject) }
                    : null,
            });
        }
        return b;
    }

    /// <summary>A result as plain data: its details are JSON, never the owner plugin's own objects (they would keep its old code loaded after a reload).</summary>
    internal static ToolResult Plain(ToolResult r) => new()
    {
        Content = r.Content, IsError = r.IsError, Details = r.Details is null or JsonNode ? r.Details : NetPiJson.ToNode(r.Details),
    };

    private static JsonObject ResultJson(ToolResult r) => new()
    {
        ["content"] = r.Content, ["isError"] = r.IsError, ["details"] = Plain(r).Details is JsonNode d ? d.DeepClone() : null,
    };

    private static ToolResult? ReadResult(JsonObject? o) => o is null ? null : new ToolResult
    {
        Content = Text(o, "content") ?? "",
        IsError = o["isError"] is JsonValue v && v.TryGetValue<bool>(out var e) && e,
        Details = o["details"]?.DeepClone(),
    };
}
