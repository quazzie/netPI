namespace NetPI.Host.Rpc;

/// <summary>The host's own settings for the settings dialog (<c>settings.schema</c>); plugins register theirs.</summary>
internal static class CoreSettings
{
    /// <summary>What <c>plugins.quiet</c> means while the key is unset (the first-run document and <c>PluginManager</c> read it).</summary>
    public const bool QuietDefault = false;

    /// <param name="defaultWorkspace">The working folder of sessions without a project when the setting is empty.</param>
    /// <summary>The dialog's pages in their order (<see cref="SettingsSection.Group"/>); <c>settings.schema</c> sorts by it.</summary>
    public static readonly string[] Groups = ["General", "Models", "Agents", "Runs", "Context", "Tools", "Knowledge", "Automation", "Safety", "Data", "Advanced"];

    /// <param name="defaultWorkspace">The working folder of sessions without a project when the setting is empty.</param>
    public static IReadOnlyList<SettingsSection> Sections(string defaultWorkspace) =>
    [
        new SettingsSection
        {
            Id = "defaults",
            Title = "Defaults",
            Group = "General",
            Order = 50,
            Settings =
            [
                SettingInfo.Folder("workspace.default", "Default working folder", "For sessions without a project.", defaultWorkspace)
                    with { Keywords = ["workspace", "cwd", "directory"] },
            ],
        },
        new SettingsSection
        {
            Id = "modelList",
            Title = "Model list",
            Group = "Models",
            Order = 5,
            Settings =
            [
                SettingInfo.ModelRef("defaultModel", "Default model", "For sessions that have none.", "first available"),
                SettingInfo.Int("models.refreshSeconds", "Refresh the model list every", 10, "Model states (a local model loaded or not) follow within this time; agents are active only while their model is loaded. 0 = only on changes.", 0, 3600, "s"),
            ],
        },
        new SettingsSection
        {
            Id = "core",
            Title = "Host",
            Group = "Advanced",
            Order = 10,
            Help = "The NetPI process itself: its log, plugin reloads, the port and the database library.",
            Settings =
            [
                SettingInfo.Choice("logging.level", "Log level", "Information", ["Trace", "Debug", "Information", "Warning", "Error"],
                    "Host log in ~/.netpi/logs.") with { Subgroup = "Logging and plugins" },
                SettingInfo.Bool("plugins.quiet", "Quiet plugin reloads", QuietDefault,
                    "While on, a plugin reload is recorded and not applied: the running version keeps serving, so nothing swaps under a running chat. Switching it off applies everything that piled up.")
                    with { Subgroup = "Logging and plugins", Keywords = ["hot reload", "defer"] },
                SettingInfo.Bool("host.autoRestart", "Restart by itself to install an update", true,
                    "When a publish left something for the next start (new host files, plugins built against changed contracts, -NextStart), NetPI restarts once nothing goes on: no run, active or queued, no process the shell started, and no change from you for the quiet time below. A desktop window closes and opens again; a browser tab reconnects.")
                    with { Subgroup = "Updates", Keywords = ["restart", "update", "pending", "publish"] },
                SettingInfo.Int("host.autoRestartQuietSeconds", "Quiet time before it restarts", RestartWhenIdle.DefaultQuietSeconds,
                    "How long nothing may have changed (a message sent, a setting saved, anything you do that writes) before the restart.", 10, 3600, "s")
                    with { Subgroup = "Updates", Advanced = true },
                new SettingInfo
                {
                    Key = "server.port", Type = "int", Label = "Port", Default = System.Text.Json.Nodes.JsonValue.Create(7431),
                    Min = 1, Max = 65535, Applies = "restart", Help = "A free port is used when it is taken.",
                    Subgroup = "Server", Keywords = ["network", "http", "listen"],
                },
                new SettingInfo
                {
                    Key = "server.devOrigins", Type = "list", Label = "Extra allowed origins", Applies = "restart",
                    Help = "e.g. http://localhost:5173 for npm run dev.", Subgroup = "Server", Keywords = ["cors", "vite", "dev server"],
                },
                SettingInfo.Str("server.bind", "Listen on", null,
                    "What the server listens on: loopback (the default, 127.0.0.1), all (every interface — a client on the LAN), or an IP address. Beyond loopback the token rides the wire in the clear: keep the server to a trusted LAN, and let its clients through with server.publicOrigins when the origin is not the server's own address.",
                    "loopback, all or an IP address (e.g. 192.168.1.5)") with { Applies = "restart", Subgroup = "Server", Keywords = ["bind", "listen", "lan", "remote", "network"] },
                SettingInfo.List("server.publicOrigins", "Allowed origins (beyond loopback)", null,
                    "Origins a server bound beyond loopback accepts on top of this machine's own addresses, in the same shape as server.devOrigins (full origins, * for any): the hostname of a reverse proxy, a second LAN address of the machine. A loopback-bound server ignores it.") with { Applies = "restart", Subgroup = "Server", Keywords = ["origin", "cors", "lan", "remote", "proxy"] },
                new SettingInfo
                {
                    Key = "database.sqlitePath", Type = "file", Label = "SQLite library", Applies = "restart",
                    Placeholder = "winsqlite3.dll / libsqlite3", Subgroup = "Storage", Keywords = ["database", "dll"],
                    Help = "Explicit library to load (winsqlite3.dll on Windows, libsqlite3 elsewhere). Empty = the default; env NETPI_SQLITE wins.",
                },
            ],
        },
    ];
}
