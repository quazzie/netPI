using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Logging;
using NetPI.Host.Sessions;
using NetPI.Host.Web;

namespace NetPI.Host.Rpc;

/// <summary>The host's own RPC methods (see docs/PROTOCOL.md, "Core RPC methods").</summary>
internal static class CoreRpc
{
    private const string UiStatePrefix = "ui.";

    /// <summary>The serialized size a message page may grow to before it is split (see <c>sessions.messages</c>):
    /// well under the largest single WebSocket message a client can take (<c>WsClient.MaxPendingBytes</c>), the rest
    /// of the page comes back on the next <c>beforeSeq</c>.</summary>
    private const int MaxPageBytes = 8 * 1024 * 1024;

    public static void Register(HostKernel k, List<IDisposable> registrations)
    {
        var add = new Adder(k, registrations);
        RegisterApp(add);
        RegisterProjects(add);
        RegisterSessions(add);
        RegisterSessionMessages(add);
        RegisterModels(add);
        RegisterUi(add);
        RegisterPlugins(add);
        RegisterSettings(add);
        RegisterMisc(add);
    }

    /// <summary>
    /// How each section adds its methods: the kernel and the list that keeps its registrations alive. An answer that
    /// is computed right here goes through <see cref="Add"/>, one that awaits through <see cref="AddAsync"/>.
    /// </summary>
    private sealed class Adder
    {
        /// <summary>The kernel the sections read through.</summary>
        public readonly HostKernel K;
        private readonly List<IDisposable> _registrations;

        public Adder(HostKernel k, List<IDisposable> registrations)
        {
            K = k;
            _registrations = registrations;
        }

        public void Add(string method, string description, Func<RpcRequest, object?> handler, bool readOnly = false) =>
            _registrations.Add(K.Rpc.Register(method, (req, _) => Task.FromResult(handler(req)), description, readOnly));

        public void AddAsync(string method, string description, Func<RpcRequest, CancellationToken, Task<object?>> handler, bool readOnly = false) =>
            _registrations.Add(K.Rpc.Register(method, (req, ct) => handler(req, ct), description, readOnly));
    }

    // ------------------------------------------------------------ app
    private static void RegisterApp(Adder a) =>
        a.Add("app.info", "Host information → { version, os, home, appDir, defaultWorkspace, desktop, maxMessageBytes, ... }", _ => new
        {
            version = HostInfo.Version,
            os = HostInfo.OsName,
            osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            home = a.K.Paths.Home,
            appDir = a.K.Paths.AppDir,
            defaultWorkspace = a.K.Paths.DefaultWorkspace,
            settingsFile = a.K.Paths.SettingsFile,
            desktop = a.K.Options.Desktop,
            pid = Environment.ProcessId,
            dotnet = Environment.Version.ToString(),
            sqlite = a.K.Storage.Info is { Provider: "sqlite" } info ? info.Version : null,
            pathSeparator = Path.DirectorySeparatorChar.ToString(),
            maxMessageBytes = WsHub.MaxMessageBytes,
        }, readOnly: true);

    // ------------------------------------------------------------ projects
    private static void RegisterProjects(Adder a)
    {
        a.Add("projects.list", "All projects → ProjectInfo[]", _ => a.K.Sessions.ListProjects(), readOnly: true);

        a.Add("projects.create", "Create a project: { name, path, create? } → ProjectInfo", req =>
        {
            var path = ExistingDirectory(req.Required("path"), req.Bool("create") == true);
            return a.K.Sessions.CreateProject(req.Str("name") ?? "", path);
        });

        a.Add("projects.update", "Update a project: { id, name?, path?, meta? } → ProjectInfo (meta is merged key by key; a null value removes a key)", req =>
        {
            var path = req.Str("path") is { Length: > 0 } p ? ExistingDirectory(p, req.Bool("create") == true) : null;
            var meta = req.Prop("meta") is { ValueKind: JsonValueKind.Object } m ? JsonNode.Parse(m.GetRawText()) as JsonObject : null;
            return a.K.Sessions.UpdateProject(req.Required("id"), req.Str("name"), path, meta);
        });

        a.Add("projects.delete", "Delete a project (its sessions are detached): { id } → true", req =>
        {
            a.K.Sessions.DeleteProject(req.Required("id"));
            return true;
        });
    }

    // ------------------------------------------------------------ sessions
    private static void RegisterSessions(Adder a)
    {
        a.Add("sessions.list", "Sessions, pinned first then newest first: { projectId?, search?, includeSubagents?, parentSessionId?, includeArchived?, archivedOnly? (only archived; takes precedence over includeArchived), limit?, offset? } → SessionInfo[]",
            req => a.K.Sessions.ListSessions(req.Bind<SessionQuery>()), readOnly: true);

        a.Add("sessions.create", "Create a session: { title?, projectId?, model?, reasoning? } → SessionInfo", req =>
            a.K.Sessions.CreateSession(new SessionInfo
            {
                Title = req.Str("title") ?? "",
                ProjectId = req.Str("projectId"),
                Model = req.Str("model"),
                Reasoning = req.Str("reasoning"),
            }));

        a.Add("sessions.fork", "Fork a chat: a new chat with its messages up to a message, the original unchanged: { id, upToSeq? (the last) } → SessionInfo (publishes session.forked)", req =>
        {
            var id = req.Required("id");
            var from = a.K.Sessions.Require(id);
            if (from.Kind == "subagent") throw new RpcException("bad_request", "A subagent's chat can't be forked: fork the chat that started it.");
            var last = a.K.Sessions.GetMessages(id, null, 1).LastOrDefault()?.Seq ?? 0;
            var upTo = Math.Clamp(req.Int64("upToSeq") ?? last, 0, last);
            var context = SessionFork.ContextTokens(a.K.Sessions.GetMessages(id, upTo + 1, 50));
            var taken = a.K.Sessions.ListSessions(new SessionQuery { Search = SessionFork.BaseTitle(from.Title), IncludeArchived = true, Limit = 1000 })
                .Select(s => s.Title).ToHashSet(StringComparer.Ordinal);
            return a.K.Sessions.ForkSession(id, upTo, SessionFork.Template(from, upTo, context, a.K.Sessions.ForkResetKeys(), taken));
        });

        a.Add("sessions.get", "One session: { id } → SessionInfo", req =>
        {
            var id = req.Required("id");
            return a.K.Sessions.Require(id);
        }, readOnly: true);

        a.Add("sessions.update", "Update a session: { id, title?, model?, reasoning?, archived?, pinned?, meta? } → SessionInfo (null clears model/reasoning; meta is merged key by key and a null value removes a key, so the keys other plugins keep there survive)", req =>
            a.K.Sessions.UpdateSession(req.Required("id"), s =>
            {
                if (req.Prop("title") is { ValueKind: JsonValueKind.String } t) s.Title = t.GetString()!.Trim();
                if (req.Prop("model") is { } m) s.Model = m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } mv ? mv : null;
                if (req.Prop("reasoning") is { } r) s.Reasoning = r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rv ? rv : null;
                if (req.Bool("archived") is { } a) s.Archived = a;
                if (req.Bool("pinned") is { } p) s.Pinned = p;
                if (req.Prop("meta") is { } meta)
                {
                    // Merged, like a project's: workspace, plan, goal and tool-policy state all live in a session's meta, and a
                    // caller that sets one key must not wipe the keys the plugins own.
                    if (meta.ValueKind != JsonValueKind.Object) throw new RpcException("bad_request", "meta must be an object: { key: value } merges, { key: null } removes a key.");
                    s.Meta ??= new JsonObject();
                    foreach (var property in meta.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Null) s.Meta.Remove(property.Name);
                        else s.Meta[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                    if (s.Meta.Count == 0) s.Meta = null;
                }
            }));

        a.Add("sessions.delete", "Delete a session and its subagent sessions: { id } → true", req =>
        {
            a.K.Sessions.DeleteSession(req.Required("id"));
            return true;
        });

        a.Add("sessions.setProject", "Attach/detach a project: { id, projectId: string|null } → SessionInfo (publishes session.project)",
            req => a.K.Sessions.SetSessionProject(req.Required("id"), req.Str("projectId")));
    }

    private static void RegisterSessionMessages(Adder a)
    {
        a.Add("sessions.messages", "Message page: { id, beforeSeq?, limit? (60) } → { messages, hasMore } ascending by seq. The page is bounded in messages AND in serialized size: when the page overflows its byte budget it comes back shorter, with hasMore set and ending at an earlier seq, which the client follows with beforeSeq.", req =>
        {
            var id = req.Required("id");
            a.K.Sessions.Require(id);
            var limit = Math.Clamp(req.Int("limit") ?? 60, 1, 2000);
            var page = a.K.Sessions.GetMessages(id, req.Int64("beforeSeq"), limit + 1);
            var hasMore = page.Count > limit;
            if (hasMore) page = page.Skip(1).ToList();

            // A page whose messages carry inline images can far exceed what one client can take in a single message
            // (~25 images of 1.3 MB in 60 messages is already ~33 MB): keep the NEWEST whole messages while they fit
            // the budget (a page is what a client views now; the oldest is what it can still page back for with
            // beforeSeq) and let the first one that would not fit set hasMore.
            var taken = new List<ChatMessage>(page.Count);
            var size = 0;
            for (var i = page.Count - 1; i >= 0; i--)
            {
                var m = page[i];
                var len = Wire.SerializeValue(m).Length;
                if (taken.Count > 0 && size + len > MaxPageBytes) { hasMore = true; break; }
                taken.Add(m);
                size += len;
            }
            taken.Reverse();
            return new { messages = taken, hasMore };
        }, readOnly: true);

        a.Add("sessions.stats", "Session-service counters: → { contextCache: { hits, reads } } — the cached contexts: a hit is a read served without touching the store, a read went to it", _ =>
        {
            var (hits, reads) = a.K.Sessions.ContextCache;
            return new { contextCache = new { hits, reads } };
        }, readOnly: true);
    }

    // ------------------------------------------------------------ models
    private static void RegisterModels(Adder a) =>
        a.AddAsync("models.list", "Models of all providers: { refresh? } → { models, defaultModel }", async (req, ct) =>
        {
            var models = await a.K.Models.ListAsync(req.Bool("refresh") ?? false, ct).ConfigureAwait(false);
            return new { models, defaultModel = a.K.Models.DefaultModelRef };
        }, readOnly: true);

    // ------------------------------------------------------------ ui
    private static void RegisterUi(Adder a)
    {
        a.Add("ui.tabs", "Plugin UI tabs → UiTabInfo[]", _ => a.K.Ui.Tabs, readOnly: true);
        a.Add("ui.commands", "Slash commands → SlashCommandInfo[]", _ => a.K.Ui.Commands, readOnly: true);

        a.Add("ui.state.get", "Persisted UI state: { key } → JSON or null", req =>
        {
            var raw = a.K.Sessions.GetValue(UiStatePrefix + req.Required("key"));
            if (raw is null) return null;
            try { return JsonNode.Parse(raw); }
            catch (JsonException) { return null; }
        }, readOnly: true);

        a.Add("ui.state.set", "Persist UI state: { key, value } → true (null value deletes)", req =>
        {
            var key = req.Required("key");
            var value = req.Prop("value");
            a.K.Sessions.SetValue(UiStatePrefix + key,
                value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.Value.GetRawText());
            return true;
        });
    }

    // ------------------------------------------------------------ plugins
    private static void RegisterPlugins(Adder a)
    {
        a.Add("plugins.list", "Plugins → PluginInfo[]", _ => a.K.Plugins.List(), readOnly: true);

        a.AddAsync("plugins.reload", "Reload a plugin: { id } → true", async (req, ct) =>
        {
            await a.K.Plugins.ReloadAsync(req.Required("id"), ct).ConfigureAwait(false);
            return true;
        });

        a.AddAsync("plugins.setEnabled", "Enable/disable a plugin: { id, enabled } → true", async (req, ct) =>
        {
            var enabled = req.Bool("enabled") ?? throw new RpcException("bad_request", "Missing parameter 'enabled'");
            await a.K.Plugins.SetEnabledAsync(req.Required("id"), enabled, ct).ConfigureAwait(false);
            return true;
        });

        a.AddAsync("plugins.rescan", "Look for new/removed plugin folders → true", async (_, ct) =>
        {
            await a.K.Plugins.RescanAsync(ct).ConfigureAwait(false);
            return true;
        });
    }

    // ------------------------------------------------------------ settings
    private static void RegisterSettings(Adder a)
    {
        // settings.get is deliberately NOT read-only: it returns the document as it is, API keys and all (that is why
        // diag.settings redacts), so it must not be reachable from a tool. Everything marked below only reads.
        a.Add("settings.get", "Settings document → { path, settings }", _ => new { path = a.K.Settings.FilePath, settings = a.K.Settings.Snapshot() });

        a.Add("settings.set", "Set one value: { path (dotted), value } → true (null removes the key)", req =>
        {
            var path = req.Required("path");
            var value = req.Prop("value");
            a.K.Settings.Set(path, value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : JsonNode.Parse(value.Value.GetRawText()));
            return true;
        });

        a.Add("settings.replace", "Replace the whole document: { settings: object, base?: the document as loaded } → true. With base, a document that changed since the load is a conflict (409) instead of a lost update.", req =>
        {
            if (req.Prop("settings") is not { ValueKind: JsonValueKind.Object } s)
                throw new RpcException("bad_request", "'settings' must be a JSON object");
            // The compare and the replace are one store call under its writer lock: two saves that race on the same
            // base cannot both pass the compare, so the later one gets the conflict instead of a silent lost update.
            // base may hold credentials: it is compared in the store and is not logged or echoed here.
            var baseDoc = req.Prop("base") is { ValueKind: JsonValueKind.Object } b ? (JsonObject)JsonNode.Parse(b.GetRawText())! : null;
            if (a.K.Settings.Replace((JsonObject)JsonNode.Parse(s.GetRawText())!, baseDoc) is not SettingsReplace.Saved)
                throw new RpcException("conflict", "The settings changed after you loaded them; reload and save again");
            return true;
        });

        a.Add("settings.schema", "The settings the dialog shows as controls → SettingsSection[] (host and plugins, by group and order)", _ =>
        {
            string[] groups = ["General", "Models", "Agents", "Context", "Tools"];
            int Rank(string g) => Array.IndexOf(groups, g) is var i and >= 0 ? i : groups.Length;
            return CoreSettings.Sections(Path.Combine(a.K.Paths.Home, "workspace")).Concat(a.K.Services.GetAll<SettingsSection>())
                .OrderBy(s => Rank(s.Group)).ThenBy(s => s.Order).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, readOnly: true);
    }

    // ------------------------------------------------------------ misc
    private static void RegisterMisc(Adder a)
    {
        a.Add("fs.dirs", "Folder picker: { path? } → { path, parent, dirs: {name,path}[], roots }", req => ListDirectories(req.Str("path")), readOnly: true);

        a.Add("tools.list", "Tool registrations → { name, label, description, category, readOnly, pluginId, active, disabled, priority }[]", _ =>
        {
            var active = new HashSet<IAgentTool>(a.K.Tools.All, ReferenceEqualityComparer.Instance);
            return a.K.Tools.Registrations.Select(r =>
            {
                var d = r.Tool.Definition;
                return new
                {
                    name = d.Name, label = d.Label, description = d.Description, category = d.Category, readOnly = d.ReadOnly,
                    pluginId = r.PluginId, active = active.Contains(r.Tool), disabled = a.K.Tools.IsDisabled(d.Name), priority = r.Priority,
                };
            }).ToList();
        }, readOnly: true);

        a.Add("rpc.list", "RPC methods → { method, description, pluginId }[]", _ => a.K.Rpc.List(), readOnly: true);

        a.Add("services.list", "Registered services (diagnostics)", _ => a.K.Services.List(), readOnly: true);

        a.AddAsync("events.flush", "Wait until every event published before this call has been delivered: its answer follows them on the same socket → true", async (_, ct) =>
        {
            await a.K.Bus.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return true;
        }, readOnly: true);

        a.Add("events.recent", "Recent bus events: { max? } → { type, sid, d, seq, ts, source }[]", req =>
        {
            var max = Math.Clamp(req.Int("max") ?? 200, 1, 500);
            return a.K.Bus.Recent(max).Select(e => new
            {
                type = e.Type, sid = e.SessionId, d = SafeElement(e.Data), seq = e.Seq, ts = e.Time.ToUnixTimeMilliseconds(), source = e.Source, ui = e.Ui,
            }).ToList();
        }, readOnly: true);

        a.Add("logs.recent", "Recent log entries: { max? } → { time, level, category, message, exception? }[]", req =>
        {
            var max = Math.Clamp(req.Int("max") ?? 200, 1, 2000);
            return a.K.LogSink.Recent(max).Select(e => new
            {
                time = e.Time, level = LogSink.LevelTag(e.Level).ToLowerInvariant(), category = e.Category, message = e.Message, exception = e.Exception,
            }).ToList();
        }, readOnly: true);
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
