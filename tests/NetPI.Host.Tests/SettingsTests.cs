using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;
using NetPI.Host.Settings;

namespace NetPI.Host.Tests;

public static class SettingsTests
{
    public sealed class ProviderOptions
    {
        public string? BaseUrl { get; set; }
        public string? Transport { get; set; }
    }

    public static void Register(TestRunner r)
    {
        r.Add("settings: first run writes the default document", () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            using var s = new SettingsStore(file, NullLogger.Instance);
            Check.True(File.Exists(file));
            var text = File.ReadAllText(file);
            Check.NotContains(text, "qwen", "nothing about one machine");
            Check.Equal(null, s.GetNode("defaultModel"), "no default model: the first loaded local model, else the first one");
            Check.Equal("http://127.0.0.1:8090", s.Get<string>("providers.aiproxy.baseUrl"));
            Check.Equal("responses", s.Get<string>("providers.aiproxy.transport"));
            Check.Equal("", s.Get<string>("providers.anthropic.apiKey"));
            Check.Equal(null, s.GetNode("agents"), "no agents: they are set up in the app");
            Check.Equal(null, s.GetNode("lanes"), "no lanes (they became agents)");
            Check.True(s.Get<bool>("compaction.enabled"));
            Check.True(s.Get<bool>("nudge.enabled"));
            Check.Equal(6, s.Get<int>("retry.maxAttempts"));
            Check.Equal(0, s.Get<List<string>>("tools.disabled")!.Count);
            Check.Equal(0, s.Get<List<string>>("plugins.disabled")!.Count);
            Check.Equal(7431, s.Get<int>("server.port"));
            Check.True(s.GetNode("ui") is JsonObject);
        });

        r.Add("settings: dotted paths, typed reads, defaults, removal", () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            File.WriteAllText(file, """
                {
                  // comments and trailing commas are allowed
                  "providers": { "aiproxy": { "baseUrl": "http://x:1", "transport": "chat", }, },
                  "list": [ { "name": "first" } ],
                }
                """);
            using var s = new SettingsStore(file, NullLogger.Instance);
            var opts = s.Get<ProviderOptions>("providers.aiproxy")!;
            Check.Equal("http://x:1", opts.BaseUrl);
            Check.Equal("chat", opts.Transport);
            Check.Equal("first", s.Get<string>("list.0.name"));
            Check.Equal("fallback", s.Get("missing.path", "fallback"));
            Check.Equal(0, s.Get<int>("providers.aiproxy.baseUrl"), "type mismatch → default");

            s.Set("a.b.c", JsonValue.Create(5));
            Check.Equal(5, s.Get<int>("a.b.c"));
            var node = s.GetNode("a")!;
            node["b"]!["c"] = 99; // a clone: must not affect the store
            Check.Equal(5, s.Get<int>("a.b.c"));
            s.Set("a.b.c", null);
            Check.True(s.GetNode("a.b.c") is null);
            Check.True(s.GetNode("a.b") is JsonObject);

            var onDisk = JsonNode.Parse(File.ReadAllText(file))!;
            Check.Equal("http://x:1", onDisk["providers"]!["aiproxy"]!["baseUrl"]!.GetValue<string>());
            Check.False(File.Exists(file + ".tmp"), "temp file moved into place");

            s.Replace(new JsonObject { ["only"] = true });
            Check.True(s.Get<bool>("only"));
            Check.True(s.GetNode("providers") is null);
        });

        r.Add("settings: changes publish settings.changed; external edits reload; invalid JSON keeps the last good doc", async () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            await using var bus = new EventBus(NullLogger.Instance);
            using var s = new SettingsStore(file, NullLogger.Instance);
            s.AttachBus(bus);
            s.StartWatching();
            var events = new List<string>();
            using var sub = bus.Subscribe(EventTypes.SettingsChanged, e => { lock (events) events.Add(((JsonObject)e.Data!).ToJsonString()); });

            s.Set("ui.theme", JsonValue.Create("dark"));
            await bus.FlushAsync();
            Check.Equal(1, events.Count);
            Check.Contains(events[0], "\"path\":\"ui.theme\"");
            s.Set("ui.theme", JsonValue.Create("dark")); // unchanged → no event
            await Task.Delay(400); // also: our own write must not come back through the watcher
            await bus.FlushAsync();
            Check.Equal(1, events.Count, "no event for an unchanged value or our own write");

            var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            doc["ui"]!["theme"] = "light";
            File.WriteAllText(file, "// edited by hand\n" + doc.ToJsonString());
            await Wait.UntilAsync(() => s.Get<string>("ui.theme") == "light", "external edit reloaded");
            await bus.FlushAsync();
            lock (events) Check.True(events.Any(e => e.Contains("\"source\":\"file\"")), "settings.changed from file");

            File.WriteAllText(file, "{ this is not json");
            await Task.Delay(700);
            Check.Equal("light", s.Get<string>("ui.theme"), "invalid JSON ignored");

            doc["ui"]!["theme"] = "blue";
            File.WriteAllText(file, doc.ToJsonString());
            await Wait.UntilAsync(() => s.Get<string>("ui.theme") == "blue", "valid JSON picked up again");
        });
    }
}
