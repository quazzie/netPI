using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>Invalid input for an idea operation (reported to the model/UI as an error message).</summary>
public sealed class IdeaInputException(string message) : Exception(message);

/// <summary>
/// Operations on ideas stored as <see cref="JsonObject"/>s, so unknown fields written by other tools or by hand survive.
/// </summary>
public static class IdeaOps
{
    public static readonly string[] Statuses = ["open", "parked", "planned", "in-progress", "done", "rejected"];
    public static readonly string[] Priorities = ["low", "medium", "high"];
    public static readonly string[] Kinds = ["note", "research", "plan", "requirements", "design", "decision", "blocker", "links", "todo"];

    /// <summary>Fields that patches never touch directly.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "createdAt", "createdBy", "updatedAt", "sessionIds", "sections", "addSections", "updateSections", "removeSectionIds",
        "title", "summary", "status", "priority", "tags",
    };

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ lookup helpers

    /// <summary>Property lookup ignoring case, '_' and '-' (add_sections = addSections).</summary>
    public static JsonNode? Pick(JsonObject o, params string[] names)
    {
        foreach (var name in names)
            if (o.TryGetPropertyValue(name, out var exact)) return exact;
        foreach (var (k, v) in o)
        {
            var nk = Norm(k);
            foreach (var name in names) if (nk == Norm(name)) return v;
        }
        return null;
    }

    public static bool Has(JsonObject o, params string[] names)
    {
        foreach (var name in names) if (o.ContainsKey(name)) return true;
        foreach (var (k, _) in o) foreach (var name in names) if (Norm(k) == Norm(name)) return true;
        return false;
    }

    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static string? Str(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    public static string? Str(JsonObject o, params string[] names) => Str(Pick(o, names));

    /// <summary>Arrays sometimes arrive as JSON strings from models: unwrap them.</summary>
    public static JsonArray? Array(JsonNode? n)
    {
        switch (n)
        {
            case JsonArray a: return a;
            case JsonObject o: return [o.DeepClone()];
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Trim();
                if (s.StartsWith('[') || s.StartsWith('{'))
                {
                    try
                    {
                        var parsed = JsonNode.Parse(s);
                        return parsed is JsonArray pa ? pa : parsed is JsonObject po ? [po] : null;
                    }
                    catch (JsonException) { }
                }
                return null;
            default: return null;
        }
    }

    public static JsonObject? Find(JsonArray ideas, string id)
    {
        id = id.Trim();
        JsonObject? ci = null;
        foreach (var n in ideas)
        {
            if (n is not JsonObject o) continue;
            var oid = Str(o["id"]);
            if (oid == id) return o;
            if (oid is not null && (string.Equals(oid, id, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(oid, "idea-" + id, StringComparison.OrdinalIgnoreCase))) ci ??= o;
        }
        return ci;
    }

    public static IEnumerable<JsonObject> All(JsonArray ideas) => ideas.OfType<JsonObject>();

    // ------------------------------------------------------------------ normalization

    public static string NormalizeStatus(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return v switch
        {
            "open" or "new" or "todo" or "idea" or "proposed" => "open",
            "parked" or "backlog" or "later" or "deferred" or "on-hold" or "someday" => "parked",
            "planned" or "scheduled" or "ready" => "planned",
            "in-progress" or "inprogress" or "wip" or "doing" or "started" or "active" or "in-work" => "in-progress",
            "done" or "completed" or "complete" or "implemented" or "closed" or "finished" => "done",
            "rejected" or "declined" or "wontfix" or "won't-do" or "wont-do" or "dropped" or "cancelled" or "canceled" => "rejected",
            _ => throw new IdeaInputException($"Invalid status '{s}'. Use one of: {string.Join(", ", Statuses)}."),
        };
    }

    public static string NormalizePriority(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "low" or "lo" or "minor" or "p3" => "low",
            "medium" or "med" or "normal" or "mid" or "p2" => "medium",
            "high" or "hi" or "urgent" or "critical" or "important" or "p1" or "p0" => "high",
            _ => throw new IdeaInputException($"Invalid priority '{s}'. Use one of: {string.Join(", ", Priorities)}."),
        };
    }

    public static string NormalizeKind(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "" or "note" or "notes" or "comment" => "note",
            "research" or "findings" or "investigation" or "analysis" => "research",
            "plan" or "plans" or "implementation" or "steps" or "approach" => "plan",
            "requirements" or "requirement" or "reqs" or "spec" or "specification" or "acceptance" => "requirements",
            "design" or "architecture" => "design",
            "decision" or "decisions" or "adr" => "decision",
            "blocker" or "blockers" or "risk" or "risks" or "issue" or "issues" => "blocker",
            "links" or "link" or "references" or "refs" or "sources" => "links",
            "todo" or "todos" or "tasks" or "checklist" => "todo",
            _ => "note",
        };
    }

    public static List<string> ParseTags(JsonNode? n)
    {
        var raw = new List<string>();
        switch (n)
        {
            case JsonArray a:
                foreach (var x in a) if (Str(x) is { } s) raw.Add(s);
                break;
            case JsonValue v when Str(v) is { } s:
                if (Array(v) is { } parsed) return ParseTags(parsed);
                raw.AddRange(s.Split([',', ';'], StringSplitOptions.None));
                break;
        }
        var result = new List<string>();
        foreach (var t in raw)
        {
            var tag = t.Trim().TrimStart('#').Trim();
            if (tag.Length > 0 && !result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
        }
        return result;
    }

    // ------------------------------------------------------------------ creation

    public static string NewId(string prefix, IEnumerable<string?> existing, int length)
    {
        var set = new HashSet<string?>(existing, StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var id = prefix + Ids.Short(length);
            if (!set.Contains(id)) return id;
        }
    }

    /// <summary>
    /// Create an idea from an input object ({ title, summary?, status?, priority?, tags?, sections? }).
    /// With <paramref name="keepExtraFields"/> (UI) other input fields are stored as well.
    /// </summary>
    public static JsonObject CreateIdea(JsonObject input, JsonArray existing, string createdBy, string? sessionId, bool keepExtraFields = false)
    {
        var title = Str(input, "title")?.Trim();
        if (string.IsNullOrEmpty(title)) throw new IdeaInputException("An idea needs a title.");
        var now = Now();
        var idea = new JsonObject
        {
            ["id"] = NewId("idea-", All(existing).Select(i => Str(i["id"])), 6),
            ["title"] = title,
            ["summary"] = Str(input, "summary", "description")?.Trim() ?? "",
            ["status"] = Str(input, "status") is { Length: > 0 } st ? NormalizeStatus(st) : "open",
            ["priority"] = Str(input, "priority") is { Length: > 0 } pr ? NormalizePriority(pr) : "medium",
            ["tags"] = ToArray(ParseTags(Pick(input, "tags"))),
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["createdBy"] = createdBy,
            ["sections"] = new JsonArray(),
            ["sessionIds"] = sessionId is null ? new JsonArray() : new JsonArray(sessionId),
        };
        var sections = (JsonArray)idea["sections"]!;
        if (Array(Pick(input, "sections")) is { } given)
            foreach (var s in given) sections.Add(CreateSection(s, sections));
        if (keepExtraFields)
            foreach (var (k, v) in input)
                if (!Protected.Contains(k) && !idea.ContainsKey(k) && Norm(k) != "description") idea[k] = v?.DeepClone();
        return idea;
    }

    public static JsonObject CreateSection(JsonNode? input, JsonArray existingSections)
    {
        JsonObject src = input switch
        {
            JsonObject o => o,
            JsonValue v when Str(v) is { } text => new JsonObject { ["content"] = text },
            _ => throw new IdeaInputException("A section must be an object { kind, title?, content }."),
        };
        var content = Str(src, "content", "text", "body", "markdown") ?? "";
        var title = Str(src, "title", "name")?.Trim();
        if (content.Trim().Length == 0 && string.IsNullOrEmpty(title))
            throw new IdeaInputException("A section needs content (or at least a title).");
        var section = new JsonObject
        {
            ["id"] = NewId("sec-", existingSections.OfType<JsonObject>().Select(s => Str(s["id"])), 4),
            ["kind"] = NormalizeKind(Str(src, "kind", "type")),
        };
        if (!string.IsNullOrEmpty(title)) section["title"] = title;
        section["content"] = content;
        section["updatedAt"] = Now();
        foreach (var (k, v) in src)
            if (!section.ContainsKey(k) && Norm(k) is not ("content" or "text" or "body" or "markdown" or "title" or "name" or "kind" or "type" or "id"))
                section[k] = v?.DeepClone();
        return section;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    // ------------------------------------------------------------------ patching

    /// <summary>
    /// Apply a patch: title, summary, status, priority, tags, sections, addSections, updateSections [{id, title?, content?, kind?}],
    /// removeSectionIds. From the UI (<paramref name="fromUi"/>) <c>sections</c> replaces all sections and other top-level
    /// fields are set (null removes them); from a tool <c>sections</c> is treated like addSections and extra fields are ignored.
    /// Returns human-readable change descriptions.
    /// </summary>
    public static List<string> ApplyPatch(JsonObject idea, JsonObject patch, bool fromUi, string? sessionId = null)
    {
        var changes = new List<string>();
        if (Has(patch, "title"))
        {
            var t = Str(patch, "title")?.Trim();
            if (string.IsNullOrEmpty(t)) throw new IdeaInputException("title cannot be empty.");
            if (Str(idea["title"]) != t) { idea["title"] = t; changes.Add("title"); }
        }
        if (Has(patch, "summary"))
        {
            var s = Str(patch, "summary")?.Trim() ?? "";
            if (Str(idea["summary"]) != s) { idea["summary"] = s; changes.Add("summary"); }
        }
        if (Has(patch, "status"))
        {
            var s = NormalizeStatus(Str(patch, "status"));
            var old = Str(idea["status"]);
            if (old != s) { idea["status"] = s; changes.Add($"status ({old ?? "?"} → {s})"); }
        }
        if (Has(patch, "priority"))
        {
            var p = NormalizePriority(Str(patch, "priority"));
            var old = Str(idea["priority"]);
            if (old != p) { idea["priority"] = p; changes.Add($"priority ({old ?? "?"} → {p})"); }
        }
        if (Has(patch, "tags"))
        {
            var tags = ParseTags(Pick(patch, "tags"));
            var old = ParseTags(idea["tags"]);
            if (!old.SequenceEqual(tags)) { idea["tags"] = ToArray(tags); changes.Add("tags"); }
        }

        if (idea["sections"] is not JsonArray sections) { sections = []; idea["sections"] = sections; }

        if (fromUi && Has(patch, "sections") && Array(Pick(patch, "sections")) is { } replacement)
        {
            var old = sections.OfType<JsonObject>().ToDictionary(s => Str(s["id"]) ?? "", StringComparer.Ordinal);
            var next = new JsonArray();
            foreach (var n in replacement)
            {
                if (n is JsonObject so && Str(so, "id") is { } sid && old.TryGetValue(sid, out var existing))
                {
                    var copy = (JsonObject)existing.DeepClone();
                    UpdateSection(copy, so);
                    // Extra UI fields (e.g. "collapsed") are stored too; null removes them.
                    foreach (var (k, v) in so)
                    {
                        if (Norm(k) is "id" or "title" or "name" or "content" or "text" or "body" or "markdown" or "kind" or "type" or "updatedat") continue;
                        if (v is null) copy.Remove(k);
                        else copy[k] = v.DeepClone();
                    }
                    next.Add(copy);
                }
                else next.Add(CreateSection(n, next));
            }
            if (!JsonNode.DeepEquals(sections, next)) changes.Add("sections");
            idea["sections"] = sections = next;
        }
        if ((Array(Pick(patch, "addSections")) ?? (fromUi ? null : Array(Pick(patch, "sections")))) is { Count: > 0 } add)
        {
            foreach (var n in add) sections.Add(CreateSection(n, sections));
            changes.Add(add.Count == 1 ? "added 1 section" : $"added {add.Count} sections");
        }
        if (Array(Pick(patch, "updateSections")) is { Count: > 0 } upd)
        {
            foreach (var n in upd)
            {
                if (n is not JsonObject u || Str(u, "id") is not { } sid)
                    throw new IdeaInputException("updateSections entries need an id (see idea_get).");
                var target = FindSection(sections, sid) ?? throw new IdeaInputException($"Section '{sid}' not found in this idea.");
                if (UpdateSection(target, u)) changes.Add($"updated section {Str(target["id"])}");
            }
        }
        if (Pick(patch, "removeSectionIds") is { } rm)
        {
            var ids = rm is JsonArray ra ? ra.Select(Str).ToList() : [Str(rm)];
            foreach (var sid in ids)
            {
                if (string.IsNullOrWhiteSpace(sid)) continue;
                var target = FindSection(sections, sid) ?? throw new IdeaInputException($"Section '{sid}' not found in this idea.");
                sections.Remove(target);
                changes.Add($"removed section {sid}");
            }
        }

        if (fromUi)
        {
            foreach (var (k, v) in patch.ToList())
            {
                if (Protected.Contains(k) || Protected.Any(p => Norm(p) == Norm(k))) continue;
                if (v is null) { if (idea.Remove(k)) changes.Add(k); }
                else if (!JsonNode.DeepEquals(idea[k], v)) { idea[k] = v.DeepClone(); changes.Add(k); }
            }
        }

        if (changes.Count > 0)
        {
            idea["updatedAt"] = Now();
            if (sessionId is not null) AddSession(idea, sessionId);
        }
        return changes;
    }

    private static JsonObject? FindSection(JsonArray sections, string id) =>
        sections.OfType<JsonObject>().FirstOrDefault(s => Str(s["id"]) == id)
        ?? sections.OfType<JsonObject>().FirstOrDefault(s => string.Equals(Str(s["id"]), id, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(Str(s["id"]), "sec-" + id, StringComparison.OrdinalIgnoreCase));

    private static bool UpdateSection(JsonObject section, JsonObject u)
    {
        var changed = false;
        if (Has(u, "title", "name"))
        {
            var t = Str(u, "title", "name")?.Trim();
            if (string.IsNullOrEmpty(t)) { changed |= section.Remove("title"); }
            else if (Str(section["title"]) != t) { section["title"] = t; changed = true; }
        }
        if (Has(u, "content", "text", "body", "markdown"))
        {
            var c = Str(u, "content", "text", "body", "markdown") ?? "";
            if (Str(section["content"]) != c) { section["content"] = c; changed = true; }
        }
        if (Has(u, "kind", "type"))
        {
            var k = NormalizeKind(Str(u, "kind", "type"));
            if (Str(section["kind"]) != k) { section["kind"] = k; changed = true; }
        }
        if (changed) section["updatedAt"] = Now();
        return changed;
    }

    public static void AddSession(JsonObject idea, string sessionId)
    {
        if (idea["sessionIds"] is not JsonArray a) { a = []; idea["sessionIds"] = a; }
        if (!a.Any(x => Str(x) == sessionId)) a.Add(sessionId);
    }

    // ------------------------------------------------------------------ filtering & rendering

    public static bool Matches(JsonObject idea, string? status, string? tag, string? query)
    {
        var st = Str(idea["status"]) ?? "open";
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            if (s == "active") { if (st is "done" or "rejected") return false; }
            else if (s != "all")
            {
                var wanted = s.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries).Select(NormalizeStatus).ToHashSet();
                if (!wanted.Contains(st)) return false;
            }
        }
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var t = tag.Trim().TrimStart('#');
            if (!ParseTags(idea["tags"]).Contains(t, StringComparer.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var hay = new StringBuilder();
            hay.Append(Str(idea["id"])).Append(' ').Append(Str(idea["title"])).Append(' ').Append(Str(idea["summary"])).Append(' ')
               .Append(string.Join(' ', ParseTags(idea["tags"])));
            if (idea["sections"] is JsonArray secs)
                foreach (var s in secs.OfType<JsonObject>()) hay.Append(' ').Append(Str(s["title"])).Append(' ').Append(Str(s["content"]));
            var h = hay.ToString();
            foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!h.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static string ListLine(JsonObject idea)
    {
        var sb = new StringBuilder("- ");
        sb.Append(Str(idea["id"])).Append(" [").Append(Str(idea["status"]) ?? "open").Append(" · ").Append(Str(idea["priority"]) ?? "medium")
          .Append("] ").Append(Str(idea["title"]));
        var summary = OneLine(Str(idea["summary"]));
        if (summary.Length > 0) sb.Append(" — ").Append(summary.Length > 140 ? summary[..137] + "..." : summary);
        var tags = ParseTags(idea["tags"]);
        if (tags.Count > 0) sb.Append("  ").Append(string.Join(' ', tags.Select(t => "#" + t)));
        var n = (idea["sections"] as JsonArray)?.Count ?? 0;
        if (n > 0) sb.Append($" ({n} section{(n == 1 ? "" : "s")})");
        return sb.ToString();
    }

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static string KindLabel(string? kind) => NormalizeKind(kind) switch
    {
        "research" => "Research", "plan" => "Plan", "requirements" => "Requirements", "design" => "Design",
        "decision" => "Decision", "blocker" => "Blocker", "links" => "Links", "todo" => "To do", _ => "Note",
    };

    /// <summary>Full markdown rendering (idea_get).</summary>
    public static string RenderMarkdown(JsonObject idea)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Str(idea["title"])).Append('\n');
        sb.Append('`').Append(Str(idea["id"])).Append("` · status: ").Append(Str(idea["status"]) ?? "open")
          .Append(" · priority: ").Append(Str(idea["priority"]) ?? "medium");
        var tags = ParseTags(idea["tags"]);
        if (tags.Count > 0) sb.Append(" · tags: ").Append(string.Join(", ", tags));
        sb.Append('\n');
        sb.Append("Created ").Append(Str(idea["createdAt"]) ?? "?");
        if (Str(idea["createdBy"]) is { } by) sb.Append(" by ").Append(by);
        if (Str(idea["updatedAt"]) is { } up) sb.Append(" · updated ").Append(up);
        sb.Append('\n');
        if (Str(idea["summary"]) is { Length: > 0 } summary) sb.Append('\n').Append(summary.Trim()).Append('\n');
        AppendSections(sb, idea, withIds: true);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendSections(StringBuilder sb, JsonObject idea, bool withIds)
    {
        if (idea["sections"] is not JsonArray secs) return;
        foreach (var s in secs.OfType<JsonObject>())
        {
            sb.Append("\n## ").Append(KindLabel(Str(s["kind"])));
            if (Str(s["title"]) is { Length: > 0 } t) sb.Append(": ").Append(t);
            if (withIds) sb.Append(" [").Append(Str(s["id"])).Append(']');
            sb.Append('\n');
            if (Str(s["content"]) is { Length: > 0 } c) sb.Append(c.Trim()).Append('\n');
        }
    }

    /// <summary>Composer text that asks the agent to implement an idea.</summary>
    public static string ToPrompt(JsonObject idea, string fileName)
    {
        var sb = new StringBuilder();
        sb.Append("Implement the following idea from the ideas backlog (`").Append(Str(idea["id"])).Append("` in ").Append(fileName).Append(").\n");
        sb.Append("Its sections contain earlier research, plans and decisions — use them. Keep the idea up to date with idea_update: ")
          .Append("set the status to \"in-progress\" when you start and \"done\" when finished, and add a note section for anything important you learn.\n\n");
        sb.Append("# ").Append(Str(idea["title"])).Append('\n');
        var meta = new List<string> { "Priority: " + (Str(idea["priority"]) ?? "medium") };
        var tags = ParseTags(idea["tags"]);
        if (tags.Count > 0) meta.Add("Tags: " + string.Join(", ", tags));
        sb.Append(string.Join(" · ", meta)).Append('\n');
        if (Str(idea["summary"]) is { Length: > 0 } summary) sb.Append('\n').Append(summary.Trim()).Append('\n');
        AppendSections(sb, idea, withIds: false);
        return sb.ToString().TrimEnd();
    }
}
