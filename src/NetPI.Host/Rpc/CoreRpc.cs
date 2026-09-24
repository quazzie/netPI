using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Data;
using NetPI.Host.Logging;
using NetPI.Host.Sessions;

namespace NetPI.Host.Rpc;

/// <summary>The host's own RPC methods (see docs/PROTOCOL.md, "Core RPC methods").</summary>
internal static class CoreRpc
{
    private const string UiStatePrefix = "ui.";

    public static void Register(HostKernel k, List<IDisposable> registrations)
    {
        void Add(string method, string description, Func<RpcRequest, object?> handler) =>
            registrations.Add(k.Rpc.Register(method, (req, _) => Task.FromResult(handler(req)), description));

        void AddAsync(string method, string description, Func<RpcRequest, CancellationToken, Task<object?>> handler) =>
            registrations.Add(k.Rpc.Register(method, (req, ct) => handler(req, ct), description));

        // ------------------------------------------------------------ app
        Add("app.info", "Host information → { version, os, home, appDir, defaultWorkspace, desktop, ... }", _ => new
        {
            version = HostInfo.Version,
            os = HostInfo.OsName,
            osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            home = k.Paths.Home,
            appDir = k.Paths.AppDir,
            defaultWorkspace = k.Paths.DefaultWorkspace,
            settingsFile = k.Paths.SettingsFile,
            desktop = k.Options.Desktop,
            pid = Environment.ProcessId,
            dotnet = Environment.Version.ToString(),
            sqlite = Sqlite3.Version,
            pathSeparator = Path.DirectorySeparatorChar.ToString(),
        });

        // ------------------------------------------------------------ projects
        Add("projects.list", "All projects → ProjectInfo[]", _ => k.Sessions.ListProjects());

        Add("projects.create", "Create a project: { name, path, create? } → ProjectInfo", req =>
        {
            var path = ExistingDirectory(req.Required("path"), req.Bool("create") == true);
            return k.Sessions.CreateProject(req.Str("name") ?? "", path);
        });

        Add("projects.update", "Update a project: { id, name?, path?, meta? } → ProjectInfo (meta is merged key by key; a null value removes a key)", req =>
        {
            var path = req.Str("path") is { Length: > 0 } p ? ExistingDirectory(p, req.Bool("create") == true) : null;
            var meta = req.Prop("meta") is { ValueKind: JsonValueKind.Object } m ? JsonNode.Parse(m.GetRawText()) as JsonObject : null;
            return k.Sessions.UpdateProject(req.Required("id"), req.Str("name"), path, meta);
        });

        Add("projects.delete", "Delete a project (its sessions are detached): { id } → true", req =>
        {
            k.Sessions.DeleteProject(req.Required("id"));
            return true;
        });

        // ------------------------------------------------------------ sessions
        Add("sessions.list", "Sessions, newest first: { projectId?, search?, includeSubagents?, parentSessionId?, includeArchived?, limit?, offset? } → SessionInfo[]",
            req => k.Sessions.ListSessions(req.Bind<SessionQuery>()));

        Add("sessions.create", "Create a session: { title?, projectId?, model?, reasoning? } → SessionInfo", req =>
            k.Sessions.CreateSession(new SessionInfo
            {
                Title = req.Str("title") ?? "",
                ProjectId = req.Str("projectId"),
                Model = req.Str("model"),
                Reasoning = req.Str("reasoning"),
            }));

        Add("sessions.get", "One session: { id } → SessionInfo", req =>
        {
            var id = req.Required("id");
            return k.Sessions.GetSession(id) ?? throw new RpcException("not_found", $"Session {id} not found");
        });

        Add("sessions.update", "Update a session: { id, title?, model?, reasoning?, archived?, meta? } → SessionInfo (null clears model/reasoning)", req =>
            k.Sessions.UpdateSession(req.Required("id"), s =>
            {
                if (req.Prop("title") is { ValueKind: JsonValueKind.String } t) s.Title = t.GetString()!.Trim();
                if (req.Prop("model") is { } m) s.Model = m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } mv ? mv : null;
                if (req.Prop("reasoning") is { } r) s.Reasoning = r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rv ? rv : null;
                if (req.Bool("archived") is { } a) s.Archived = a;
                if (req.Prop("meta") is { } meta) s.Meta = meta.ValueKind == JsonValueKind.Object ? JsonNode.Parse(meta.GetRawText()) as JsonObject : null;
            }));

        Add("sessions.delete", "Delete a session and its subagent sessions: { id } → true", req =>
        {
            k.Sessions.DeleteSession(req.Required("id"));
            return true;
        });

        Add("sessions.setProject", "Attach/detach a project: { id, projectId: string|null } → SessionInfo (publishes session.project)",
            req => k.Sessions.SetSessionProject(req.Required("id"), req.Str("projectId")));

        Add("sessions.messages", "Message page: { id, beforeSeq?, limit? (60) } → { messages, hasMore } ascending by seq", req =>
        {
            var id = req.Required("id");
            if (k.Sessions.GetSession(id) is null) throw new RpcException("not_found", $"Session {id} not found");
            var limit = Math.Clamp(req.Int("limit") ?? 60, 1, 2000);
            var page = k.Sessions.GetMessages(id, req.Int64("beforeSeq"), limit + 1);
            var hasMore = page.Count > limit;
            return new { messages = hasMore ? page.Skip(1).ToList() : page, hasMore };
        });

        // ------------------------------------------------------------ models
        AddAsync("models.list", "Models of all providers: { refresh? } → { models, defaultModel }", async (req, ct) =>
        {
            var models = await k.Models.ListAsync(req.Bool("refresh") ?? false, ct).ConfigureAwait(false);
            return new { models, defaultModel = k.Models.DefaultModelRef };
        });

        // ------------------------------------------------------------ ui
        Add("ui.tabs", "Plugin UI tabs → UiTabInfo[]", _ => k.Ui.Tabs);
        Add("ui.commands", "Slash commands → SlashCommandInfo[]", _ => k.Ui.Commands);

        Add("ui.state.get", "Persisted UI state: { key } → JSON or null", req =>
        {
            var raw = k.Sessions.GetValue(UiStatePrefix + req.Required("key"));
            if (raw is null) return null;
            try { return JsonNode.Parse(raw); }
            catch (JsonException) { return null; }
        });

        Add("ui.state.set", "Persist UI state: { key, value } → true (null value deletes)", req =>
        {
            var key = req.Required("key");
            var value = req.Prop("value");
            k.Sessions.SetValue(UiStatePrefix + key,
                value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.Value.GetRawText());
            return true;
        });

        // ------------------------------------------------------------ plugins
        Add("plugins.list", "Plugins → PluginInfo[]", _ => k.Plugins.List());

        AddAsync("plugins.reload", "Reload a plugin: { id } → true", async (req, ct) =>
        {
            await k.Plugins.ReloadAsync(req.Required("id"), ct).ConfigureAwait(false);
            return true;
        });

        AddAsync("plugins.setEnabled", "Enable/disable a plugin: { id, enabled } → true", async (req, ct) =>
        {
            var enabled = req.Bool("enabled") ?? throw new RpcException("bad_request", "Missing parameter 'enabled'");
            await k.Plugins.SetEnabledAsync(req.Required("id"), enabled, ct).ConfigureAwait(false);
            return true;
        });

        AddAsync("plugins.rescan", "Look for new/removed plugin folders → true", async (_, ct) =>
        {
            await k.Plugins.RescanAsync(ct).ConfigureAwait(false);
            return true;
        });

        // ------------------------------------------------------------ settings
        Add("settings.get", "Settings document → { path, settings }", _ => new { path = k.Settings.FilePath, settings = k.Settings.Snapshot() });

        Add("settings.set", "Set one value: { path (dotted), value } → true (null removes the key)", req =>
        {
            var path = req.Required("path");
            var value = req.Prop("value");
            k.Settings.Set(path, value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : JsonNode.Parse(value.Value.GetRawText()));
            return true;
        });

        Add("settings.replace", "Replace the whole document: { settings: object } → true", req =>
        {
            if (req.Prop("settings") is not { ValueKind: JsonValueKind.Object } s)
                throw new RpcException("bad_request", "'settings' must be a JSON object");
            k.Settings.Replace((JsonObject)JsonNode.Parse(s.GetRawText())!);
            return true;
        });

        Add("settings.schema", "The settings the dialog shows as controls → SettingsSection[] (host and plugins, by group and order)", _ =>
        {
            string[] groups = ["General", "Models", "Agents", "Context", "Tools"];
            int Rank(string g) => Array.IndexOf(groups, g) is var i and >= 0 ? i : groups.Length;
            return CoreSettings.Sections.Concat(k.Services.GetAll<SettingsSection>())
                .OrderBy(s => Rank(s.Group)).ThenBy(s => s.Order).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        // ------------------------------------------------------------ misc
        Add("fs.dirs", "Folder picker: { path? } → { path, parent, dirs: {name,path}[], roots }", req => ListDirectories(req.Str("path")));

        Add("tools.list", "Tool registrations → { name, label, description, category, readOnly, pluginId, active, disabled, priority }[]", _ =>
        {
            var active = new HashSet<IAgentTool>(k.Tools.All, ReferenceEqualityComparer.Instance);
            return k.Tools.Registrations.Select(r =>
            {
                var d = r.Tool.Definition;
                return new
                {
                    name = d.Name, label = d.Label, description = d.Description, category = d.Category, readOnly = d.ReadOnly,
                    pluginId = r.PluginId, active = active.Contains(r.Tool), disabled = k.Tools.IsDisabled(d.Name), priority = r.Priority,
                };
            }).ToList();
        });

        Add("rpc.list", "RPC methods → { method, description, pluginId }[]", _ => k.Rpc.List());

        Add("services.list", "Registered services (diagnostics)", _ => k.Services.List());

        Add("events.recent", "Recent bus events: { max? } → { type, sid, d, seq, ts, source }[]", req =>
        {
            var max = Math.Clamp(req.Int("max") ?? 200, 1, 500);
            return k.Bus.Recent(max).Select(e => new
            {
                type = e.Type, sid = e.SessionId, d = SafeElement(e.Data), seq = e.Seq, ts = e.Time.ToUnixTimeMilliseconds(), source = e.Source, ui = e.Ui,
            }).ToList();
        });

        Add("logs.recent", "Recent log entries: { max? } → { time, level, category, message, exception? }[]", req =>
        {
            var max = Math.Clamp(req.Int("max") ?? 200, 1, 2000);
            return k.LogSink.Recent(max).Select(e => new
            {
                time = e.Time, level = LogSink.LevelTag(e.Level).ToLowerInvariant(), category = e.Category, message = e.Message, exception = e.Exception,
            }).ToList();
        });
    }

    private static string ExistingDirectory(string path, bool create)
    {
        var full = PathUtil.Expand(path);
        if (Directory.Exists(full)) return full;
        if (!create) throw new RpcException("bad_request", $"Directory does not exist: {full}");
        Directory.CreateDirectory(full);
        return full;
    }

    private static object ListDirectories(string? requested)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = string.IsNullOrWhiteSpace(requested) ? home : requested.Trim();
        if (OperatingSystem.IsWindows() && path.Length == 2 && path[1] == ':') path += "\\";
        path = PathUtil.Expand(path);
        if (!Directory.Exists(path)) throw new RpcException("not_found", $"Directory not found: {path}");
        var dir = new DirectoryInfo(path);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        List<object> dirs;
        try
        {
            dirs = dir.EnumerateDirectories("*", options)
                .Where(d => !d.Name.StartsWith('.') && !d.Name.StartsWith('$'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => (object)new { name = d.Name, path = d.FullName })
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            throw new RpcException("forbidden", $"Access denied: {path}");
        }
        var roots = OperatingSystem.IsWindows() ? Directory.GetLogicalDrives() : new[] { "/", home };
        return new { path = dir.FullName, parent = dir.Parent?.FullName, dirs, roots };
    }

    private static JsonElement? SafeElement(object? data)
    {
        if (data is null) return null;
        try { return NetPiJson.ToElement(data); }
        catch { return null; }
    }
}
