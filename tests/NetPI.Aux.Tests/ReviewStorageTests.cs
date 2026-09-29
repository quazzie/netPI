using System.Reflection;
using System.Text.Json.Nodes;
using NetPI.Ideas;
namespace NetPI.Aux.Tests;
public static class ReviewStorageTests
{
    public static void Register(TestRunner r)
    {
        r.Add("review: rejected patch must not leak into a later successful save", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("review-cache"));
            var path = Path.Combine(ctx.Paths.Home, "ideas.json");
            File.WriteAllText(path, "{\"ideas\":[{\"id\":\"idea-review\",\"title\":\"Original\",\"status\":\"open\"}]}");
            using var store = new IdeasStore(ctx.Events);
            await store.ReadAsync(path, f => f.Ideas.Count);
            try
            {
                await store.UpdateAsync<object?>(path, f =>
                {
                    IdeaOps.ApplyPatch((JsonObject)f.Ideas[0]!, new JsonObject { ["title"] = "Rejected title", ["status"] = "INVALID" }, true);
                    return null;
                });
            }
            catch (IdeaInputException) { }
            Check.Equal("Original", JsonNode.Parse(File.ReadAllText(path))!["ideas"]![0]!["title"]!.GetValue<string>());
            await store.UpdateAsync<object?>(path, f => { f.Ideas.Add(new JsonObject { ["id"] = "idea-other", ["title"] = "Other" }); return null; });
            Check.Equal("Original", JsonNode.Parse(File.ReadAllText(path))!["ideas"]![0]!["title"]!.GetValue<string>(), "a rejected patch must not be committed by an unrelated update");
        });
        r.Add("review: shared lock must not write a stale cached snapshot after a missed watcher event", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("review-two-stores"));
            var path = Path.Combine(ctx.Paths.Home, "ideas.json");
            File.WriteAllText(path, "{\"ideas\":[]}");
            using var first = new IdeasStore(ctx.Events);
            using var second = new IdeasStore(ctx.Events);
            await second.ReadAsync(path, f => f.Ideas.Count);
            // Fault injection: an OS watcher can delay/drop notifications. The lock must still protect updates.
            var watchers = (Dictionary<string, FileSystemWatcher>)typeof(IdeasStore).GetField("_watchers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(second)!;
            foreach (var watcher in watchers.Values) watcher.EnableRaisingEvents = false;
            await first.UpdateAsync<object?>(path, f => { f.Ideas.Add(new JsonObject { ["id"] = "idea-first", ["title"] = "First" }); return null; });
            await second.UpdateAsync<object?>(path, f => { f.Ideas.Add(new JsonObject { ["id"] = "idea-second", ["title"] = "Second" }); return null; });
            Check.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["ideas"]!.AsArray().Count, "both independently committed updates must survive");
        });
    }
}
