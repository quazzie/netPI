using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// The collection this migration creates (plugin netpi.workspaces) — the shape the Workspaces plugin's WorkspaceStore
// reads and writes, kept here for the one-off store migration:
//   workspaces   key: the workspace id
//       doc:   the WorkspaceInfo as JSON (camelCase, ISO dates, nulls omitted):
//              { id, name, path, projectId?, kind, branch?, baseCommit?, repoCommonDir?, ownerSessionId?,
//                ownerAgentId?, managed, createdAt, updatedAt, meta? } plus updatedMs (the updatedAt in ms)
//       index: projectId (Text), ownerSessionId (Text), updatedMs (Integer)
internal static class WorkspacesMigration
{
    public const string PluginId = "netpi.workspaces";
    public const string Collection = "workspaces";

    public static CollectionSpec Spec() => new CollectionSpec().Text("projectId").Text("ownerSessionId").Integer("updatedMs");

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        var rows = Rows(old, notes);
        var items = storage.Plugins.For(PluginId).Collection(Collection, Spec());
        storage.Plugins.For(PluginId).Transaction(() =>
        {
            foreach (var w in rows) items.Put(w.Id, Write(w));
        });
    }

    private static List<WorkspaceInfo> Rows(Database old, List<string> notes)
    {
        var sql = "SELECT id, name, path, project_id, kind, branch, base_commit, repo_common_dir, owner_session_id, " +
                  "owner_agent_id, managed, created_at, updated_at, meta FROM old.workspaces ORDER BY id";
        try
        {
            return old.Query(sql, null, r => new WorkspaceInfo
            {
                Id = r.GetString("id"),
                Name = r.GetString("name"),
                Path = r.GetString("path"),
                ProjectId = r.GetStringOrNull("project_id"),
                Kind = r.GetString("kind"),
                Branch = r.GetStringOrNull("branch"),
                BaseCommit = r.GetStringOrNull("base_commit"),
                RepoCommonDir = r.GetStringOrNull("repo_common_dir"),
                OwnerSessionId = r.GetStringOrNull("owner_session_id"),
                OwnerAgentId = r.GetStringOrNull("owner_agent_id"),
                Managed = r.GetInt64("managed") != 0,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("created_at")),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64("updated_at")),
                Meta = Jsonx.Object(r.GetStringOrNull("meta")),
            }).ToList();
        }
        catch (SqliteException)
        {
            notes.Add("old database has no workspaces table (predates workspaces): none to migrate");
            return [];
        }
    }

    /// <summary>Exactly what WorkspaceStore.Write produces: the record as JSON plus updatedMs.</summary>
    private static JsonObject Write(WorkspaceInfo w)
    {
        var doc = JsonSerializer.SerializeToNode(w, NetPiJson.Options) as JsonObject ?? new JsonObject();
        doc["updatedMs"] = w.UpdatedAt.ToUnixTimeMilliseconds();
        return doc;
    }
}
