using System.Text.Json;
using NetPI.Host.Config;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §11a (P/B): config updates must not destroy existing settings.
/// <see cref="ConfigService.MergePluginSection"/> used to treat a malformed
/// config.json as an empty object and overwrite the file, discarding every other
/// setting. Now it REJECTS a malformed config (retaining the original bytes) and
/// writes a VALID one atomically (temp + atomic move, last-known-good copy).
/// </summary>
public sealed class ConfigIntegrityTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "netpi-cfg-" + Guid.NewGuid().ToString("n"));
    private string ConfigPath => Path.Combine(_home, "config.json");

    public ConfigIntegrityTests() => Directory.CreateDirectory(_home);

    private void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);
    private string ReadConfig() => File.ReadAllText(ConfigPath);

    public void Dispose() { try { Directory.Delete(_home, recursive: true); } catch { } }

    private static JsonElement Obj(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement.Clone();

    /// <summary>The core defect: a malformed config must be REJECTED (original bytes
    /// retained, no settings lost), not silently rebuilt from just the update.</summary>
    [Fact]
    public void MergePluginSection_MalformedConfig_Rejects_AndKeepsOriginalBytes()
    {
        WriteConfig("{ \"host\": {\"pluginDirectory\": \"./plugins\"}, this is not valid json");
        var before = ReadConfig();

        var ex = Assert.Throws<InvalidConfigException>(() =>
            new ConfigService(_home).MergePluginSection("netpi.web", Obj(new { port = 5173 })));

        Assert.Contains("not valid JSON", ex.Message);
        // Original bytes are EXACTLY retained — no silent overwrite.
        Assert.Equal(before, ReadConfig());
    }

    /// <summary>A valid merge preserves every other top-level + plugin setting and
    /// leaves a valid JSON file, plus a last-known-good copy.</summary>
    [Fact]
    public void MergePluginSection_ValidUpdate_PreservesOtherSettings_AndWritesAtomicLkg()
    {
        WriteConfig("""
            {
              "host": { "pluginDirectory": "./plugins" },
              "plugins": { "netpi.web": { "port": 5173 }, "netpi.retry": { "enabled": false } }
            }
            """);
        var before = ReadConfig();

        new ConfigService(_home).MergePluginSection("netpi.web", Obj(new { port = 9999 }));

        var after = ReadConfig();
        using var doc = JsonDocument.Parse(after); // still valid JSON
        var plugins = doc.RootElement.GetProperty("plugins");
        // The updated section is merged (not replaced wholesale).
        Assert.Equal(9999, plugins.GetProperty("netpi.web").GetProperty("port").GetInt32());
        // Sibling plugin settings are preserved.
        Assert.False(plugins.GetProperty("netpi.retry").GetProperty("enabled").GetBoolean());
        // The host section is preserved.
        Assert.Equal("./plugins", doc.RootElement.GetProperty("host").GetProperty("pluginDirectory").GetString());
        Assert.NotSame(before, after); // the file actually changed
        // A recoverable last-known-good copy was written.
        Assert.True(File.Exists(Path.Combine(_home, ".config.json.last-known-good")));
    }

    /// <summary>Deep-merge, not replace: an update to one key keeps the other keys
    /// already present in that same plugin section.</summary>
    [Fact]
    public void MergePluginSection_DeepMerges_KeepsExistingKeysInSameSection()
    {
        WriteConfig("""
            { "plugins": { "netpi.autocompact": { "enabled": true, "reserveTokens": 16384 } } }
            """);
        new ConfigService(_home).MergePluginSection("netpi.autocompact", Obj(new { keepRecentTokens = 20000 }));

        var ac = JsonDocument.Parse(ReadConfig()).RootElement.GetProperty("plugins").GetProperty("netpi.autocompact");
        Assert.True(ac.GetProperty("enabled").GetBoolean());        // kept
        Assert.Equal(16384, ac.GetProperty("reserveTokens").GetInt32()); // kept
        Assert.Equal(20000, ac.GetProperty("keepRecentTokens").GetInt32()); // added
    }

    /// <summary>A fresh (bootstrapped) config merges cleanly and is valid JSON.
    /// Malformed files (above) are rejected; a well-formed one is not.</summary>
    [Fact]
    public void MergePluginSection_ExistingDefaultConfig_Merges()
    {
        // The constructor bootstraps a default config.json if absent; the merge works on it.
        var svc = new ConfigService(_home);
        svc.MergePluginSection("netpi.web", Obj(new { port = 5173 }));
        var web = JsonDocument.Parse(ReadConfig()).RootElement.GetProperty("plugins").GetProperty("netpi.web");
        Assert.Equal(5173, web.GetProperty("port").GetInt32());
    }
}
