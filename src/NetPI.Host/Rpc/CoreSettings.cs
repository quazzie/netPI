namespace NetPI.Host.Rpc;

/// <summary>The host's own settings for the settings dialog (<c>settings.schema</c>); plugins register theirs.</summary>
internal static class CoreSettings
{
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
                SettingInfo.Folder("workspace.default", "Default working folder", "For sessions without a project.", defaultWorkspace),
                SettingInfo.Choice("logging.level", "Log level", "Information", ["Trace", "Debug", "Information", "Warning", "Error"],
                    "Host log in ~/.netpi/logs."),
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
            ],
        },
    ];
}
