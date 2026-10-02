using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

// The collection this plugin keeps in its own storage (ctx.Data); AgentStore is the only code that touches it. A one-off
// migration of an existing home is written from this block: name, key, document fields, index fields.
//   agent_records  key: the agent id
//       index fields: sessionId (text), createdAt (text, the ISO timestamp it is written as)
//       { id, sessionId, parentAgentId, name, status, task, result, error, model, createdAt, finishedAt,
//         stats: { info: AgentInfo, instructions: string|null, notifyParent: bool } }

/// <summary>Persisted agent record (so recent agents survive restarts).</summary>
internal sealed record AgentRecord(AgentInfo Info, string? Instructions, bool NotifyParent);

/// <summary>
/// Agent records in the plugin's own <c>agent_records</c> collection (see the file's header for its shape). Every call is
/// best effort: a failing store only costs what it wrote.
/// </summary>
internal sealed class AgentStore(IPluginContext ctx)
{
    private IDataCollection _records = null!;

    public void Initialize() =>
        _records = ctx.Data.Collection("agent_records", new CollectionSpec().Text("sessionId").Text("createdAt"));

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string StatusText(AgentStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    private static string? Text(JsonObject doc, string field) =>
        doc[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
                var rows = _records.Find(new DataQuery().In("status", ["running", "queued", "yielded"]));
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

    public void Save(AgentInfo info, string? instructions, bool notifyParent)
    {
        try
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
                ["stats"] = new JsonObject
                {
                    ["info"] = JsonSerializer.SerializeToNode(info, NetPiJson.Options),
                    ["instructions"] = instructions,
                    ["notifyParent"] = notifyParent,
                },
            });
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist agent {Agent}", info.Id);
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

    private static AgentRecord Map(JsonObject doc)
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
        info.QueuedMessages = 0;
        return new AgentRecord(info, instructions, notify);
    }
}
