using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// Completion the automatic check is not allowed to judge is recorded on the idea, not only in a background-work line
/// that scrolls away (idea-n2jj97). The bounds themselves stay exactly what they are: a summary is not proof, a
/// truncated patch is not evidence, and a rework of twelve commits is not something one bounded read can judge. What
/// changes is what the reviewer is handed — the bound that was reached, every linked commit with the repository it was
/// made in and the paths read out of its patch, and the idea's own requirements as a checklist that nothing here
/// satisfies. The same document is what <c>ideas.review</c> answers, so a reviewing agent can find the hashes it has
/// to read itself.
/// </summary>
internal sealed class IdeaReview(IPluginContext ctx, IdeasRepository repo)
{
    /// <summary>The section the state lives in. One per idea, and refreshed when the evidence changes.</summary>
    public const string Title = "Needs review";
    /// <summary>How many checklist lines the section carries (it is read in the tab and by an agent).</summary>
    private const int MaxRequirements = 20;
    /// <summary>How many paths one commit's entry names.</summary>
    private const int MaxFiles = 12;

    private readonly IPluginContext _ctx = ctx;
    private readonly IdeasRepository _repo = repo;

    /// <summary>What a linked commit is in the manifest: what it is, where it was made, and what was read from it.</summary>
    public JsonObject Commit(JsonObject entry, IReadOnlyList<string>? files = null, string? note = null)
    {
        var commit = new JsonObject
        {
            ["hash"] = IdeaOps.Str(entry["hash"]) ?? "",
            ["short"] = IdeaOps.Str(entry["short"]),
            ["subject"] = IdeaOps.Str(entry["subject"]),
            ["repo"] = IdeaOps.Str(entry["repo"]),
        };
        if (files is { Count: > 0 })
            commit["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        if (note is { Length: > 0 }) commit["note"] = note;
        return commit;
    }

    /// <summary>
    /// Write the state on the idea, and say so in the log. The evidence was read against one revision and the user may
    /// have edited the idea since, so it is recorded on what the idea is now — the evidence itself does not change —
    /// and left out rather than stamped over their edit if the idea keeps moving under it.
    /// </summary>
    public bool Record(string ideaId, string limit, IReadOnlyList<JsonObject> commits)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = _repo.Find(ideaId);
            if (current is null)
            {
                _ctx.Logger.LogDebug("Ideas: {Idea} needs review ({Limit}), but the idea is gone", ideaId, limit);
                return false;
            }
            var manifest = new JsonObject
            {
                ["limit"] = limit,
                ["at"] = IdeaOps.Now(),
                ["revision"] = current.Revision,
                ["commits"] = new JsonArray(commits.Select(c => (JsonNode)c.DeepClone()).ToArray()),
                ["requirements"] = new JsonArray(Requirements(current.Doc).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            };
            var older = Section(current.Doc);
            if (Same(older?["review"] as JsonObject, manifest)) return true;   // the same state again: no write, no revision

            var section = new JsonObject { ["kind"] = "research", ["title"] = Title, ["content"] = Render(manifest), ["review"] = manifest };
            // Refreshed, never stacked: the older state leaves in the same transaction as the new one.
            var patch = new JsonObject { ["addSections"] = new JsonArray(section) };
            if (IdeaOps.Str(older?["id"]) is { Length: > 0 } old) patch["removeSectionIds"] = new JsonArray(JsonValue.Create(old));
            try
            {
                _repo.Update(ideaId, patch, fromUi: true, expectedRevision: current.Revision);
                _ctx.Logger.LogWarning("Ideas: {Idea} is not judged as finished automatically ({Limit}). The idea says what a reviewer has to read, and its completion stays blocked until they update it.",
                    ideaId, limit);
                return true;
            }
            catch (IdeasConflictException) when (attempt == 0) { }   // edited while this was read: read it again and write once more
        }
        _ctx.Logger.LogWarning("Ideas: {Idea} kept being edited while its completion evidence was read, so its review state is not recorded; the next commit that works on it records it again", ideaId);
        return false;
    }

    /// <summary>The review state an idea carries, as <c>ideas.review</c> answers it (null: it carries none).</summary>
    public static JsonNode? State(JsonObject idea) => Section(idea)?["review"]?.DeepClone();

    /// <summary>The idea's review section, by its title.</summary>
    private static JsonObject? Section(JsonObject idea) =>
        (idea["sections"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => IdeaOps.Str(s["title"]) == Title);

    private static bool Same(JsonObject? older, JsonObject manifest) => older is not null
        && IdeaOps.Str(older["limit"]) == IdeaOps.Str(manifest["limit"])
        && JsonNode.DeepEquals(older["commits"], manifest["commits"])
        && JsonNode.DeepEquals(older["requirements"], manifest["requirements"]);

    /// <summary>
    /// The paths a patch names, out of git's own <c>diff --git a/x b/x</c> lines: what a reviewer reads instead of
    /// the bound the check could not pass.
    /// </summary>
    public static List<string> Paths(string patch)
    {
        const string header = "diff --git a/";
        var files = new List<string>();
        foreach (var line in patch.Split('\n'))
        {
            if (!line.StartsWith(header, StringComparison.Ordinal)) continue;
            var rest = line[header.Length..];
            var other = rest.IndexOf(" b/", StringComparison.Ordinal);
            files.Add(IdeaOps.Clip(other < 0 ? rest : rest[(other + 3)..].Trim(), 120));
            if (files.Count >= MaxFiles) break;
        }
        return files;
    }

    /// <summary>
    /// The idea's own checklist: the list lines of its summary and of its plan, requirements and to-do sections. It is
    /// the idea's text and never the check's conclusion — each line is what a reviewer has to support on its own.
    /// </summary>
    public static List<string> Requirements(JsonObject idea)
    {
        var texts = new List<string>();
        var summary = IdeaOps.Str(idea["summary"]);
        if (summary is { Length: > 0 }) texts.Add(summary);
        foreach (var s in (idea["sections"] as JsonArray ?? []).OfType<JsonObject>())
            if (IdeaOps.Str(s["kind"]) is "plan" or "requirements" or "todo" && IdeaOps.Str(s["content"]) is { Length: > 0 } content)
                texts.Add(content);
        var items = new List<string>();
        foreach (var text in texts)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var item = line.StartsWith("- [ ] ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)
                    ? line[6..]
                    : line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal)
                        ? line[2..]
                        : Numbered(line, out var numbered) ? numbered : null;
                if (item is not { Length: > 0 }) continue;
                item = IdeaOps.Clip(item, 200);
                if (!items.Contains(item, StringComparer.OrdinalIgnoreCase)) items.Add(item);
                if (items.Count >= MaxRequirements) return items;
            }
        }
        // An idea whose plan is one sentence still has something a reviewer has to support.
        if (items.Count == 0 && summary is { Length: > 0 }) items.Add(IdeaOps.Clip(summary.Trim(), 200));
        return items;
    }

    private static bool Numbered(string line, out string item)
    {
        item = "";
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot < 1 || !int.TryParse(line[..dot], out _)) return false;
        item = line[(dot + 2)..];
        return item.Length > 0;
    }

    /// <summary>The section's text: what stopped the check, what there is to read, and how a reviewer finishes it.</summary>
    private static string Render(JsonObject manifest)
    {
        var commits = (manifest["commits"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var sb = new StringBuilder();
        sb.Append("The automatic completion check stopped here: ").Append(IdeaOps.Str(manifest["limit"])).Append(".\n\n")
          .Append("This does not say the idea is unfinished — it says the check cannot read all of the evidence, so it does not judge it. Nothing below is proof of anything.\n\n");
        sb.Append("Linked commits (").Append(commits.Count).Append("):\n");
        foreach (var c in commits)
        {
            sb.Append("- `").Append(IdeaOps.Str(c["short"]) ?? IdeaOps.Clip(IdeaOps.Str(c["hash"]) ?? "?", 7)).Append('`');
            if (IdeaOps.Str(c["subject"]) is { Length: > 0 } subject) sb.Append(' ').Append(subject);
            sb.Append(" — ").Append(IdeaOps.Str(c["repo"]) ?? "an unrecorded repository").Append('\n');
            if (c["files"] is JsonArray files && files.Count > 0)
                sb.Append("  - files: ").Append(string.Join(", ", files.Select(IdeaOps.Str))).Append('\n');
            if (IdeaOps.Str(c["note"]) is { Length: > 0 } note) sb.Append("  - ").Append(note).Append('\n');
        }
        var requirements = (manifest["requirements"] as JsonArray ?? []).Select(IdeaOps.Str).Where(s => s is { Length: > 0 }).ToList();
        sb.Append("\nRequirements (the idea's own; none of them is evidenced by this check):\n");
        if (requirements.Count == 0) sb.Append("_the idea states none itemized: its own text above is what has to be evidenced_\n");
        foreach (var r in requirements) sb.Append("- [ ] ").Append(r).Append('\n');
        sb.Append("\nTo finish this, read the complete evidence yourself — `git show <hash>` in the repository named above, or `files.commits` with `{ cwd, hash }` — and then update the idea explicitly: in the Ideas tab, or with `ideas.verifyUpdate` and that evidence, which applies the update only if an independent verification of it accepts. Until every requirement is supported, the automatic completion stays blocked.");
        return sb.ToString();
    }
}