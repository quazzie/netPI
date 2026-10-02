using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Plan;

internal sealed record PlanStep(string Text, string? Detail);

internal sealed record PlanFile(string Path, string? Note);

/// <summary>
/// What <c>plan_submit</c> carries: a title, a summary, the steps, and what the user needs to judge it (the files that
/// change, the risks, how it is tested, what is still open). Kept as JSON in the plan's document, rendered as markdown
/// for the idea and the file, and shown as a card in the chat.
/// </summary>
internal sealed record PlanBody(
    string Title, string Summary, IReadOnlyList<PlanStep> Steps, IReadOnlyList<PlanFile> Files,
    IReadOnlyList<string> Risks, IReadOnlyList<string> Tests, IReadOnlyList<string> OpenQuestions)
{
    public const int MaxSteps = 40;
    public const int MaxFiles = 60;
    public const int MaxList = 20;

    public JsonObject ToJson() => new()
    {
        ["title"] = Title,
        ["summary"] = Summary,
        ["steps"] = new JsonArray([.. Steps.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["detail"] = s.Detail })]),
        ["files"] = new JsonArray([.. Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["note"] = f.Note })]),
        ["risks"] = Strings(Risks),
        ["tests"] = Strings(Tests),
        ["openQuestions"] = Strings(OpenQuestions),
    };

    private static JsonArray Strings(IEnumerable<string> list) => new([.. list.Select(x => (JsonNode)x)]);

    /// <summary>A body read back from a stored document (what <see cref="ToJson"/> wrote).</summary>
    public static PlanBody FromJson(JsonNode? n) => new(
        Text(n?["title"]) ?? "Plan", Text(n?["summary"]) ?? "",
        [.. (n?["steps"] as JsonArray ?? []).Select(s => new PlanStep(Text(s?["text"]) ?? "", Text(s?["detail"]))).Where(s => s.Text.Length > 0)],
        [.. (n?["files"] as JsonArray ?? []).Select(f => new PlanFile(Text(f?["path"]) ?? "", Text(f?["note"]))).Where(f => f.Path.Length > 0)],
        List(n?["risks"]), List(n?["tests"]), List(n?["openQuestions"]));

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static List<string> List(JsonNode? n) => [.. (n as JsonArray ?? []).Select(Text).OfType<string>()];

    public bool SameAs(PlanBody other) => JsonNode.DeepEquals(ToJson(), other.ToJson());

    /// <summary>The plan as markdown (the idea's plan section, the saved file, the first message of a new chat); the title is the caller's.</summary>
    public string Markdown()
    {
        var sb = new StringBuilder();
        if (Summary.Length > 0) sb.Append(Summary).Append("\n\n");
        sb.Append("## Steps\n");
        for (var i = 0; i < Steps.Count; i++)
        {
            sb.Append(i + 1).Append(". ").Append(Steps[i].Text);
            if (Steps[i].Detail is { } d) sb.Append(" — ").Append(d.Replace("\r", "").Replace("\n", "\n   "));
            sb.Append('\n');
        }
        if (Files.Count > 0)
        {
            sb.Append("\n## Files\n");
            foreach (var f in Files) sb.Append("- `").Append(f.Path).Append('`').Append(f.Note is { } n ? " — " + n : "").Append('\n');
        }
        Section(sb, "Risks", Risks);
        Section(sb, "Tests", Tests);
        Section(sb, "Open questions", OpenQuestions);
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        sb.Append("\n## ").Append(title).Append('\n');
        foreach (var x in items) sb.Append("- ").Append(x).Append('\n');
    }

    /// <summary>
    /// Lenient, like the other tools: steps / files as strings or objects (text | step | title, detail; path | file, note), the lists
    /// as arrays or one string, arguments sent as a JSON string unwrapped. A plan needs a title (or a summary to take one from) and a step.
    /// </summary>
    public static bool TryParse(JsonElement args, out PlanBody plan, out string error)
    {
        plan = null!;
        error = "";
        args = Args.Unwrap(args);
        var title = Args.Clip(Args.Str(args, "title", "name"), 120);
        var summary = Args.Clip(Args.Str(args, "summary", "overview", "description", "goal"), 1500) ?? "";
        var steps = new List<PlanStep>();
        foreach (var e in Args.Items(args, "steps", "plan", "tasks"))
        {
            var text = e.ValueKind == JsonValueKind.String ? Args.Clip(e.GetString(), 400) : Args.Clip(Args.Str(e, "text", "step", "title", "task", "name"), 400);
            if (text is null) continue;
            var detail = e.ValueKind == JsonValueKind.Object ? Args.Clip(Args.Str(e, "detail", "details", "description", "notes"), 1200) : null;
            steps.Add(new PlanStep(text, detail));
            if (steps.Count == MaxSteps) break;
        }
        var files = new List<PlanFile>();
        foreach (var e in Args.Items(args, "files", "paths"))
        {
            var path = e.ValueKind == JsonValueKind.String ? Args.Clip(e.GetString(), 300) : Args.Clip(Args.Str(e, "path", "file", "name"), 300);
            if (path is null) continue;
            files.Add(new PlanFile(path, e.ValueKind == JsonValueKind.Object ? Args.Clip(Args.Str(e, "note", "notes", "what", "change", "description"), 300) : null));
            if (files.Count == MaxFiles) break;
        }
        if (steps.Count == 0)
        {
            error = "plan_submit needs \"steps\": an array of { \"text\", \"detail\"? } (the plan, in order), with a \"title\" and a \"summary\".";
            return false;
        }
        title ??= Args.Clip(summary.Split('\n')[0], 80) ?? Args.Clip(steps[0].Text, 80)!;
        plan = new PlanBody(title, summary, steps, files, Args.Strings(args, "risks", "risk"), Args.Strings(args, "tests", "testing", "verification"),
            Args.Strings(args, "openQuestions", "questions", "open_questions"));
        return true;
    }
}

/// <summary>Argument reading shared by the plan tools: names match case-insensitively without _ and -.</summary>
internal static class Args
{
    public static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return e;
        try
        {
            using var doc = JsonDocument.Parse(e.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return e; }
    }

    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static JsonElement? Get(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    public static string? Str(JsonElement e, params string[] names) => Get(e, names) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>The elements of an array argument (a JSON string holding one is unwrapped; one object or string counts as a list of one).</summary>
    public static List<JsonElement> Items(JsonElement args, params string[] names)
    {
        var found = Get(args, names);
        if (found is { ValueKind: JsonValueKind.String } s && Unwrap(s) is { ValueKind: JsonValueKind.Array } parsed) found = parsed;
        return found switch
        {
            { ValueKind: JsonValueKind.Array } a => [.. a.EnumerateArray()],
            { ValueKind: JsonValueKind.String or JsonValueKind.Object } one => [one],
            _ => [],
        };
    }

    public static List<string> Strings(JsonElement args, params string[] names) => [.. Items(args, names)
        .Select(e => e.ValueKind == JsonValueKind.String ? Clip(e.GetString(), 400) : Clip(Str(e, "text", "description", "name"), 400))
        .OfType<string>().Take(PlanBody.MaxList)];
}
