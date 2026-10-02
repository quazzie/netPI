using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// The collections this migration creates (plugin netpi.context) — the shape the Context plugin's PromptStore reads and
// writes, kept here for the one-off store migration:
//   context_prompts  key: the session id                 index fields: (none)
//       { prompt: string, createdAt: string, promptRevision: int }
//   context_tools    key: the session id                  index fields: (none)
//       { tools: string[], sinceSeq: int }
//   context_sent     key: "<sessionId>:<version>"        index fields: sessionId (Text), version (Integer), afterSeq (Integer)
//       { sessionId: string, version: int, afterSeq: int, prompt: string, createdAt: string,
//         tools: [{ name: string, description: string, parameters: object, revision: string }] }
internal static class ContextMigration
{
    public const string PluginId = "netpi.context";

    public static CollectionSpec PromptsSpec() => new CollectionSpec();
    public static CollectionSpec ToolsSpec() => new CollectionSpec();
    public static CollectionSpec SentSpec() => new CollectionSpec().Text("sessionId").Integer("version").Integer("afterSeq");

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        var data = storage.Plugins.For(PluginId);
        var prompts = data.Collection("context_prompts", PromptsSpec());
        var tools = data.Collection("context_tools", ToolsSpec());
        var sent = data.Collection("context_sent", SentSpec());

        var promptRows = TryRead(old, "context_prompts", notes,
            "SELECT session_id, prompt, created_at, prompt_revision FROM {0} ORDER BY session_id",
            r => (r.GetString("session_id"), r.GetString("prompt"), r.GetString("created_at"), r.GetInt64("prompt_revision")));
        var toolRows = TryRead(old, "context_tools", notes,
            "SELECT session_id, tools, since_seq FROM {0} ORDER BY session_id",
            r => (r.GetString("session_id"), r.GetString("tools"), r.GetInt64("since_seq")));
        var sentRows = TryRead(old, "context_sent", notes,
            "SELECT session_id, version, after_seq, prompt, tools, created_at FROM {0} ORDER BY session_id, version",
            r => (r.GetString("session_id"), r.GetInt64("version"), r.GetInt64("after_seq"), r.GetString("prompt"), r.GetString("tools"), r.GetString("created_at")));

        data.Transaction(() =>
        {
            foreach (var (sessionId, prompt, createdAt, revision) in promptRows)
                prompts.Put(sessionId, new JsonObject
                {
                    ["prompt"] = prompt,
                    ["createdAt"] = createdAt,
                    ["promptRevision"] = revision,
                });
            foreach (var (sessionId, toolsJson, sinceSeq) in toolRows)
            {
                var doc = new JsonObject { ["sinceSeq"] = sinceSeq };
                PutTools(doc, notes, "context_tools", sessionId, toolsJson);
                tools.Put(sessionId, doc);
            }
            foreach (var (sessionId, version, afterSeq, prompt, toolsJson, createdAt) in sentRows)
            {
                var doc = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["version"] = version,
                    ["afterSeq"] = afterSeq,
                    ["prompt"] = prompt,
                    ["createdAt"] = createdAt,
                };
                PutTools(doc, notes, "context_sent", sessionId, toolsJson);
                sent.Put(sessionId + ":" + version, doc);
            }
        });
    }

    /// <summary>The tools column is the array as a JSON string; a value that does not parse is kept raw (as the string).</summary>
    private static void PutTools(JsonObject doc, List<string> notes, string table, string sessionId, string toolsJson)
    {
        var node = Jsonx.ArrayOrRaw(toolsJson);
        if (node is not JsonArray)
            notes.Add($"{table} row for session {sessionId}: the tools column does not parse as a JSON array; kept as-is");
        doc["tools"] = node;
    }

    private static List<T> TryRead<T>(Database old, string table, List<string> notes, string format, Func<ISqlRow, T> map)
    {
        try
        {
            return old.Query(string.Format(format, "old." + table), null, map).ToList();
        }
        catch (SqliteException)
        {
            notes.Add($"old database has no {table} table: nothing to migrate for it");
            return [];
        }
    }
}
