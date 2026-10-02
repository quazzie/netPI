using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// The collection this migration creates (plugin netpi.runtime) — the shape the Runtime plugin's AgentStore reads and
// writes, kept here for the one-off store migration:
//   agent_records  key: the agent id
//       index fields: sessionId (Text), createdAt (Text, the ISO timestamp it is written as)
//       { id, sessionId, parentAgentId, name, status, task, result, error, model, createdAt, finishedAt,
//         stats: { info: AgentInfo, instructions: string|null, notifyParent: bool } }
internal static class RuntimeMigration
{
    public const string PluginId = "netpi.runtime";
    public const string Collection = "agent_records";

    public static CollectionSpec Spec() => new CollectionSpec().Text("sessionId").Text("createdAt");

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        var items = storage.Plugins.For(PluginId).Collection(Collection, Spec());
        var rows = TryRead(old, notes);
        storage.Plugins.For(PluginId).Transaction(() =>
        {
            foreach (var r in rows)
            {
                var stats = Jsonx.Object(r.StatsText);
                if (r.StatsText is not null && stats is null)
                    notes.Add($"agent {r.Id}: the stats column does not parse as JSON; kept as-is");
                JsonNode? statsNode = stats;
                if (statsNode is null && r.StatsText is not null) statsNode = JsonValue.Create(r.StatsText);
                var doc = new JsonObject
                {
                    ["id"] = r.Id,
                    ["sessionId"] = r.SessionId,
                    ["parentAgentId"] = r.ParentAgentId,
                    ["name"] = r.Name,
                    ["status"] = r.Status,
                    ["task"] = r.Task,
                    ["result"] = r.Result,
                    ["error"] = r.Error,
                    ["model"] = r.Model,
                    ["createdAt"] = r.CreatedAt,
                    ["finishedAt"] = r.FinishedAt,
                    ["stats"] = statsNode,
                };
                items.Put(r.Id, doc);
            }
        });
    }

    private sealed record Row(string Id, string SessionId, string? ParentAgentId, string Name, string Status, string? Task,
        string? Result, string? Error, string? Model, string CreatedAt, string? FinishedAt, string? StatsText);

    private static List<Row> TryRead(Database old, List<string> notes)
    {
        try
        {
            return old.Query("""
                SELECT id, session_id, parent_agent_id, name, status, task, result, error, model, created_at, finished_at, stats
                FROM old.agent_records ORDER BY id
                """, null, r => new Row(
                    r.GetString("id"), r.GetString("session_id"), r.GetStringOrNull("parent_agent_id"), r.GetString("name"),
                    r.GetString("status"), r.GetStringOrNull("task"), r.GetStringOrNull("result"), r.GetStringOrNull("error"),
                    r.GetStringOrNull("model"), r.GetString("created_at"), r.GetStringOrNull("finished_at"),
                    r.GetStringOrNull("stats"))).ToList();
        }
        catch (SqliteException)
        {
            notes.Add("old database has no agent_records table: nothing to migrate for it");
            return [];
        }
    }
}
