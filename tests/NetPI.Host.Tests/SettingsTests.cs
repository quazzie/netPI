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
            Check.Equal(0, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length, "the temp file moved into place");

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

        r.Add("settings: concurrent writers on distinct keys all reach the file", () =>
        {
            var dir = T.TempDir("settings");
            var file = Path.Combine(dir, "settings.json");
            using var s = new SettingsStore(file, NullLogger.Instance);
            const int rounds = 6, writers = 16;
            Parallel.For(0, rounds, round =>
            {
                for (var i = 0; i < writers; i++) s.Set($"conc.{round}.{i}", JsonValue.Create(round * 1000 + i));
            });
            var onDisk = JsonNode.Parse(File.ReadAllText(file))!["conc"]!.AsObject();
            for (var round = 0; round < rounds; round++)
                for (var i = 0; i < writers; i++)
                    Check.Equal(round * 1000 + i, onDisk[round.ToString()]![i.ToString()]!.GetValue<int>(), $"round {round}, key {i}");
            Check.Equal(0, Directory.GetFiles(dir, "*.tmp").Length, "no temp file left behind");
        });

        r.Add("settings: the file matches the live document whatever the mix of writers", () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            using var s = new SettingsStore(file, NullLogger.Instance);
            Parallel.For(0, 12, i =>
            {
                if (i % 3 == 0)
                {
                    var doc = s.Snapshot();
                    doc["mix"] = JsonValue.Create(i);   // a whole-document write racing the single-key ones
                    s.Replace(doc);
                }
                else s.Set($"mix.{i}", JsonValue.Create(i));
            });
            // The invariant: what is on disk is the live document, in the order the writes were made.
            Check.True(JsonNode.DeepEquals(s.Snapshot(), JsonNode.Parse(File.ReadAllText(file))), "memory == disk");
        });

        r.Add("settings: a reload of the file never undoes a write made while it was reading", async () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            using var s = new SettingsStore(file, NullLogger.Instance);
            // A reload picks up edits other programs made. It reads the file and then replaces the live document with what it read,
            // so a Set that lands in between is undone (the live value goes back to the older one until the next reload): a setting
            // the user just changed, lost. Hammer the reload while writes land and ask after each one what is live.
            using var stop = new CancellationTokenSource();
            var reloads = 0;
            var reloader = Task.Run(() => { while (!stop.IsCancellationRequested) { s.ReloadFromDisk(); Interlocked.Increment(ref reloads); } });
            const int writes = 300;
            string? lost = null;
            for (var i = 0; i < writes && lost is null; i++)
            {
                s.Set("race.value", JsonValue.Create(i));
                var live = s.Get<int>("race.value", -1);
                if (live != i) lost = $"write {i} was undone: the live value is {live}";
            }
            stop.Cancel();
            await reloader;
            Check.True(lost is null, lost ?? "");
            Check.True(Volatile.Read(ref reloads) >= 20, "the reloader really ran alongside the writes (they take turns, so about one reload per write): " + reloads);
            Check.Equal(writes - 1, s.Get<int>("race.value"), "the last write is live");
            Check.Equal(writes - 1, JsonNode.Parse(File.ReadAllText(file))!["race"]!["value"]!.GetValue<int>(), "and on disk");
        });

        r.Add("settings: a failed write changes nothing, and the identical retry writes", () =>
        {
            var dir = T.TempDir("settings");
            var file = Path.Combine(dir, "settings.json");
            using var s = new SettingsStore(file, NullLogger.Instance);
            s.Set("ui.theme", JsonValue.Create("dark"));
            var before = File.ReadAllText(file);
            var changes = 0;
            s.Changed += _ => Interlocked.Increment(ref changes);

            // A read-only destination: the move into place fails, however often it is retried.
            File.SetAttributes(file, FileAttributes.ReadOnly);
            var failed = false;
            try { s.Set("ui.theme", JsonValue.Create("light")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check.True(failed, "the write failed");
            Check.Equal("dark", s.Get<string>("ui.theme"), "a value that is not on disk is not live either");
            Check.Equal(before, File.ReadAllText(file), "the file is untouched");
            Check.Equal(0, Volatile.Read(ref changes), "no change event for a write that did not happen");
            Check.Equal(0, Directory.GetFiles(dir, "*.tmp").Length, "no temp file left behind");

            File.SetAttributes(file, FileAttributes.Normal);
            s.Set("ui.theme", JsonValue.Create("light"));   // the identical retry is a real write, not a no-op
            Check.Equal("light", s.Get<string>("ui.theme"));
            Check.Equal("light", JsonNode.Parse(File.ReadAllText(file))!["ui"]!["theme"]!.GetValue<string>());
            Check.Equal(1, Volatile.Read(ref changes), "one event, for the write that happened");
        });

        r.Add("settings: a broken file at start is kept as-is, and writes are refused until it parses", () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            const string broken = "{ \"providers\": { \"anthropic\": { \"apiKey\": \"sk-1\" } }";
            File.WriteAllText(file, broken);   // the closing brace dropped in a hand edit
            using var s = new SettingsStore(file, NullLogger.Instance);
            Check.True(s.InvalidOnDisk, "the store knows the file does not parse");
            Check.True(s.InvalidOnDiskError is { Length: > 0 }, "and it keeps the parse error");
            Check.Equal("", s.Get<string>("providers.anthropic.apiKey"), "defaults are live");

            Check.Throws<InvalidOperationException>(() => s.Set("a.b", JsonValue.Create(1)), "Set over the broken file is refused");
            Check.Throws<InvalidOperationException>(() => s.Replace(new JsonObject { ["x"] = true }), "Replace is refused too");
            Check.Equal(broken, File.ReadAllText(file), "the broken file survives: it is not replaced by defaults plus one change");

            File.WriteAllText(file, "{ \"providers\": { \"anthropic\": { \"apiKey\": \"sk-1\" } } }");
            s.ReloadFromDisk();
            Check.False(s.InvalidOnDisk, "the fixed file clears the state");
            Check.Equal("sk-1", s.Get<string>("providers.anthropic.apiKey"), "and the user's value is live again");
            s.Set("ui.theme", JsonValue.Create("dark"));
            Check.Equal("sk-1", JsonNode.Parse(File.ReadAllText(file))!["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>());
            Check.Equal("dark", JsonNode.Parse(File.ReadAllText(file))!["ui"]!["theme"]!.GetValue<string>(), "the fix plus the new write, on disk");
        });

        r.Add("settings: an external edit that breaks the file is not overwritten either", () =>
        {
            var file = Path.Combine(T.TempDir("settings"), "settings.json");
            File.WriteAllText(file, "{ \"providers\": { \"anthropic\": { \"apiKey\": \"sk-1\" } } }");
            using var s = new SettingsStore(file, NullLogger.Instance);
            s.Set("ui.theme", JsonValue.Create("dark"));
            Check.Equal("dark", s.Get<string>("ui.theme"));
            Check.False(s.InvalidOnDisk);

            var onDisk = File.ReadAllText(file).TrimEnd();
            File.WriteAllText(file, onDisk[..^1]);   // the hand edit drops the last brace (the file ends in a newline)
            s.ReloadFromDisk();
            Check.True(s.InvalidOnDisk, "the broken reload is noticed");
            Check.Equal("dark", s.Get<string>("ui.theme"), "the last valid document stays live");
            Check.Throws<InvalidOperationException>(() => s.Set("ui.theme", JsonValue.Create("blue")), "a write over the broken file is refused");
            Check.Equal(onDisk[..^1], File.ReadAllText(file), "the file the user was editing survives");

            File.WriteAllText(file, "{ \"providers\": { \"anthropic\": { \"apiKey\": \"sk-1\" } }, \"ui\": { \"theme\": \"light\" } }");
            s.ReloadFromDisk();
            Check.False(s.InvalidOnDisk);
            Check.Equal("light", s.Get<string>("ui.theme"), "the fixed file is live");
            s.Set("ui.theme", JsonValue.Create("blue"));   // writes work again once it parses
            Check.Equal("blue", JsonNode.Parse(File.ReadAllText(file))!["ui"]!["theme"]!.GetValue<string>());
        });
    }
}
