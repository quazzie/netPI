using System;
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

    /// <summary>astra-1 P0.5: the single effective runtime home — see
    /// <see cref="NetPI.Abstractions.RuntimeHome"/> (NETPI_HOME aware).</summary>
    public static string DefaultRuntimeDirectory() => NetPI.Abstractions.RuntimeHome.Dir;

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

    /// <summary>
    /// Deep-merge <paramref name="properties"/> into <c>plugins:&lt;pluginId&gt;</c>
    /// in config.json and persist the file (PLAN §36).
    ///
    /// astra-1 §11a (P/B): a parse failure is REJECTED, never silently turned into
    /// a missing plugin section — the original bytes are retained and an
    /// <see cref="InvalidConfigException"/> is thrown with a useful error. The new
    /// config is validated as a complete object, written to a sibling temp file,
    /// then atomically replaced over the original, keeping a last-known-good
    /// copy. A revision check (write time + length) detects a concurrent writer
    /// between read and write and retries from a fresh read.
    /// </summary>
    public void MergePluginSection(string pluginId, JsonElement properties)
    {
        lock (_gate)
        {
            if (properties.ValueKind != JsonValueKind.Object) return;

            // Validate the incoming section up front so a bad payload never
            // touches the file.
            JsonNode.Parse(properties.ToString());

            for (int attempt = 0; ; attempt++)
            {
                // 1. Read + parse. A malformed file is a hard reject (retain bytes).
                JsonNode root;
                if (!File.Exists(_configPath))
                    root = new JsonObject();
                else
                {
                    var json = File.ReadAllText(_configPath);
                    if (string.IsNullOrWhiteSpace(json)) root = new JsonObject();
                    else
                    {
                        try { root = JsonNode.Parse(json)!; }
                        catch (JsonException ex)
                        {
                            throw new InvalidConfigException(
                                $"config.json at '{_configPath}' is not valid JSON; the original " +
                                $"bytes were retained and the update was not applied: {ex.Message}");
                        }
                    }
                    if (root is not JsonObject) root = new JsonObject();
                }
                var info = new FileInfo(_configPath);
                var writeTime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                var length = info.Exists ? info.Length : 0;

                // 2. Build the proposed complete config.
                if (root["plugins"] is not JsonObject plugins) root["plugins"] = plugins = new JsonObject();
                var key = pluginId.ToLowerInvariant();
                var target = plugins[key] as JsonObject ?? new JsonObject();
                DeepMergeInto(target, JsonNode.Parse(properties.ToString())!.AsObject());
                plugins[key] = target;
                var proposed = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

                // 3. Re-check the revision: a concurrent writer that touched the
                //    file between read and write invalidates this merge — retry.
                var rev = File.Exists(_configPath) ? new FileInfo(_configPath) : null;
                if (rev is { } ri && (ri.LastWriteTimeUtc != writeTime || ri.Length != length) && attempt < 3)
                {
                    continue; // re-read and merge again
                }

                // 4. Atomic replace: write a sibling temp, keep a last-known-good
                //    copy, then move the temp over the original.
                try
                {
                    AtomicWriteJson(proposed);
                }
                catch (Exception ex) when (ex is not InvalidConfigException)
                {
                    throw new InvalidConfigException(
                        $"could not persist config.json (the original bytes are unchanged): {ex.Message}", ex);
                }
                return;
            }
        }
    }

    /// <summary>Writes <paramref name="json"/> atomically: sibling temp file, then a
    /// last-known-good copy, then an atomic move over <see cref="_configPath"/>, so an
    /// interrupted write never leaves a partial config and a failure can restore the
    /// last known good bytes.</summary>
    private void AtomicWriteJson(string json)
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        var tmp = Path.Combine(dir, $".config.json.{Guid.NewGuid():n}.tmp");
        var lkg = Path.Combine(dir, ".config.json.last-known-good");
        File.WriteAllText(tmp, json);
        // Keep a recoverable last-known-good copy of the previous file (if any).
        if (File.Exists(_configPath))
        {
            try { File.Copy(_configPath, lkg, overwrite: true); }
            catch { /* LKG is best-effort; the atomic move below is what matters */ }
        }
        try
        {
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }
    private static void DeepMergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (k, v) in source)
        {
            if (v is JsonObject srcObj && target[k] is JsonObject dstObj)
                DeepMergeInto(dstObj, srcObj);
            else
                target[k] = v?.DeepClone();
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// astra-1 P2: detached deep copies of config JsonElements (LKG retention).
/// </summary>
public static class ConfigJson
{
    public static JsonElement DeepClone(this JsonElement element)
    {
        if (element.ValueKind is System.Text.Json.JsonValueKind.Undefined
            or System.Text.Json.JsonValueKind.Null)
            return JsonDocument.Parse("null").RootElement.Clone();
        var node = JsonNode.Parse(element.GetRawText())!;
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
}

/// <summary>
/// astra-1 §11a (P/B): a config update was REJECTED because the existing
/// config.json was malformed (or could not be written). The original bytes are
/// retained. The message is safe to surface to a client — it names no
/// credential values, only the path and the parse/write reason.
/// </summary>
public sealed class InvalidConfigException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
