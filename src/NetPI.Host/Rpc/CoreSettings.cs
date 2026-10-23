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
