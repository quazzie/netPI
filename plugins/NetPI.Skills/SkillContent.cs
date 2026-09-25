using System.Security;
using System.Text;

namespace NetPI.Skills;

/// <summary>
/// A skill's instructions as the model gets them (from the skill tool or /skill:name), wrapped so they can be told apart
/// from the conversation: the SKILL.md body without its frontmatter, the skill's folder (relative paths resolve against
/// it) and the files it bundles, listed but not read.
/// </summary>
internal static class SkillContent
{
    /// <summary>A longer body is cut at a line, with where to read on; it keeps the result under the runtime's 20000 characters.</summary>
    public const int MaxBodyChars = 16_000;
    public const int MaxResources = 50;

    public sealed record Loaded(string Text, string Hash);

    public static Loaded? Load(Skill skill)
    {
        string raw;
        try { raw = SkillLoader.ReadText(skill.Path); }
        catch { return null; }
        var fm = Frontmatter.Parse(raw);
        var body = fm?.Body ?? raw.Trim();
        var firstLine = fm?.BodyLine ?? 1;

        var sb = new StringBuilder();
        sb.Append("<skill_content name=\"").Append(SecurityElement.Escape(skill.Name)).Append("\">\n");
        if (body.Length > MaxBodyChars)
        {
            var cut = body.LastIndexOf('\n', MaxBodyChars);
            if (cut < MaxBodyChars / 2) cut = MaxBodyChars;
            var line = firstLine + body[..cut].Count(c => c == '\n') + 1;
            sb.Append(body[..cut].TrimEnd()).Append("\n\n[… SKILL.md continues: read ").Append(skill.Path)
              .Append(" from line ").Append(line).Append(" for the rest.]");
        }
        else sb.Append(body);
        sb.Append("\n\nSkill directory: ").Append(skill.Dir).Append("\nRelative paths in this skill resolve against it.");
        var files = Resources(skill.Dir, out var more);
        if (files.Count > 0)
        {
            sb.Append("\n<skill_resources>\n");
            foreach (var f in files) sb.Append("  <file>").Append(SecurityElement.Escape(f)).Append("</file>\n");
            if (more) sb.Append("  (more files: list the directory)\n");
            sb.Append("</skill_resources>");
        }
        sb.Append("\n</skill_content>");
        return new Loaded(sb.ToString(), SkillLoader.Hash(raw));
    }

    /// <summary>The skill's other files, relative with forward slashes (no .git or node_modules), sorted, capped.</summary>
    internal static List<string> Resources(string dir, out bool more)
    {
        var list = new List<string>();
        more = false;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dir, 0));
        while (stack.Count > 0)
        {
            var (d, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.GetFiles(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    if (rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Count >= MaxResources) { more = true; return Sorted(list); }
                    list.Add(rel);
                }
                if (depth >= 3) continue;
                foreach (var sub in Directory.GetDirectories(d).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(sub);
                    if (name is ".git" or "node_modules" or "__pycache__") continue;
                    stack.Push((sub, depth + 1));
                }
            }
            catch { /* unreadable folder */ }
        }
        return Sorted(list);
    }

    private static List<string> Sorted(List<string> list)
    {
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
