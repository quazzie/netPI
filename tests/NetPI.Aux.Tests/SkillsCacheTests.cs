using NetPI.Skills;

namespace NetPI.Aux.Tests;

/// <summary>The skill loader's discovery cache: a model call does not walk every skills folder again, and a skill
/// added after the first walk is still found.</summary>
public static class SkillsCacheTests
{
    private static void Skill(string root, string folder, string name)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: Do {name}.\n---\n\nDo the thing.\n");
    }

    public static void Register(TestRunner r)
    {
        r.Add("skills: a repeated discovery reuses the folder walk; a skill added afterwards is still discovered", () =>
        {
            var ctx = new FakePluginContext(T.TempDir("skills-home"), "netpi.skills");
            var user = T.TempDir("skills-user");
            var project = T.TempDir("skills-project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));
            var root = Path.Combine(project, ".netpi", "skills");
            Skill(root, "deploy", "deploy");
            var loader = new SkillLoader(ctx, user);

            Check.Equal("deploy", string.Join(",", loader.Discover(project).Skills.Select(s => s.Name)));
            var walks = loader.Enumerations;
            Check.True(walks > 0, "the first discovery walked the folders");
            loader.Discover(project);
            Check.Equal(walks, loader.Enumerations, "the second discovery, right after, walked nothing");

            // a new skill right under the root moves the folder's last write (a clock a second behind is set by hand,
            // as a later write would be)
            Skill(root, "review", "review");
            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow.AddMinutes(1));
            Check.Equal("deploy,review", string.Join(",", loader.Discover(project).Skills.Select(s => s.Name).Order(StringComparer.Ordinal)),
                "the added skill is discovered: the walk is not stale");
            Check.True(loader.Enumerations > walks, "the changed folder was walked again");
            ctx.Unload();
            return Task.CompletedTask;
        });
    }
}
