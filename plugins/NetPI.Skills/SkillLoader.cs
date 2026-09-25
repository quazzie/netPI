using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Skills;

/// <summary>A skill that applies to a working directory. <see cref="Hash"/> covers what the catalog shows (name, description).</summary>
internal sealed record Skill(
    string Name, string Description, string Path, string Dir, string Scope, bool ModelInvocable, bool Disabled,
    string? License, string? Compatibility, IReadOnlyList<string> AllowedTools, string Hash)
{
    /// <summary>Agents see it in the catalog and may load it.</summary>
    public bool Listed => ModelInvocable && !Disabled;
}

/// <summary>Something wrong with a SKILL.md: <c>warning</c> (loaded anyway) or <c>error</c> (skipped).</summary>
internal sealed record SkillProblem(string Path, string Level, string Message);

internal sealed record SkillSet(IReadOnlyList<Skill> Skills, IReadOnlyList<SkillProblem> Problems)
{
    public Skill? Find(string name) => Skills.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    public IEnumerable<Skill> Listed => Skills.Where(s => s.Listed);
}

/// <summary>
/// Finds skills (directories with a SKILL.md, per the Agent Skills standard) for a working directory, first found wins:
/// the project (<c>.netpi/skills</c> and <c>.agents/skills</c> in the working directory and every parent up to the git root,
/// your home folder excepted), then <c>skills.paths</c>, then global (<c>&lt;home&gt;/skills</c>, <c>~/.agents/skills</c>).
/// With <c>skills.claudeCode</c> also <c>.claude/skills</c> at both levels. Parsed files are cached by path, time and size.
/// </summary>
internal sealed partial class SkillLoader(IPluginContext ctx, string? userHome = null)
{
    public const int MaxDepth = 5;
    public const int MaxDirs = 2000;
    public const int MaxBytes = 256 * 1024;

    private sealed record Parsed(DateTime MtimeUtc, long Length, string? Name, string? Description, bool ModelInvocable,
        string? License, string? Compatibility, IReadOnlyList<string> AllowedTools, List<(string Level, string Message)> Problems);

    private readonly ConcurrentDictionary<string, Parsed> _cache = new(PathComparer);

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private string UserHome => userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NameRx();

    private List<string> Setting(string key)
    {
        try
        {
            return ctx.Settings.GetNode(key) switch
            {
                JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                    .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
                JsonValue v when v.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one) => [one.Trim()],
                _ => [],
            };
        }
        catch { return []; }
    }

    /// <summary>The folders searched, in precedence order, with their scope (project | extra | global).</summary>
    public List<(string Dir, string Scope)> Roots(string cwd)
    {
        var claude = ctx.Settings.Get("skills.claudeCode", false);
        string[] names = claude ? [".netpi/skills", ".agents/skills", ".claude/skills"] : [".netpi/skills", ".agents/skills"];
        var roots = new List<(string, string)>();
        var seen = new HashSet<string>(PathComparer);
        void Add(string dir, string scope)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(dir);
                if (seen.Add(full)) roots.Add((full, scope));
            }
            catch { /* not a path */ }
        }

        var home = UserHome;
        try
        {
            for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
            {
                // your home folder's .agents/skills is the global one
                if (!PathComparer.Equals(dir.FullName.TrimEnd('\\', '/'), home.TrimEnd('\\', '/')))
                    foreach (var n in names) Add(System.IO.Path.Combine(dir.FullName, n), "project");
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")) || File.Exists(System.IO.Path.Combine(dir.FullName, ".git"))) break;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Skill folders above {Cwd} not searched", cwd);
        }

        foreach (var p in Setting("skills.paths"))
        {
            var path = p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal) ? System.IO.Path.Combine(home, p[2..])
                : p == "~" ? home
                : System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(cwd, p);
            Add(path, "extra");
        }

        Add(System.IO.Path.Combine(ctx.Paths.Home, "skills"), "global");
        Add(System.IO.Path.Combine(home, ".agents", "skills"), "global");
        if (claude) Add(System.IO.Path.Combine(home, ".claude", "skills"), "global");
        return roots;
    }

    public SkillSet Discover(string cwd)
    {
        var disabled = new HashSet<string>(Setting("skills.disabled"), StringComparer.OrdinalIgnoreCase);
        var skills = new List<Skill>();
        var problems = new List<SkillProblem>();
        var byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, scope) in Roots(cwd))
        {
            foreach (var file in SkillFiles(root))
            {
                var p = Parse(file);
                if (p is null) continue;
                foreach (var (level, message) in p.Problems) problems.Add(new SkillProblem(file, level, message));
                if (p.Name is null || p.Description is null) continue;
                if (byName.TryGetValue(p.Name, out var first))
                {
                    problems.Add(new SkillProblem(file, "warning", $"Another skill named \"{p.Name}\" comes first ({first.Path}): this one is not used."));
                    continue;
                }
                var dir = System.IO.Path.GetDirectoryName(file)!;
                var skill = new Skill(p.Name, p.Description, file, dir, scope, p.ModelInvocable, disabled.Contains(p.Name),
                    p.License, p.Compatibility, p.AllowedTools, Hash(p.Name + "\n" + p.Description));
                byName[p.Name] = skill;
                skills.Add(skill);
            }
        }
        return new SkillSet(skills, problems);
    }

    /// <summary>The SKILL.md files under a skills folder (or the folder itself when it is one skill), sorted, bounded.</summary>
    private static IEnumerable<string> SkillFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var own = System.IO.Path.Combine(root, "SKILL.md");
        if (File.Exists(own)) { yield return own; yield break; }
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxDirs)
        {
            var (dir, depth) = queue.Dequeue();
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; }
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                if (++visited > MaxDirs) yield break;
                var name = System.IO.Path.GetFileName(child);
                if (name.StartsWith('.') || name == "node_modules") continue;
                var file = System.IO.Path.Combine(child, "SKILL.md");
                if (File.Exists(file)) yield return file; // a skill: its own folders are its resources
                else if (depth + 1 < MaxDepth) queue.Enqueue((child, depth + 1));
            }
        }
    }

    private Parsed? Parse(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            if (_cache.TryGetValue(file, out var hit) && hit.MtimeUtc == fi.LastWriteTimeUtc && hit.Length == fi.Length) return hit;
            var parsed = ParseText(ReadText(file), System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)!), fi);
            _cache[file] = parsed;
            foreach (var (level, message) in parsed.Problems)
                ctx.Logger.Log(level == "error" ? LogLevel.Warning : LogLevel.Information, "Skill {Path}: {Message}", file, message);
            return parsed;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Cannot read {Path}", file);
            return null;
        }
    }

    /// <summary>Lenient, as the standard's client guide advises: problems are warnings unless the skill can't be listed.</summary>
    private static Parsed ParseText(string text, string folder, FileInfo fi)
    {
        var problems = new List<(string, string)>();
        var fm = Frontmatter.Parse(text);
        if (fm is null)
        {
            problems.Add(("error", "No YAML frontmatter (a --- block with name and description) at the top: skipped."));
            return new Parsed(fi.LastWriteTimeUtc, fi.Length, null, null, true, null, null, [], problems);
        }
        var f = fm.Fields;
        var name = f.Str("name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = folder;
            problems.Add(("warning", $"No name: the folder name \"{folder}\" is used."));
        }
        else
        {
            if (name.Length > 64) problems.Add(("warning", $"The name is {name.Length} characters (at most 64)."));
            if (!NameRx().IsMatch(name)) problems.Add(("warning", $"The name \"{name}\" should be lowercase letters, digits and single hyphens."));
            if (!string.Equals(name, folder, StringComparison.Ordinal)) problems.Add(("warning", $"The name \"{name}\" differs from its folder \"{folder}\"."));
        }
        var description = f.Str("description")?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            problems.Add(("error", "No description: skipped (agents choose a skill by its description)."));
            description = null;
        }
        else if (description.Length > 1024) problems.Add(("warning", $"The description is {description.Length} characters (at most 1024)."));
        var compatibility = f.Str("compatibility")?.Trim();
        if (compatibility is { Length: > 500 }) problems.Add(("warning", $"compatibility is {compatibility.Length} characters (at most 500)."));
        return new Parsed(fi.LastWriteTimeUtc, fi.Length, name, description, !f.Flag("disable-model-invocation"),
            f.Str("license")?.Trim(), compatibility, f.Words("allowed-tools"), problems);
    }

    internal static string ReadText(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = (int)Math.Min(fs.Length, MaxBytes);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = fs.Read(buf, read, len - read);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    internal static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
}
