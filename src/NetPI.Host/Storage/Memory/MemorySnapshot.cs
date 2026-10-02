using System.Text.Json.Nodes;

namespace NetPI.Host.Storage.Memory;

/// <summary>
/// The store as files: one <c>memory.json</c> with every collection in it (the shape is documented at the top of
/// <see cref="MemoryStorageProvider"/>), written into the directory a caller names — the directory is what a
/// backup manifest lists, next to <see cref="StorageInfo.Provider"/>, so a restore knows which provider reads it.
/// </summary>
internal sealed class MemorySnapshot(MemoryStorage store) : IStorageSnapshot
{
    public const string FileName = "memory.json";

    public IReadOnlyList<string> Write(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            throw new StorageException($"The snapshot directory '{directory}' does not exist");
        // One copy of everything, taken under the provider's lock. The file is written outside it: a slow disk must
        // not hold up a chat, and the text is already the copy.
        var text = store.Read(() => Dump().ToJsonString(NetPiJson.Indented));
        File.WriteAllText(Path.Combine(directory, FileName), text);
        return [FileName];
    }

    private JsonObject Dump()
    {
        var root = new JsonObject
        {
            ["provider"] = store.ProviderId,
            ["version"] = 1,
            ["nextMessageId"] = store.NextMessageId,
        };
        var projects = new JsonObject();
        foreach (var (id, row) in store.Projects.OrderBy(p => p.Key, StringComparer.Ordinal))
            projects[id] = new JsonObject
            {
                ["id"] = row.Id,
                ["name"] = row.Name,
                ["path"] = row.Path,
                ["createdAt"] = row.CreatedAt,
                ["updatedAt"] = row.UpdatedAt,
                ["lastUsedAt"] = row.LastUsedAt,
                ["meta"] = Row.Object(row.Meta),
            };
        root["projects"] = projects;

        var sessions = new JsonObject();
        foreach (var (id, row) in store.SessionRows.OrderBy(s => s.Key, StringComparer.Ordinal))
            sessions[id] = new JsonObject
            {
                ["id"] = row.Id,
                ["title"] = row.Title,
                ["projectId"] = row.ProjectId,
                ["parentSessionId"] = row.ParentSessionId,
                ["kind"] = row.Kind,
                ["model"] = row.Model,
                ["reasoning"] = row.Reasoning,
                ["createdAt"] = row.CreatedAt,
                ["updatedAt"] = row.UpdatedAt,
                ["archived"] = row.Archived,
                ["pinned"] = row.Pinned,
                ["messageCount"] = row.MessageCount,
                ["contextTokens"] = row.ContextTokens,
                ["meta"] = Row.Object(row.Meta),
            };
        root["sessions"] = sessions;

        var messages = new JsonObject();
        foreach (var (sessionId, rows) in store.Messages.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var list = new JsonArray();
            foreach (var row in rows)
                list.Add(new JsonObject
                {
                    ["id"] = row.Id,
                    ["seq"] = row.Seq,
                    ["role"] = row.Role.ToString().ToLowerInvariant(),
                    ["parts"] = JsonNode.Parse(row.Parts),
                    ["createdAt"] = row.CreatedAt,
                    ["provider"] = row.Provider,
                    ["model"] = row.Model,
                    ["stopReason"] = row.StopReason,
                    ["usage"] = row.Usage is null ? null : JsonNode.Parse(row.Usage),
                    ["durationMs"] = row.DurationMs,
                    ["compacted"] = row.Compacted,
                    ["meta"] = Row.Object(row.Meta),
                });
            messages[sessionId] = list;
        }
        root["messages"] = messages;

        var values = new JsonObject();
        foreach (var (key, value) in store.ValuesByKey.OrderBy(v => v.Key, StringComparer.Ordinal)) values[key] = value;
        root["values"] = values;

        var plugins = new JsonObject();
        foreach (var (pluginId, data) in store.PluginsById.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var collections = new JsonObject();
            foreach (var (name, collection) in data.Collections.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var fields = new JsonObject();
                foreach (var (field, type) in collection.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)) fields[field] = type.ToString();
                var docs = new JsonObject();
                foreach (var (key, doc) in collection.Docs.OrderBy(d => d.Key, StringComparer.Ordinal)) docs[key] = (JsonObject)doc.DeepClone();
                collections[name] = new JsonObject { ["fields"] = fields, ["docs"] = docs };
            }
            plugins[pluginId] = collections;
        }
        root["plugins"] = plugins;
        return root;
    }
}
