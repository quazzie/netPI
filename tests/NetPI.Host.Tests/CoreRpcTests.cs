namespace NetPI.Host.Tests;

/// <summary>
/// The host's own RPC surface as a golden list: every method <c>rpc.list</c> reports, with its description and
/// read-only flag. A change that adds, renames or re-describes a core method — or flips what a read-only path may
/// reach — moves the list, and the failing diff shows the protocol change (idea-yonab5).
/// </summary>
public static class CoreRpcTests
{
    /// <summary>What a fresh host registers, method by method: the description as <c>rpc.list</c> reports it and the read-only flag.</summary>
    private static readonly (string Method, string Description, bool ReadOnly)[] Golden =
    [
        ("app.info", "Host information → { version, os, home, appDir, defaultWorkspace, desktop, maxMessageBytes, ... }", true),
        ("events.flush", "Wait until every event published before this call has been delivered: its answer follows them on the same socket → true", true),
        ("events.recent", "Recent bus events: { max? } → { type, sid, d, seq, ts, source }[]", true),
        ("fs.dirs", "Folder picker: { path? } → { path, parent, dirs: {name,path}[], roots }", true),
        ("logs.recent", "Recent log entries: { max? } → { time, level, category, message, exception? }[]", true),
        ("models.list", "Models of all providers: { refresh? } → { models, defaultModel }", true),
        ("plugins.list", "Plugins → PluginInfo[]", true),
        ("plugins.reload", "Reload a plugin: { id } → true", false),
        ("plugins.rescan", "Look for new/removed plugin folders → true", false),
        ("plugins.setEnabled", "Enable/disable a plugin: { id, enabled } → true", false),
        ("projects.create", "Create a project: { name, path, create? } → ProjectInfo", false),
        ("projects.delete", "Delete a project (its sessions are detached): { id } → true", false),
        ("projects.list", "All projects → ProjectInfo[]", true),
        ("projects.update", "Update a project: { id, name?, path?, meta? } → ProjectInfo (meta is merged key by key; a null value removes a key)", false),
        ("rpc.list", "RPC methods → { method, description, pluginId }[]", true),
        ("sessions.create", "Create a session: { title?, projectId?, model?, reasoning? } → SessionInfo", false),
        ("sessions.delete", "Delete a session and its subagent sessions: { id } → true", false),
        ("sessions.fork", "Fork a chat: a new chat with its messages up to a message, the original unchanged: { id, upToSeq? (the last) } → SessionInfo (publishes session.forked)", false),
        ("sessions.get", "One session: { id } → SessionInfo", true),
        ("sessions.list", "Sessions, pinned first then newest first: { projectId?, search?, includeSubagents?, parentSessionId?, includeArchived?, archivedOnly? (only archived; takes precedence over includeArchived), limit?, offset? } → SessionInfo[]", true),
        ("sessions.messages", "Message page: { id, beforeSeq?, limit? (60) } → { messages, hasMore } ascending by seq. The page is bounded in messages AND in serialized size: when the page overflows its byte budget it comes back shorter, with hasMore set and ending at an earlier seq, which the client follows with beforeSeq.", true),
        ("sessions.setProject", "Attach/detach a project: { id, projectId: string|null } → SessionInfo (publishes session.project)", false),
        ("sessions.stats", "Session-service counters: → { contextCache: { hits, reads } } — the cached contexts: a hit is a read served without touching the store, a read went to it", true),
        ("sessions.update", "Update a session: { id, title?, model?, reasoning?, archived?, pinned?, meta? } → SessionInfo (null clears model/reasoning; meta is merged key by key and a null value removes a key, so the keys other plugins keep there survive)", false),
        ("services.list", "Registered services (diagnostics)", true),
        ("settings.get", "Settings document → { path, settings }", false),
        ("settings.replace", "Replace the whole document: { settings: object, base?: the document as loaded } → true. With base, a document that changed since the load is a conflict (409) instead of a lost update.", false),
        ("settings.schema", "The settings the dialog shows as controls → SettingsSection[] (host and plugins, by group and order)", true),
        ("settings.set", "Set one value: { path (dotted), value } → true (null removes the key)", false),
        ("tools.list", "Tool registrations → { name, label, description, category, readOnly, pluginId, active, disabled, priority }[]", true),
        ("ui.commands", "Slash commands → SlashCommandInfo[]", true),
        ("ui.state.get", "Persisted UI state: { key } → JSON or null", true),
        ("ui.state.set", "Persist UI state: { key, value } → true (null value deletes)", false),
        ("ui.tabs", "Plugin UI tabs → UiTabInfo[]", true),
    ];

    public static void Register(TestRunner r)
    {
        r.Add("rpc: the host's registered methods are the golden list (descriptions and read-only flags)", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"));
            var golden = Golden.OrderBy(g => g.Method, StringComparer.Ordinal).ToList();
            var actual = server.Rpc.List().Select(m => (m.Method, m.Description ?? "", m.ReadOnly))
                .OrderBy(m => m.Method, StringComparer.Ordinal).ToList();
            Check.Equal(golden.Count, actual.Count, "the number of registered methods");
            for (var i = 0; i < Math.Min(golden.Count, actual.Count); i++)
                Check.Equal(golden[i], actual[i], golden[i].Method);
        });
    }
}
