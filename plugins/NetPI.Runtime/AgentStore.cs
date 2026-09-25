using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Runtime;

/// <summary>Persisted agent record (so recent agents survive restarts).</summary>
internal sealed record AgentRecord(AgentInfo Info, string? Instructions, bool NotifyParent);

/// <summary>
/// Agent records in the <c>agent_records</c> table (scope "agent"). Every call is best effort: a missing or failing
/// database only disables persistence.
/// </summary>
internal sealed class AgentStore(IPluginContext ctx)
{
    private bool _ready;

    public void Initialize()
    {
        IDatabase? db;
        try { db = ctx.Db; } catch { db = null; }
        if (db is null) return;
        try
        {
            db.Migrate("agent",
                """
                CREATE TABLE IF NOT EXISTS agent_records (
                  id TEXT PRIMARY KEY,
                  session_id TEXT NOT NULL,
                  parent_agent_id TEXT,
                  name TEXT NOT NULL,
                  status TEXT NOT NULL,
                  task TEXT,
                  result TEXT,
                  error TEXT,
                  model TEXT,
                  created_at TEXT NOT NULL,
                  finished_at TEXT,
                  stats TEXT
                )
                """,
                "CREATE INDEX IF NOT EXISTS ix_agent_records_session ON agent_records(session_id)",
                "CREATE INDEX IF NOT EXISTS ix_agent_records_created ON agent_records(created_at)");
            _ready = true;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Agent records table unavailable; agents are kept in memory only");
        }
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string StatusText(AgentStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    /// <summary>Mark rows left running/queued/yielded by a previous process as failed ("interrupted"). Returns the count.</summary>
    public int MarkInterrupted()
    {
        if (!_ready) return 0;
        try
        {
            return ctx.Db.Execute(
                "UPDATE agent_records SET status = 'failed', error = 'interrupted', finished_at = @now WHERE status IN ('running', 'queued', 'yielded')",
                new Dictionary<string, object?> { ["now"] = Iso(DateTimeOffset.UtcNow) });
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to mark interrupted agents");
            return 0;
        }
    }

    public void Save(AgentInfo info, string? instructions, bool notifyParent)
    {
        if (!_ready) return;
        try
        {
            var stats = new JsonObject
            {
                ["info"] = JsonSerializer.SerializeToNode(info, NetPiJson.Options),
                ["instructions"] = instructions,
                ["notifyParent"] = notifyParent,
            };
            ctx.Db.Execute(
                """
                INSERT INTO agent_records (id, session_id, parent_agent_id, name, status, task, result, error, model, created_at, finished_at, stats)
                VALUES (@id, @sessionId, @parentAgentId, @name, @status, @task, @result, @error, @model, @createdAt, @finishedAt, @stats)
                ON CONFLICT(id) DO UPDATE SET
                  session_id = excluded.session_id, parent_agent_id = excluded.parent_agent_id, name = excluded.name,
                  status = excluded.status, task = excluded.task, result = excluded.result, error = excluded.error,
                  model = excluded.model, finished_at = excluded.finished_at, stats = excluded.stats
                """,
                new Dictionary<string, object?>
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
                    ["stats"] = stats.ToJsonString(),
                });
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist agent {Agent}", info.Id);
        }
    }

    public List<AgentRecord> LoadRecent(int limit)
    {
        if (!_ready) return [];
        try
        {
            return ctx.Db.Query("SELECT * FROM agent_records ORDER BY created_at DESC LIMIT @limit",
                new Dictionary<string, object?> { ["limit"] = limit }, Map).Where(r => r is not null).Select(r => r!).ToList();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to load agent records");
            return [];
        }
    }

    public AgentRecord? LoadBySession(string sessionId)
    {
        if (!_ready) return null;
        try
        {
            return ctx.Db.QuerySingle("SELECT * FROM agent_records WHERE session_id = @sid ORDER BY created_at DESC LIMIT 1",
                new Dictionary<string, object?> { ["sid"] = sessionId }, Map);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to load agent for session {Session}", sessionId);
            return null;
        }
    }

    private static AgentRecord? Map(IDbRow r)
    {
        AgentInfo? info = null;
        string? instructions = null;
        var notify = true;
        var statsText = r.GetStringOrNull("stats");
        if (!string.IsNullOrEmpty(statsText))
        {
            try
            {
                var stats = JsonNode.Parse(statsText) as JsonObject;
                if (stats?["info"] is JsonObject io) info = io.Deserialize<AgentInfo>(NetPiJson.Options);
                if (stats?["instructions"] is JsonValue iv && iv.TryGetValue<string>(out var s)) instructions = s;
                if (stats?["notifyParent"] is JsonValue nv && nv.TryGetValue<bool>(out var b)) notify = b;
            }
            catch { /* corrupt stats: rebuild from columns */ }
        }
        info ??= new AgentInfo();
        info.Id = r.GetString("id");
        info.SessionId = r.GetString("session_id");
        info.ParentAgentId = r.GetStringOrNull("parent_agent_id");
        info.Name = r.GetString("name");
        info.Task = r.GetStringOrNull("task");
        info.Result = r.GetStringOrNull("result");
        info.Error = r.GetStringOrNull("error");
        info.Model = r.GetStringOrNull("model");
        if (Enum.TryParse<AgentStatus>(r.GetString("status"), ignoreCase: true, out var st)) info.Status = st;
        if (DateTimeOffset.TryParse(r.GetString("created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) info.CreatedAt = c;
        if (r.GetStringOrNull("finished_at") is { } fs && DateTimeOffset.TryParse(fs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f)) info.FinishedAt = f;
        info.Activity = null;
        info.QueuedMessages = 0;
        return new AgentRecord(info, instructions, notify);
    }
}
