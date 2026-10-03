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

        /// <summary>
        /// A core method; its requests are checked against <paramref name="parameters"/>, or against its row in
        /// <see cref="Declarations"/>, or — a method that takes nothing — against no parameters at all (idea-yvcy8b).
        /// </summary>
        public void Add(string method, string description, Func<RpcRequest, object?> handler, bool readOnly = false, RpcParam[]? parameters = null) =>
            _registrations.Add(K.Rpc.Register(new RpcMethod(method, description, readOnly, parameters ?? Declared(method)), (req, _) => Task.FromResult(handler(req))));

        public void AddAsync(string method, string description, Func<RpcRequest, CancellationToken, Task<object?>> handler, bool readOnly = false, RpcParam[]? parameters = null) =>
            _registrations.Add(K.Rpc.Register(new RpcMethod(method, description, readOnly, parameters ?? Declared(method)), (req, ct) => handler(req, ct)));

        private static RpcParam[] Declared(string method) => Declarations.TryGetValue(method, out var p) ? p : [];
    }

    private static RpcParam Req(string name, string type = RpcParamType.String) => RpcParam.Req(name, type);
    private static RpcParam Opt(string name, string type = RpcParamType.String) => RpcParam.Opt(name, type);

    /// <summary>
    /// The parameters of the core methods that take any, in one place (the descriptions say what they mean). A method
    /// missing here takes none, so a mistyped name in a request is a bad_request that names it, not a silent default.
    /// </summary>
    private static readonly Dictionary<string, RpcParam[]> Declarations = new(StringComparer.Ordinal)
    {
        ["projects.create"] = [Opt("name"), Req("path"), Opt("create", RpcParamType.Boolean)],
        ["projects.update"] = [Req("id"), Opt("name"), Opt("path"), Opt("create", RpcParamType.Boolean), Opt("meta", RpcParamType.Object)],
        ["projects.delete"] = [Req("id")],
        ["sessions.list"] =
        [
            Opt("projectId"), Opt("search"), Opt("includeSubagents", RpcParamType.Boolean), Opt("parentSessionId"),
            Opt("includeArchived", RpcParamType.Boolean), Opt("archivedOnly", RpcParamType.Boolean), Opt("attachedKey"), Opt("attachedValue"),
            Opt("includeUnmaterialized", RpcParamType.Boolean), Opt("limit", RpcParamType.Integer), Opt("offset", RpcParamType.Integer),
        ],
        ["sessions.create"] = [Opt("title"), Opt("projectId"), Opt("model"), Opt("reasoning")],
        ["sessions.fork"] = [Req("id"), Opt("upToSeq", RpcParamType.Integer)],
        ["sessions.get"] = [Req("id")],
        ["sessions.update"] =
        [
            Req("id"), Opt("title"), Opt("model"), Opt("reasoning"), Opt("archived", RpcParamType.Boolean),
            Opt("pinned", RpcParamType.Boolean), Opt("meta", RpcParamType.Object),
        ],
        ["sessions.delete"] = [Req("id")],
        ["sessions.setProject"] = [Req("id"), Opt("projectId")],
        ["models.list"] = [Opt("refresh", RpcParamType.Boolean)],
        ["ui.state.get"] = [Req("key")],
        ["ui.state.set"] = [Req("key"), Opt("value", RpcParamType.Any)],
        ["plugins.reload"] = [Req("id")],
        ["plugins.setEnabled"] = [Req("id"), Req("enabled", RpcParamType.Boolean)],
        ["settings.set"] = [Req("path"), Opt("value", RpcParamType.Any)],
        ["settings.replace"] = [Req("settings", RpcParamType.Object), Opt("base", RpcParamType.Object)],
        ["fs.dirs"] = [Opt("path")],
        ["events.recent"] = [Opt("max", RpcParamType.Integer)],
        ["logs.recent"] = [Opt("max", RpcParamType.Integer)],
    };

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
        a.Add("sessions.messages", "Message page: { id, beforeSeq? | afterSeq?, limit? (60) } → { messages, hasMore } ascending by seq. Without a cursor, or with beforeSeq, the newest messages before it (hasMore: older ones are left, follow with beforeSeq); with afterSeq, the oldest messages after it (hasMore: newer ones are left, follow with afterSeq = the last seq). The page is bounded in messages AND in serialized size: a page that overflows its byte budget comes back shorter with hasMore set.", req =>
        {
            var id = req.Required("id");
            a.K.Sessions.Require(id);
            var limit = Math.Clamp(req.Int("limit") ?? 60, 1, 2000);
            var before = req.Int64("beforeSeq");
            var after = req.Int64("afterSeq");
            if (before is not null && after is not null) throw new RpcException("bad_request", "sessions.messages: beforeSeq and afterSeq page in opposite directions; give one of them");
            var (messages, hasMore) = after is { } from
                ? MessagePageForward((seq, take) => a.K.Sessions.GetMessagesAfter(id, seq, take), from, limit)
                : MessagePage((b, take) => a.K.Sessions.GetMessages(id, b, take), before, limit);
            return new { messages, hasMore };
        }, readOnly: true, parameters:
        [
            RpcParam.Req("id", RpcParamType.String, "the session"),
            RpcParam.Opt("beforeSeq", RpcParamType.Integer, "older than this seq (exclusive)"),
            RpcParam.Opt("afterSeq", RpcParamType.Integer, "newer than this seq (exclusive)"),
            RpcParam.Opt("limit", RpcParamType.Integer, "at most this many messages (1–2000, default 60)"),
        ]);

        a.Add("sessions.stats", "Session-service counters: → { contextCache: { hits, reads } } — the cached contexts: a hit is a read served without touching the store, a read went to it", _ =>
        {
            var (hits, reads) = a.K.Sessions.ContextCache;
            return new { contextCache = new { hits, reads } };
        }, readOnly: true);
    }

    /// <summary>
    /// The page <c>sessions.messages</c> answers with: the NEWEST whole messages while they fit <see cref="MaxPageBytes"/>,
    /// ascending, and whether older ones are left (which the client follows with <c>beforeSeq</c>). <paramref name="read"/>
    /// reads the newest <c>limit</c> messages before a seq, ascending — the session store's page.
    /// <para>
    /// A page whose messages carry inline images can far exceed what one client can take in a single message (~25 images of
    /// 1.3 MB in 60 messages is already ~33 MB), and a message only turns out not to fit once it has been read and measured.
    /// So the page is read in growing windows, newest first, and stops at the first message that does not fit: 60 screenshots
    /// hand back the one message the budget takes instead of materializing (and measuring) all 60 to drop 59 — 1.2 GB of
    /// garbage for a 5 MB answer. The first message is always kept, however big it is: a chat that shows it must not show nothing.
    /// </para>
    /// </summary>
    internal static (List<ChatMessage> Messages, bool HasMore) MessagePage(Func<long?, int, IReadOnlyList<ChatMessage>> read, long? beforeSeq, int limit)
    {
        var taken = new List<ChatMessage>(limit);
        var size = 0;
        long? before = beforeSeq;
        var hasMore = false;
        var more = true;   // a window that came back full has older messages behind it; a short one is the last read
        for (var window = 1; taken.Count < limit && more;)
        {
            var batch = read(before, window);
            more = batch.Count == window;
            for (var i = batch.Count - 1; i >= 0 && taken.Count < limit; i--)
            {
                var m = batch[i];
                var len = Wire.SerializeValue(m).Length;
                if (taken.Count > 0 && size + len > MaxPageBytes) { hasMore = true; more = false; break; }
                taken.Add(m);
                size += len;
            }
            if (!more) break;
            // The next window asks for no more messages than the rest of the budget could hold (and grows like the one
            // before it), so a page of big messages is read one at a time instead of doubling into a window whose
            // messages are dropped again. taken.Count is at least 1: the window that came back full put a message on it.
            before = batch[0].Seq;
            var perMessage = Math.Max(1, size / taken.Count);
            window = Math.Min(Math.Min(window * 2, 1 + (MaxPageBytes - size) / perMessage), limit + 1 - taken.Count);
        }
        // the limit itself cut the page and there are older messages left: the client asks for the next beforeSeq
        if (taken.Count == limit && more) hasMore = true;
        taken.Reverse();
        return (taken, hasMore);
    }

    /// <summary>
    /// The forward page (idea-t6odez): the OLDEST whole messages after <paramref name="afterSeq"/> while they fit
    /// <see cref="MaxPageBytes"/>, ascending, and whether newer ones are left. Read in growing windows like
    /// <see cref="MessagePage"/>, so a run of big messages is not materialized to be dropped; the first message is always kept.
    /// </summary>
    internal static (List<ChatMessage> Messages, bool HasMore) MessagePageForward(Func<long, int, IReadOnlyList<ChatMessage>> readAfter, long afterSeq, int limit)
    {
        var taken = new List<ChatMessage>(limit);
        var size = 0;
        var after = afterSeq;
        var hasMore = false;
        var more = true;
        for (var window = 1; taken.Count < limit && more;)
        {
            var batch = readAfter(after, window);
            more = batch.Count == window;
            foreach (var m in batch)
            {
                if (taken.Count >= limit) break;
                var len = Wire.SerializeValue(m).Length;
                if (taken.Count > 0 && size + len > MaxPageBytes) { hasMore = true; more = false; break; }
                taken.Add(m);
                size += len;
            }
            if (!more || batch.Count == 0) break;
            after = batch[^1].Seq;
            var perMessage = Math.Max(1, size / taken.Count);
            window = Math.Min(Math.Min(window * 2, 1 + (MaxPageBytes - size) / perMessage), limit + 1 - taken.Count);
        }
        // the limit cut the page: look one past it, so hasMore is "newer messages exist", not "the page was full"
        if (taken.Count == limit && more) hasMore = readAfter(taken[^1].Seq, 1).Count > 0;
        return (taken, hasMore);
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
