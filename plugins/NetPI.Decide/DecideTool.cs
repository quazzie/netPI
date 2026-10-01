using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Decide;

internal sealed class DecideTool(IPluginContext ctx, DecisionClient client) : IAgentTool
{
    private const int ListAll = 40;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "decide",
        Label = "Decide",
        Category = "decide",
        ReadOnly = true,
        SummaryArg = "file",
        Description = "Ask a fast local decision model yes/no, choice or score questions about a text, many items or every line of a file; each answer has a probability.",
        Help =
            "It never writes text. Give one `text`, a list of `items`, or a `file` (every non-empty line is an item, e.g. a " +
            "log). Every item gets every question; items the model is unsure about (below min_confidence, 0–1, default 0.5) " +
            "are listed so you can check them yourself. Good for sorting logs, triaging issues, labelling many lines or files, " +
            "and quick checks. Weak at counting, dates, maths and multi-step reasoning.\n" +
            "questions: id → { \"type\": \"yes_no\", \"question\": \"…\" } | { \"type\": \"choice\", \"question\": \"…\", " +
            "\"options\": { \"label\": \"what it means\", … } } | { \"type\": \"score\", \"question\": \"…\", \"levels\": " +
            "[\"lowest\", …, \"highest\"] }. Ask one narrow thing per question; phrase yes/no as a real question (\"Does a " +
            "human need to act?\"): a statement is answered less reliably. model: a decision model id (default: setting decide.model).",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["questions"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "id → { type: yes_no|choice|score, question, options {label: meaning} (choice), levels [low…high] (score) }",
                },
                ["text"] = new JsonObject { ["type"] = "string" },
                ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["file"] = new JsonObject { ["type"] = "string" },
                ["min_confidence"] = new JsonObject { ["type"] = "number" },
                ["model"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("questions"),
        },
        PromptGuidelines =
        [
            "Use decide to classify or check many items quickly (log lines, issues, files) instead of reading them all yourself; review the unsure ones.",
        ],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind == JsonValueKind.String)
        {
            try { using var doc = JsonDocument.Parse(args.GetString() ?? "{}"); args = doc.RootElement.Clone(); }
            catch (JsonException) { }
        }
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("questions", out var qEl))
            return ToolResult.Error("decide needs questions: { id: { type: yes_no|choice|score, question, options|levels } }.");

        JsonObject questions;
        try { questions = NormalizeQuestions(qEl); }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }

        var max = Math.Clamp(ctx.Settings.Get("decide.maxItems", 500), 1, 5000);
        List<string> items = [];
        string? file = null;
        if (args.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
            items.AddRange(it.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()));
        if (args.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) items.Insert(0, tx.GetString() ?? "");
        if (args.TryGetProperty("file", out var fe) && fe.ValueKind == JsonValueKind.String && fe.GetString() is { Length: > 0 } f)
        {
            file = context.ResolvePath(f);
            if (!File.Exists(file)) return ToolResult.Error($"No such file: {file}");
            items.AddRange((await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false)).Where(l => l.Trim().Length > 0));
        }
        items = items.Where(s => s.Trim().Length > 0).ToList();
        if (items.Count == 0) return ToolResult.Error("decide needs something to decide about: text, items or file.");
        var dropped = Math.Max(0, items.Count - max);
        if (dropped > 0) items = items.Take(max).ToList();

        var minConf = args.TryGetProperty("min_confidence", out var mc) && mc.ValueKind == JsonValueKind.Number ? Math.Clamp(mc.GetDouble(), 0, 1) : 0.5;
        var model = args.TryGetProperty("model", out var me) && me.ValueKind == JsonValueKind.String ? me.GetString() : null;
        if (string.IsNullOrWhiteSpace(model) && items.Count >= Math.Max(2, ctx.Settings.Get("decide.bulkThreshold", 8)))
            model = ctx.Settings.Get("decide.bulkModel", "") is { Length: > 0 } bulk ? bulk : null;

        var results = new DecisionAnswer?[items.Count];
        using var gate = new SemaphoreSlim(Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16));
        DecisionException? failure = null;
        var started = DateTime.UtcNow;
        var tasks = items.Select(async (text, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (failure is not null) return;
                var startedItem = System.Diagnostics.Stopwatch.StartNew();
                var answers = await client.EvaluateAsync(new DecisionRequest
                {
                    Model = model, Body = new JsonObject { ["state"] = text, ["questions"] = questions.DeepClone() },
                    ExistingLease = context.AdmissionLease, HeldModel = context.Model?.Ref,
                }, ct).ConfigureAwait(false);
                results[i] = new DecisionAnswer(answers, client.Model(model), startedItem.Elapsed.TotalMilliseconds);
            }
            catch (DecisionException ex) { failure ??= ex; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (failure is not null && results.All(r => r is null)) return ToolResult.Error(failure.Message);

        var ms = (DateTime.UtcNow - started).TotalMilliseconds;
        return Summarize(questions, items, results, minConf, ms, dropped, file, failure);
    }

    /// <summary>The tool's friendly question shape (yes_no/question/options/levels) → TypeSafe's (noul/instructions/criteria).</summary>
    internal static JsonObject NormalizeQuestions(JsonElement q)
    {
        if (q.ValueKind != JsonValueKind.Object || !q.EnumerateObject().Any())
            throw new ArgumentException("questions must be an object: { id: { type, question, options|levels } }.");
        var outQ = new JsonObject();
        foreach (var p in q.EnumerateObject())
        {
            var v = p.Value;
            if (v.ValueKind == JsonValueKind.String)
            {
                outQ[p.Name] = new JsonObject { ["type"] = "noul", ["instructions"] = v.GetString() };
                continue;
            }
            if (v.ValueKind != JsonValueKind.Object) throw new ArgumentException($"question '{p.Name}' must be an object.");
            var type = (Str(v, "type") ?? "yes_no").Trim().ToLowerInvariant().Replace("-", "_");
            var instructions = Str(v, "question") ?? Str(v, "instructions") ?? Str(v, "prompt") ?? p.Name;
            switch (type)
            {
                case "yes_no" or "yesno" or "noul" or "bool" or "boolean":
                    outQ[p.Name] = new JsonObject { ["type"] = "noul", ["instructions"] = instructions };
                    break;
                case "choice" or "pick" or "one_of" or "classify":
                {
                    var opts = Prop(v, "options") ?? Prop(v, "criteria") ?? Prop(v, "labels");
                    JsonNode? criteria = opts?.ValueKind switch
                    {
                        JsonValueKind.Object => JsonNode.Parse(opts.Value.GetRawText()),
                        JsonValueKind.Array => new JsonObject(opts.Value.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 })
                            .Select(x => x.GetString()!).Distinct()
                            .Select(s => KeyValuePair.Create<string, JsonNode?>(s, s))),
                        _ => null,
                    };
                    if (criteria is not JsonObject co || co.Count < 2) throw new ArgumentException($"choice question '{p.Name}' needs at least 2 options.");
                    outQ[p.Name] = new JsonObject { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria };
                    break;
                }
                case "score" or "rate" or "scale":
                {
                    var levels = Prop(v, "levels") ?? Prop(v, "criteria");
                    if (levels is not { ValueKind: JsonValueKind.Array } l || l.GetArrayLength() < 2)
                        throw new ArgumentException($"score question '{p.Name}' needs at least 2 levels, lowest first.");
                    outQ[p.Name] = new JsonObject { ["type"] = "score", ["instructions"] = instructions, ["criteria"] = JsonNode.Parse(l.GetRawText()) };
                    break;
                }
                default:
                    throw new ArgumentException($"question '{p.Name}': unknown type '{type}' (yes_no, choice or score).");
            }
        }
        return outQ;
    }

    private static ToolResult Summarize(JsonObject questions, List<string> items, DecisionAnswer?[] results, double minConf, double ms,
        int dropped, string? file, DecisionException? failure)
    {
        var model = results.FirstOrDefault(r => r is not null)?.Model ?? "?";
        var done = results.Count(r => r is not null);
        var sb = new StringBuilder();
        sb.Append(Inv($"{model}: {done} item{(done == 1 ? "" : "s")} × {questions.Count} question{(questions.Count == 1 ? "" : "s")} in {ms / 1000:0.0} s"));
        if (dropped > 0) sb.Append($" ({dropped} more skipped: setting decide.maxItems)");
        if (failure is not null) sb.Append($". {items.Count - done} failed: {failure.Message}");
        sb.AppendLine();

        var rows = new List<object>();
        var unsure = new List<int>();
        var counts = questions.ToDictionary(q => q.Key, _ => new Dictionary<string, int>());
        var scoreSums = new Dictionary<string, (double Sum, int N)>();
        for (var i = 0; i < items.Count; i++)
        {
            if (results[i] is not { } r) continue;
            var cells = new Dictionary<string, object?>();
            var isUnsure = false;
            foreach (var (qid, qdef) in questions)
            {
                var a = r.Answers[qid] as JsonObject;
                var (label, conf) = Read(qdef!["type"]!.GetValue<string>(), a);
                cells[qid] = new { answer = label, confidence = Math.Round(conf, 3) };
                if (!DecisionConfidence.Clear(conf, 1 - conf, minConf)) isUnsure = true;
                var c = counts[qid];
                c[label] = c.GetValueOrDefault(label) + 1;
                if (a?["score"] is JsonValue sv && sv.TryGetValue<double>(out var s))
                    scoreSums[qid] = (scoreSums.GetValueOrDefault(qid).Sum + s, scoreSums.GetValueOrDefault(qid).N + 1);
            }
            if (isUnsure) unsure.Add(i);
            rows.Add(new { index = i + 1, text = Clip(items[i], 300), answers = cells, unsure = isUnsure, ms = Math.Round(r.Ms) });
        }

        foreach (var (qid, c) in counts)
        {
            var type = questions[qid]!["type"]!.GetValue<string>();
            var dist = string.Join(", ", c.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"));
            var mean = type == "score" && scoreSums.TryGetValue(qid, out var ss) && ss.N > 0 ? Inv($" (mean score {ss.Sum / ss.N:0.00})") : "";
            sb.AppendLine($"{qid}: {dist}{mean}");
        }

        var listed = items.Count <= ListAll ? Enumerable.Range(0, items.Count).Where(i => results[i] is not null).ToList() : unsure.Take(ListAll).ToList();
        if (listed.Count > 0)
        {
            sb.AppendLine(items.Count <= ListAll ? "" : Inv($"\nUnsure (confidence < {minConf:0.##}): {unsure.Count}{(unsure.Count > ListAll ? $", first {ListAll}" : "")}"));
            foreach (var i in listed)
            {
                var r = results[i]!;
                var parts = questions.Select(q =>
                {
                    var (label, conf) = Read(q.Value!["type"]!.GetValue<string>(), r.Answers[q.Key] as JsonObject);
                    return Inv($"{q.Key}={label} {conf:0.00}");
                });
                sb.AppendLine($"[{i + 1}]{(unsure.Contains(i) ? " ?" : "")} {string.Join(" · ", parts)} | {Clip(items[i], 120)}");
            }
        }
        else if (items.Count > ListAll) sb.AppendLine(Inv($"\nNo unsure answers (all ≥ {minConf:0.##})."));

        return ToolResult.Ok(sb.ToString().TrimEnd(), new { model, file, questions, count = done, unsure = unsure.Count, ms = Math.Round(ms), items = rows });
    }

    /// <summary>An answer's label and confidence: choice/score use the model's confidence; yes/no uses the distance from 0.5.</summary>
    internal static (string Label, double Confidence) Read(string type, JsonObject? a)
    {
        if (a is null) return ("?", 0);
        if (type == "noul")
        {
            var p = a["noul"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.5;
            return (p >= 0.5 ? "yes" : "no", Math.Abs(p - 0.5) * 2);
        }
        var conf = a["confidence"] is JsonValue cv && cv.TryGetValue<double>(out var c) ? c : 0;
        if (type == "score")
        {
            var s = a["score"] is JsonValue sv && sv.TryGetValue<double>(out var x) ? x : 0;
            return (s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), conf);
        }
        return (a["choice"]?.GetValue<string>() ?? "?", conf);
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Clip(string s, int n)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;
}
