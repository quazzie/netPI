namespace NetPI.Host.Rpc;

/// <summary>The host's own settings for the settings dialog (<c>settings.schema</c>); plugins register theirs.</summary>
internal static class CoreSettings
{
    /// <summary>What <c>plugins.quiet</c> means while the key is unset (the first-run document and <c>PluginManager</c> read it).</summary>
    public const bool QuietDefault = false;

    /// <param name="defaultWorkspace">The working folder of sessions without a project when the setting is empty.</param>
    public static IReadOnlyList<SettingsSection> Sections(string defaultWorkspace) =>
    [
        new SettingsSection
        {
            Id = "core",
            Title = "NetPI",
            Group = "General",
            Settings =
            [
                SettingInfo.ModelRef("defaultModel", "Default model", "For sessions that have none.", "first available"),
                SettingInfo.Int("models.refreshSeconds", "Refresh the model list every", 10, "Model states (a local model loaded or not) follow within this time; agents are active only while their model is loaded. 0 = only on changes.", 0, 3600, "s"),
                SettingInfo.Folder("workspace.default", "Default working folder", "For sessions without a project.", defaultWorkspace),
                SettingInfo.Choice("logging.level", "Log level", "Information", ["Trace", "Debug", "Information", "Warning", "Error"],
                    "Host log in ~/.netpi/logs."),
                SettingInfo.Bool("plugins.quiet", "Quiet plugin reloads", QuietDefault,
                    "While on, a plugin reload is recorded and not applied: the running version keeps serving, so nothing swaps under a running chat. Switching it off applies everything that piled up."),
                new SettingInfo
                {
                    Key = "server.port", Type = "int", Label = "Port", Default = System.Text.Json.Nodes.JsonValue.Create(7431),
                    Min = 1, Max = 65535, Applies = "restart", Help = "A free port is used when it is taken.",
                },
                new SettingInfo
                {
                    Key = "server.devOrigins", Type = "list", Label = "Extra allowed origins", Applies = "restart",
                    Help = "e.g. http://localhost:5173 for npm run dev.",
                },
                new SettingInfo
                {
                    Key = "database.sqlitePath", Type = "file", Label = "SQLite library", Applies = "restart",
                    Placeholder = "winsqlite3.dll / libsqlite3",
                    Help = "Explicit library to load (winsqlite3.dll on Windows, libsqlite3 elsewhere). Empty = the default; env NETPI_SQLITE wins.",
                },
            ],
        },
    ];
}
