using System.Text.Json;

namespace NetPI.Tools.Test;

/// <summary>The project's <c>.netpi/tests.json</c>: named suites (commands), a command for the changed files, the results file, a timeout.</summary>
internal sealed record TestConfig(IReadOnlyDictionary<string, string> Suites, string? Changed, string? Results, int TimeoutSeconds)
{
    public const string RelativePath = ".netpi/tests.json";
    public const int DefaultTimeoutSeconds = 1800;

    public const string HowToAdd =
        "Add " + RelativePath + " to the project: { \"suites\": { \"<name>\": \"<command>\" }, \"changed\": \"<command for the changed files>\", " +
        "\"results\": \"<path of a JSON results file the command writes>\", \"timeoutSeconds\"?: 1800 }. The results file holds " +
        "{ \"passed\": n, \"failed\": n, \"skipped\": n, \"failures\": [{ \"name\", \"message\", \"evidence\"? }], \"log\"?: \"<path>\" }. " +
        "The tool's only argument reaches the command in the NETPI_TEST_ONLY environment variable, and replaces {only} (quoted) where the command has it; " +
        "NETPI_TEST_RESULTS holds the results path.";

    /// <summary>The config of a workspace, or why there is none.</summary>
    public static (TestConfig? Config, string? Error) Load(string root)
    {
        var path = Path.Combine(root, ".netpi", "tests.json");
        if (!File.Exists(path)) return (null, $"No {RelativePath} in {root}. {HowToAdd}");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return (null, $"{RelativePath} must be a JSON object. {HowToAdd}");
            var suites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (o.TryGetProperty("suites", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) suites[p.Name] = p.Value.GetString()!;
            string? Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
            var timeout = o.TryGetProperty("timeoutSeconds", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) && n > 0 ? n : DefaultTimeoutSeconds;
            var config = new TestConfig(suites, Str("changed"), Str("results"), timeout);
            if (suites.Count == 0 && config.Changed is null) return (null, $"{RelativePath} names no suites and no changed command. {HowToAdd}");
            if (config.Results is null) return (null, $"{RelativePath} has no results (the path of the JSON results file the command writes). {HowToAdd}");
            // The tool deletes this file before every run: it has to be inside the checkout being tested.
            var relative = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, config.Results)));
            if (Path.IsPathRooted(config.Results) || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return (null, $"{RelativePath}: results ({config.Results}) must be a path inside the project folder.");
            return (config, null);
        }
        catch (JsonException ex) { return (null, $"{RelativePath} is not valid JSON: {ex.Message}"); }
        catch (IOException ex) { return (null, $"{RelativePath} could not be read: {ex.Message}"); }
    }
}
