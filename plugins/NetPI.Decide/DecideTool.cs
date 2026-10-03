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
            "log). Every item gets every question; items the model is unsure about (the answer's probability below min_confidence, " +
            "0–1, default 0.8, or too close to the runner-up) are listed so you can check them yourself. Identical items, and " +
            "with embeddings near-identical ones, are decided once and share the answer (the result says which). Good for sorting logs, triaging issues, labelling many lines or files, " +
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
        var a = new ToolArgs(args);
        if (!a.TryGet(out var qEl, "questions"))
            return ToolResult.Error("decide needs questions: { id: { type: yes_no|choice|score, question, options|levels } }.");

        JsonObject questions;
        try { questions = NormalizeQuestions(qEl); }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }

        var max = Math.Clamp(ctx.Settings.Get("decide.maxItems", 500), 1, 5000);
        List<string> items = [];
        string? file = null;
        if (a.TryGet(out var it, "items") && it.ValueKind == JsonValueKind.Array)
            items.AddRange(it.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()));
        if (a.TryGet(out var tx, "text") && tx.ValueKind == JsonValueKind.String) items.Insert(0, tx.GetString() ?? "");
        if (a.TryGet(out var fe, "file") && fe.ValueKind == JsonValueKind.String && fe.GetString() is { Length: > 0 } f)
        {
            file = context.ResolvePath(f);
            if (!File.Exists(file)) return ToolResult.Error($"No such file: {file}");
            items.AddRange((await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false)).Where(l => l.Trim().Length > 0));
        }
        items = items.Where(s => s.Trim().Length > 0).ToList();
        if (items.Count == 0) return ToolResult.Error("decide needs something to decide about: text, items or file.");
        var dropped = Math.Max(0, items.Count - max);
        if (dropped > 0) items = items.Take(max).ToList();

        var minConf = a.TryGet(out var mc, "min_confidence") && mc.ValueKind == JsonValueKind.Number ? Math.Clamp(mc.GetDouble(), 0, 1) : DefaultMinConfidence;
        var model = a.TryGet(out var me, "model") && me.ValueKind == JsonValueKind.String ? me.GetString() : null;
        if (string.IsNullOrWhiteSpace(model) && items.Count >= Math.Max(2, ctx.Settings.Get("decide.bulkThreshold", 8)))
            model = ctx.Settings.Get("decide.bulkModel", "") is { Length: > 0 } bulk ? bulk : null;

        var results = new DecisionAnswer?[items.Count];
        // One decision per distinct item: a repeated line is asked once, and with embeddings a near-identical one too.
        var started = DateTime.UtcNow;
        var leader = await GroupAsync(items, ct).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16));
        DecisionException? failure = null;
        var tasks = items.Select(async (text, i) =>
        {
            if (leader[i] != i) return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (failure is not null) return;
                var startedItem = System.Diagnostics.Stopwatch.StartNew();
                results[i] = await client.EvaluateAnswerAsync(new DecisionRequest
                {
                    Model = model, Body = new JsonObject { ["state"] = text, ["questions"] = questions.DeepClone() },
                    ExistingLease = context.AdmissionLease(), HeldModel = context.Model?.Ref,
                }, ct).ConfigureAwait(false);
            }
            catch (DecisionException ex) { failure ??= ex; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (failure is not null && results.All(r => r is null)) return ToolResult.Error(failure.Message);
        for (var i = 0; i < items.Count; i++)
            if (leader[i] != i) results[i] = results[leader[i]];

        var ms = (DateTime.UtcNow - started).TotalMilliseconds;
        return Summarize(questions, items, results, minConf, ms, dropped, file, failure, leader);
    }

    /// <summary>Default <c>min_confidence</c>: the answer's probability, as the other decision sites read it (0.8).</summary>
    public const double DefaultMinConfidence = 0.8;
    /// <summary>Cosine at which two items count as the same (<c>decide.groupSimilarity</c>): a line that differs only in a
    /// timestamp or an id. Kept high on purpose — two lines that differ in an error code must stay apart.</summary>
    public const double DefaultGroupSimilarity = 0.985;

    /// <summary>
    /// For each item, the index of the item whose decision it shares (itself when it is decided on its own): identical
    /// texts always, and with embeddings (setting <c>decide.groupSimilarity</c> above 0) texts at or above that cosine.
    /// Any embedding failure leaves the exact grouping.
    /// </summary>
    internal async Task<int[]> GroupAsync(List<string> items, CancellationToken ct)
    {
        var leader = new int[items.Count];
        var firstOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var key = items[i].Trim();
            leader[i] = firstOf.TryGetValue(key, out var j) ? j : i;
            if (leader[i] == i) firstOf[key] = i;
        }
        var threshold = Math.Clamp(ctx.Settings.Get("decide.groupSimilarity", DefaultGroupSimilarity), 0, 1);
        var distinct = Enumerable.Range(0, items.Count).Where(i => leader[i] == i).ToList();
        if (threshold <= 0 || distinct.Count < 2 || ctx.Services.Get<IEmbeddingService>() is not { Available: true } embedder) return leader;
        try
        {
            var vectors = (await embedder.EmbedAsync(new EmbeddingRequest { Texts = distinct.Select(i => items[i]).ToList(), Background = true }, ct).ConfigureAwait(false)).Vectors;
            var leaders = new List<int>();   // positions in distinct
            for (var k = 0; k < distinct.Count; k++)
            {
                var match = leaders.FirstOrDefault(l => VectorMath.Dot(vectors[k], vectors[l]) >= threshold, -1);
                if (match < 0) { leaders.Add(k); continue; }
                leader[distinct[k]] = distinct[match];
            }
            for (var i = 0; i < items.Count; i++) leader[i] = leader[leader[i]];   // an exact copy follows its text's leader
        }
        catch (EmbeddingException) { }
        return leader;
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
        int dropped, string? file, DecisionException? failure, int[]? leader = null)
    {
        var model = results.FirstOrDefault(r => r is not null)?.Model ?? "?";
        var done = results.Count(r => r is not null);
        var shared = leader is null ? 0 : Enumerable.Range(0, items.Count).Count(i => leader[i] != i && results[i] is not null);
        var sb = new StringBuilder();
        sb.Append(Inv($"{model}: {done} item{(done == 1 ? "" : "s")} × {questions.Count} question{(questions.Count == 1 ? "" : "s")} in {ms / 1000:0.0} s"));
        if (shared > 0) sb.Append(Inv($" ({done - shared} decided; {shared} repeat an earlier item and share its answer)"));
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
                var (label, conf, runnerUp) = Read(qdef!["type"]!.GetValue<string>(), a);
                cells[qid] = new { answer = label, confidence = Math.Round(conf, 3) };
                if (!DecisionConfidence.Clear(conf, runnerUp, minConf)) isUnsure = true;
                var c = counts[qid];
                c[label] = c.GetValueOrDefault(label) + 1;
                if (a?["score"] is JsonValue sv && sv.TryGetValue<double>(out var s))
                    scoreSums[qid] = (scoreSums.GetValueOrDefault(qid).Sum + s, scoreSums.GetValueOrDefault(qid).N + 1);
            }
            if (isUnsure) unsure.Add(i);
            rows.Add(new { index = i + 1, text = Clip(items[i], 300), answers = cells, unsure = isUnsure, ms = Math.Round(r.Ms),
                sameAs = leader is not null && leader[i] != i ? leader[i] + 1 : (int?)null });
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
                    var (label, conf, _) = Read(q.Value!["type"]!.GetValue<string>(), r.Answers[q.Key] as JsonObject);
                    return Inv($"{q.Key}={label} {conf:0.00}");
                });
                var same = leader is not null && leader[i] != i ? $" (= [{leader[i] + 1}])" : "";
                sb.AppendLine($"[{i + 1}]{(unsure.Contains(i) ? " ?" : "")}{same} {string.Join(" · ", parts)} | {Clip(items[i], 120)}");
            }
        }
        else if (items.Count > ListAll) sb.AppendLine(Inv($"\nNo unsure answers (all ≥ {minConf:0.##})."));

        return ToolResult.Ok(sb.ToString().TrimEnd(), new { model, file, questions, count = done, unsure = unsure.Count, ms = Math.Round(ms), items = rows, usage = Usage(results) });
    }

    /// <summary>
    /// What the server reported for tokens, so the cost of a decision is measurable rather than assumed: the whole
    /// argument for these calls is that they reuse a cached prefix, and <c>cached_tokens</c> is the only place that
    /// number exists. Totals across the items of one call; null when the server reported no usage.
    /// </summary>
    private static JsonObject? Usage(DecisionAnswer?[] results)
    {
        long prompt = 0, cached = 0, completion = 0;
        var seen = false;
        foreach (var r in results)
        {
            if (r?.Usage is not { } u) continue;
            seen = true;
            prompt += u["prompt_tokens"]?.GetValue<long>() ?? u["input_tokens"]?.GetValue<long>() ?? 0;
            cached += u["cached_tokens"]?.GetValue<long>() ?? u["cache_read_tokens"]?.GetValue<long>() ?? 0;
            completion += u["completion_tokens"]?.GetValue<long>() ?? u["output_tokens"]?.GetValue<long>() ?? 0;
        }
        if (!seen) return null;
        return new JsonObject
        {
            ["promptTokens"] = prompt, ["cachedTokens"] = cached, ["completionTokens"] = completion,
            ["cacheHitRate"] = prompt == 0 ? 0 : Math.Round((double)cached / prompt, 4),
        };
    }

    /// <summary>
    /// An answer's label, the probability of that label, and the runner-up's: yes/no reads p(yes) (so "no 0.96" is
    /// p(no) = 0.96, not a distance from 0.5); a choice reads its probabilities when the server sent them (the real
    /// runner-up), else 1 − confidence; a score has only its confidence.
    /// </summary>
    internal static (string Label, double Confidence, double RunnerUp) Read(string type, JsonObject? a)
    {
        if (a is null) return ("?", 0, 1);
        if (type == "noul")
        {
            var p = a["noul"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.5;
            return p >= 0.5 ? ("yes", p, 1 - p) : ("no", 1 - p, p);
        }
        var conf = a["confidence"] is JsonValue cv && cv.TryGetValue<double>(out var c) ? c : 0;
        if (type == "score")
        {
            var s = a["score"] is JsonValue sv && sv.TryGetValue<double>(out var x) ? x : 0;
            return (s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), conf, 1 - conf);
        }
        var choice = a["choice"]?.GetValue<string>() ?? "?";
        if (a["probabilities"] is JsonObject probs && probs.Count > 1)
        {
            var ordered = probs.Select(kv => kv.Value is JsonValue pv && pv.TryGetValue<double>(out var pd) ? pd : 0).OrderByDescending(x => x).ToList();
            var mine = probs[choice] is JsonValue mv && mv.TryGetValue<double>(out var md) ? md : conf;
            return (choice, mine, ordered.Count > 1 ? (Math.Abs(ordered[0] - mine) < 1e-9 ? ordered[1] : ordered[0]) : 1 - mine);
        }
        return (choice, conf, 1 - conf);
    }

    private static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Clip(string s, int n)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }

    private static string? Str(JsonElement e, string name) => new ToolArgs(e).Str(name);

    private static JsonElement? Prop(JsonElement e, string name) =>
        new ToolArgs(e).TryGet(out var v, name) ? v : null;
}
