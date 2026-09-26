using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Loops;

/// <summary>One finished tool call as the detector sees it: what ran, whether it failed, and how its result began.</summary>
internal sealed partial record Step(string Tool, string Args, bool Error, string Result)
{
    public string Key => Tool + "\n" + Args;

    public static Step From(ToolCallPart call, ToolResultPart result) =>
        new(call.Name, NormalizeArgs(call.Arguments), result.IsError, NormalizeResult(result.Content));

    /// <summary>Compact JSON, so the same arguments with other whitespace compare equal.</summary>
    public static string NormalizeArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "{}";
        try { return JsonNode.Parse(args)?.ToJsonString() ?? "{}"; }
        catch (JsonException) { return args.Trim(); }
    }

    /// <summary>The start of a result with the parts that change on every run (durations, times) blanked.</summary>
    public static string NormalizeResult(string? content)
    {
        var s = (content ?? "").Trim();
        if (s.Length > 400) s = s[..400];
        s = Volatile().Replace(s, "#");
        return Space().Replace(s, " ");
    }

    /// <summary>One line for the model check and the hint: <c>bash: npm test → error: 3 failing</c>.</summary>
    public string Line(int max = 160)
    {
        var args = Args.Length > 80 ? Args[..80] + "…" : Args;
        var head = Result.Length > 70 ? Result[..70] + "…" : Result;
        var line = $"{Tool} {args} → {(Error ? "error" : "ok")}: {head}";
        return line.Length > max ? line[..max] + "…" : line;
    }

    [GeneratedRegex(@"\d{4}-\d\d-\d\d[T ]\d\d:\d\d(:\d\d(\.\d+)?)?Z?|\b\d+(\.\d+)?\s*(ms|s|sec|seconds)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Volatile();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}

/// <summary>A loop the checks found: its kind, a signature (one hint per signature), and the hint for the agent.</summary>
internal sealed record Finding(string Kind, string Signature, string Text);

/// <summary>
/// Deterministic loop checks over a run's finished steps plus the tool calls the model just asked for (not run yet):
/// the same call a <c>repeats</c>-th time after identical results, the same failing call retried, and A→B→A→B
/// oscillation (an edit and its undo). <see cref="Suspicious"/> flags a run worth a decision-model check.
/// </summary>
internal static class LoopDetector
{
    public const int Window = 12;

    public static Finding? Check(IReadOnlyList<Step> history, IReadOnlyList<(string Tool, string Args)> pending, int repeats)
    {
        repeats = Math.Max(2, repeats);
        var recent = history.Count > Window ? history.Skip(history.Count - Window).ToList() : history.ToList();

        foreach (var (tool, args) in pending)
        {
            var key = tool + "\n" + args;
            var same = recent.Where(s => s.Key == key).ToList();
            if (same.Count < repeats - 1) continue;
            var last = same.Skip(same.Count - (repeats - 1)).ToList();
            if (last.Any(s => s.Error != last[0].Error || s.Result != last[0].Result)) continue; // results changed: not stuck
            var what = Describe(tool, args);
            return last[0].Error
                ? new Finding("retry", "retry\n" + key,
                    $"Loop check: {what} has failed {same.Count} times with the same error, and you are about to run it again. " +
                    "Running it unchanged will fail the same way. Read the error, change something (the command, the input, " +
                    "the approach), or tell the user what blocks you.")
                : new Finding("repeat", "repeat\n" + key,
                    $"Loop check: you have run {what} {same.Count} times with the same result, and you are about to run it again. " +
                    "It will not tell you anything new. Use what it already returned, try a different step, or tell the user " +
                    "what you are stuck on.");
        }

        // A, B, A and now B again: an edit and its undo, or two commands that undo each other
        if (pending.Count > 0 && recent.Count >= 3)
        {
            var (a, b, c) = (recent[^3], recent[^2], recent[^1]);
            var next = pending[0].Tool + "\n" + pending[0].Args;
            if (a.Key == c.Key && b.Key == next && a.Key != b.Key && a.Result == c.Result)
                return new Finding("oscillation", "oscillation\n" + a.Key + "\n" + b.Key,
                    $"Loop check: you are going back and forth: {Describe(a.Tool, a.Args)} and {Describe(b.Tool, b.Args)} " +
                    "undo each other, and this is the second round. Stop and decide which state is right, or ask the user.");
        }
        return null;
    }

    /// <summary>Worth asking the decision model: most of the last steps use one tool, and several of them failed.</summary>
    public static bool Suspicious(IReadOnlyList<Step> history, int window = 8)
    {
        if (history.Count < window) return false;
        var last = history.Skip(history.Count - window).ToList();
        var top = last.GroupBy(s => s.Tool).Max(g => g.Count());
        return top >= window * 3 / 4 && last.Count(s => s.Error) >= 3;
    }

    private static string Describe(string tool, string args)
    {
        string? main = null;
        try
        {
            if (JsonNode.Parse(args) is JsonObject o)
                main = o.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _))
                    .Select(p => p.Value!.GetValue<string>())
                    .FirstOrDefault(v => v.Length > 0);
        }
        catch (JsonException) { }
        main ??= args;
        main = main.ReplaceLineEndings(" ");
        if (main.Length > 80) main = main[..80] + "…";
        return $"{tool} `{main}`";
    }
}
