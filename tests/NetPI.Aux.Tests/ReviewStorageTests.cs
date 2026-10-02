using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The two regressions the post-merge review reproduced, as they stand against the storage that replaced the file:
/// a rejected patch must not be committed by a later successful write, and two instances of the plugin must not lose
/// each other's update. They are written here rather than carried over because the storage they were written against
/// (a JSON file, a watcher and a parse cache) is gone; what they assert has not changed.
/// </summary>
public static class ReviewStorageTests
{
    public static void Register(TestRunner r)
    {
        r.Add("review: rejected patch must not leak into a later successful save", async () =>
        {
            var env = new FakePluginContext(T.TempDir("review-patch"));
            var repo = IdeasRepository.Open(env.Data, env.Access, env.Log, env.Paths.Home);
            var idea = IdeaOps.CreateIdea(new JsonObject { ["title"] = "Original" }, repo.TakenIds(), "user", null);
            repo.Add(idea);
            var id = idea["id"]!.Str()!;

            // The patch changes the title and is then refused on the status: half of it must not survive.
            Check.Throws<IdeaInputException>(() =>
                repo.Update(id, new JsonObject { ["title"] = "Rejected title", ["status"] = "INVALID" }, fromUi: true));
            Check.Equal("Original", repo.Idea(id)!["title"]!.Str());

            repo.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Other" }, repo.TakenIds(), "user", null));
            Check.Equal("Original", repo.Idea(id)!["title"]!.Str(), "a rejected patch must not be committed by an unrelated update");
            Check.Equal(1, (repo.Find(id)?.Revision ?? 0), "nor by burning a revision on the way");
            env.Unload();
        });

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
