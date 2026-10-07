using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// A regression the post-merge review reproduced, as it stands against the storage that replaced the file: two
/// instances of the plugin must not lose each other's update. It is written here rather than carried over because the
/// storage it was written against (a JSON file, a watcher and a parse cache) is gone; what it asserts has not changed.
/// The review's other regression (a rejected patch must not be committed by a later successful write) is
/// "review: a rejected patch leaves no trace, and no later write commits it" in <see cref="IdeasStorageTests"/>.
/// </summary>
public static class ReviewStorageTests
{
    public static void Register(TestRunner r)
    {
        r.Add("review: two instances must not write a stale snapshot over each other", async () =>
        {
            var home = T.TempDir("review-two-instances");
            var env = new FakePluginContext(home);
            var first = IdeasRepository.Open(env.Data, env.Access, env.Log, env.Paths.Home);
            // A second store over the same home folder, as the new instance of a hot-reload swap has. There is no file
            // lock to take and no watcher to miss: a stale tree is impossible, because nothing is cached.
            var swapped = new FakePluginContext(home);
            var second = IdeasRepository.Open(swapped.Data, swapped.Access, swapped.Log, swapped.Paths.Home);

            first.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "First" }, first.TakenIds(), "user", null));
            second.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Second" }, second.TakenIds(), "user", null));
            first.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Third" }, first.TakenIds(), "user", null));

            var titles = first.All().Select(i => IdeaOps.Str(i["title"])).ToList();
            Check.Equal(3, titles.Count, "every independently committed update must survive: " + string.Join(", ", titles));
            Check.Equal(3, second.All().Count, "and the other instance sees all of them");
            Check.Equal(3, second.TakenIds().Distinct().Count(), "with distinct ids");
            swapped.Unload();
            env.Unload();
        });
    }
}
