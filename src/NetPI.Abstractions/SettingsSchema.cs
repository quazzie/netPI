using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// One setting as the settings dialog shows it: a control per <see cref="Type"/>, saved with <c>settings.set</c>,
/// <see cref="Default"/> shown while the key is unset. Plugins describe their own settings in a <see cref="SettingsSection"/>
/// registered with <c>ctx.Services.Register(section)</c>, so hot reload removes it with the plugin.
/// </summary>
public sealed class SettingInfo
{
    /// <summary>Dotted path in settings.json, e.g. <c>shell.timeoutSeconds</c>.</summary>
    public required string Key { get; init; }
    /// <summary>bool | int | number | string | text (several lines) | secret | choice | list (of strings) | model | folder | file</summary>
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Help { get; init; }
    public JsonNode? Default { get; init; }
    /// <summary>What an unset field says when there is no fixed default ("auto", "first available", "env OPENROUTER_API_KEY").</summary>
    public string? Placeholder { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Shown after a number: "s", "%", "$", "tokens".</summary>
    public string? Unit { get; init; }
    /// <summary>For <c>choice</c>.</summary>
    public IReadOnlyList<string>? Options { get; init; }
    /// <summary>When a change applies, if not at once: "restart" or "new sessions".</summary>
    public string? Applies { get; init; }

    public static SettingInfo Bool(string key, string label, bool @default, string? help = null) =>
        new() { Key = key, Type = "bool", Label = label, Default = JsonValue.Create(@default), Help = help };

    public static SettingInfo Int(string key, string label, long? @default, string? help = null, double? min = null, double? max = null, string? unit = null) =>
        new() { Key = key, Type = "int", Label = label, Default = @default is { } d ? JsonValue.Create(d) : null, Help = help, Min = min, Max = max, Unit = unit };

    public static SettingInfo Number(string key, string label, double? @default, string? help = null, double? min = null, double? max = null, string? unit = null) =>
        new() { Key = key, Type = "number", Label = label, Default = @default is { } d ? JsonValue.Create(d) : null, Help = help, Min = min, Max = max, Unit = unit };

    public static SettingInfo Str(string key, string label, string? @default = null, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "string", Label = label, Default = @default is null ? null : JsonValue.Create(@default), Help = help, Placeholder = placeholder };

    public static SettingInfo Text(string key, string label, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "text", Label = label, Help = help, Placeholder = placeholder };

    public static SettingInfo Secret(string key, string label, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "secret", Label = label, Help = help, Placeholder = placeholder };

    public static SettingInfo Choice(string key, string label, string @default, IReadOnlyList<string> options, string? help = null) =>
        new() { Key = key, Type = "choice", Label = label, Default = JsonValue.Create(@default), Options = options, Help = help };

    public static SettingInfo List(string key, string label, IReadOnlyList<string>? @default = null, string? help = null, string? placeholder = null) =>
        new()
        {
            Key = key, Type = "list", Label = label, Help = help, Placeholder = placeholder,
            Default = @default is null ? null : new JsonArray([.. @default.Select(s => (JsonNode?)JsonValue.Create(s))]),
        };

    public static SettingInfo ModelRef(string key, string label, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "model", Label = label, Help = help, Placeholder = placeholder };

    public static SettingInfo Folder(string key, string label, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "folder", Label = label, Help = help, Placeholder = placeholder };

    public static SettingInfo FilePath(string key, string label, string? help = null, string? placeholder = null) =>
        new() { Key = key, Type = "file", Label = label, Help = help, Placeholder = placeholder };
}

/// <summary>A titled group of settings in the settings dialog (<c>settings.schema</c>).</summary>
public sealed class SettingsSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Where the dialog lists it: General, Models, Agents, Context, Tools.</summary>
    public string Group { get; init; } = "Tools";
    public int Order { get; init; }
    public string? Help { get; init; }
    public required IReadOnlyList<SettingInfo> Settings { get; init; }
}
