using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Host.Config;

/// <summary>
/// Small stable config surface for plugins: raw access to this plugin's own
/// section of ~/.netpi/config.json. Tolerant of unknown properties; plugins
/// own their own section (PLAN §35).
/// </summary>
public interface IConfigService
{
    /// <summary>The plugin's raw config section, or a <c>ValueKind.Null</c> element when absent.</summary>
    JsonElement GetRaw(string pluginId);

    /// <summary>The host's own section (<c>host</c> in config.json).</summary>
    JsonElement GetHostRaw();
}

/// <summary>
/// Configuration from ~/.netpi/config.json. The runtime directory is created
/// if missing and a default config.json is written on first run. The file is
/// re-read on every access, so config changes are picked up without explicit
/// reloads.
/// </summary>
public sealed class ConfigService : IConfigService, IDisposable
{
    private readonly object _gate = new();
    private readonly string _configPath;

    public string ConfigPath => _configPath;

    public ConfigService(string? runtimeDir = null)
    {
        var dir = runtimeDir ?? DefaultRuntimeDirectory();
        _configPath = Path.Combine(dir, "config.json");
        Directory.CreateDirectory(dir);
        if (!File.Exists(_configPath))
            File.WriteAllText(_configPath, DefaultConfigJson);
    }

    public static string DefaultRuntimeDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netpi");

    private const string DefaultConfigJson = """
    {
      "host": {
        "pluginDirectory": "./plugins"
      },
      "plugins": {
        "netpi.testplugin": {
          "generation": "gen1"
        }
      }
    }
    """;

    private JsonElement Resolve(string path)
    {
        lock (_gate)
        {
            if (!File.Exists(_configPath))
                return JsonDocument.Parse("null").RootElement.Clone();

            JsonNode? root;
            try
            {
                var json = File.ReadAllText(_configPath);
                root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return JsonDocument.Parse("null").RootElement.Clone();
            }

            // Walk "plugins:netpi.testplugin" (colon = JSON object key).
            var current = root;
            foreach (var part in path.Split(':'))
            {
                if (current is not JsonObject obj)
                    return JsonDocument.Parse("null").RootElement.Clone();
                current = obj[part];
            }

            if (current is null)
                return JsonDocument.Parse("null").RootElement.Clone();
            using var doc = JsonDocument.Parse(current.ToJsonString());
            return doc.RootElement.Clone();
        }
    }

    public JsonElement GetRaw(string pluginId) =>
        Resolve($"plugins:{pluginId.ToLowerInvariant()}");

    public JsonElement GetHostRaw() => Resolve("host");

    public void Dispose()
    {
    }
}
