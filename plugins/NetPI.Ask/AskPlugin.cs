using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ask;

/// <summary>
/// <c>ask_user</c>: the agent asks the user questions, with options to pick from, and waits for the answers. The call is
/// a wait for the user (<see cref="UserWait"/>, kind <c>ask</c>): the run ends at it, holding no instance and no reload,
/// and goes on when the user answers; the question survives a reload of this plugin and a restart, because the runtime
/// keeps it. The UI shows the questions inline in the chat and answers through <c>ask.answer</c>; <c>ask.pending</c>
/// lists the questions that wait (the runtime's waits of kind <c>ask</c>). Every question has an id of its own
/// (<c>ask_…</c>, like a guard approval): a tool call id belongs to the model that made it, and two chats can hold the
/// same one, so it cannot say which question is being answered.
/// Events (unscoped, the session in their data, so a window without the chat open hears of it too): <c>ask.asked</c>
/// when a question starts waiting, <c>ask.closed</c> when it stops (answered, steered: the user wrote a new message
/// instead, cancelled: the chat was stopped or deleted), both relayed from the runtime's <c>agent.waiting</c> /
/// <c>agent.waitEnded</c>. Subagents can't ask: nobody watches their chat.
/// </summary>
[NetPiPlugin("netpi.ask", Name = "Ask the user", Description = "ask_user: the agent asks you questions inline in the chat and waits for your answers", Order = 66)]
public sealed class AskPlugin : INetPiPlugin
{
    public const string Kind = "ask";

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var pending = new PendingAsks(context);
        context.Tools.Register(new AskUserTool());
        context.Rpc.Register(new RpcMethod("ask.pending", "Questions waiting for the user: { sessionId? } → { id, sessionId, callId, agentId, agentName, questions, askedAt }[]", ReadOnly: true, Params: [RpcParam.Opt("sessionId")]),
            (req, _) => Task.FromResult<object?>(pending.List(req.Str("sessionId"))));
        context.Rpc.Register(new RpcMethod("ask.answer", "Answer a waiting question: { id, answers?: string[][] (the options picked, per question), text? } → true (id, or callId + sessionId for an older caller)", ReadOnly: false, Params: [RpcParam.Opt("id", RpcParamType.String, "the question card's id"), RpcParam.Opt("sessionId"), RpcParam.Opt("callId"), RpcParam.Opt("answers", RpcParamType.Array, "the options picked, per question"), RpcParam.Opt("text", RpcParamType.String, "the user's own words")]),
            async (req, ct) => await pending.AnswerAsync(req, ct).ConfigureAwait(false));
        // the cards: the runtime announces a question once it waits, and says when it stopped waiting (sync handlers: an
        // async one left alive would root this plugin's load context)
        context.Events.Subscribe(EventTypes.AgentWaiting, e =>
        {
            if (e.As<JsonObject>()?["wait"] is JsonObject w && (string?)w["kind"] == Kind) context.Events.Publish("ask.asked", PendingAsks.Json(w));
        });
        context.Events.Subscribe(EventTypes.AgentWaitEnded, e =>
        {
            if (e.As<JsonObject>() is not { } d || (string?)d["kind"] != Kind || (string?)d["reason"] == "answered") return;   // an answer closes its own card
            context.Events.Publish("ask.closed", new JsonObject
            {
                ["id"] = d["waitId"]?.DeepClone(), ["sessionId"] = d["sessionId"]?.DeepClone(), ["callId"] = d["callId"]?.DeepClone(),
                ["status"] = d["reason"]?.DeepClone(), ["answers"] = null, ["text"] = null,
            });
        });
        return Task.CompletedTask;
    }

    /// <summary>Nothing to withdraw: the questions are the runtime's, and they wait on through a reload of this plugin.</summary>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed record AskOption(string Label, string? Description);

internal sealed record AskQuestion(string Question, IReadOnlyList<AskOption> Options, bool Multiple);

/// <summary>Per question the options picked (an empty list: none), and the user's own words.</summary>
internal sealed record AskAnswer(IReadOnlyList<IReadOnlyList<string>> Selected, string? Text);

/// <summary>
/// The questions that wait, read from the runtime (its waits of kind <c>ask</c>, the questions in their data) — this
/// plugin keeps none, so a reload of it loses none. By the question's own id, never by the tool call's.
/// </summary>
internal sealed class PendingAsks(IPluginContext ctx)
{
    private IAgentRuntime Runtime => ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime is not running.");

    private IEnumerable<WaitingCall> Waiting(string? sessionId) =>
        ctx.Services.Get<IAgentRuntime>()?.Waits(sessionId, AskPlugin.Kind).Where(w => !w.Answered) ?? [];

    public JsonArray List(string? sessionId) => new([.. Waiting(sessionId).Select(w => (JsonNode)Json(NetPiJson.ToNode(w)!.AsObject()))]);

    public async Task<bool> AnswerAsync(RpcRequest req, CancellationToken ct)
    {
        var id = req.Str("id");
        var callId = req.Str("callId");
        var sessionId = req.Str("sessionId");
        var w = Waiting(null).FirstOrDefault(x => !string.IsNullOrEmpty(id) ? x.Id == id : x.CallId == callId && (sessionId is null || x.SessionId == sessionId));
        if (w is null || (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(callId)))
            throw new RpcException("not_found", "No question waits with that id (it was answered, or its chat moved on).");
        var questions = Questions(w.Data);
        var selected = new List<IReadOnlyList<string>>();
        if (req.Prop("answers") is { ValueKind: JsonValueKind.Array } answers)
        {
            foreach (var a in answers.EnumerateArray())
            {
                var picked = a.ValueKind switch
                {
                    JsonValueKind.Array => a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()),
                    JsonValueKind.String => [a.GetString()!.Trim()],
                    _ => [],
                };
                selected.Add([.. picked.Where(x => x.Length > 0).Distinct()]);
            }
        }
        while (selected.Count < questions.Count) selected.Add([]);
        if (selected.Count > questions.Count) selected.RemoveRange(questions.Count, selected.Count - questions.Count);
        var text = req.Str("text")?.Trim();
        if (string.IsNullOrEmpty(text)) text = null;
        if (text is null && selected.All(s => s.Count == 0)) throw new RpcException("bad_request", "An answer needs an option picked or some text.");
        var answer = new AskAnswer(selected, text);
        var result = ToolResult.Ok(AskUserTool.Render(questions, answer), AskUserTool.Details(questions, answer, "answered"));
        if (!await Runtime.ResolveAsync(w.Id, WaitResolution.Answer(result), ct).ConfigureAwait(false))
            throw new RpcException("not_found", "The question was already answered.");
        ctx.Events.Publish("ask.closed", new JsonObject
        {
            ["id"] = w.Id,
            ["sessionId"] = w.SessionId,
            ["callId"] = w.CallId,
            ["status"] = "answered",
            ["answers"] = AnswersJson(answer),
            ["text"] = answer.Text,
        });
        return true;
    }

    /// <summary>A waiting question as the card, ask.pending and ask.asked show it, from the runtime's wait.</summary>
    internal static JsonObject Json(JsonObject wait) => new()
    {
        ["id"] = wait["id"]?.DeepClone(),
        ["sessionId"] = wait["sessionId"]?.DeepClone(),
        ["callId"] = wait["callId"]?.DeepClone(),
        ["agentId"] = wait["agentId"]?.DeepClone(),
        ["agentName"] = wait["data"]?["agentName"]?.DeepClone(),
        ["questions"] = wait["data"]?["questions"]?.DeepClone() ?? new JsonArray(),
        ["askedAt"] = wait["since"]?.DeepClone(),
    };

    /// <summary>The questions back from a wait's data (as <see cref="QuestionsJson"/> wrote them).</summary>
    internal static List<AskQuestion> Questions(JsonObject? data) =>
        [.. (data?["questions"] as JsonArray ?? []).OfType<JsonObject>().Select(q => new AskQuestion(
            (string?)q["question"] ?? "",
            [.. (q["options"] as JsonArray ?? []).OfType<JsonObject>().Select(o => new AskOption((string?)o["label"] ?? "", (string?)o["description"]))],
            q["multiple"] is JsonValue m && m.TryGetValue<bool>(out var multiple) && multiple))];

    internal static JsonArray QuestionsJson(IReadOnlyList<AskQuestion> questions) => new([.. questions.Select(q => (JsonNode)new JsonObject
    {
        ["question"] = q.Question,
        ["options"] = new JsonArray([.. q.Options.Select(o => (JsonNode)new JsonObject { ["label"] = o.Label, ["description"] = o.Description })]),
        ["multiple"] = q.Multiple,
    })]);

    internal static JsonArray AnswersJson(AskAnswer a) => new([.. a.Selected.Select(s => (JsonNode)new JsonArray([.. s.Select(x => (JsonNode)x)]))]);
}

internal sealed class AskUserTool : IAgentTool
{
    public const int MaxQuestions = 4;
    public const int MaxOptions = 8;
    private const int MaxQuestion = 1000;
    // The card wraps a long label over several lines (AskCard.svelte: .olbl { overflow-wrap: anywhere }), so these
    // bound the model-facing text and the stored payload, not what fits on screen. They were 120 and 300, which cut a
    // label that carried the whole meaning of an option; the limits are now stated in the schema, so a model that
    // writes past them is told rather than silently clipped.
    private const int MaxLabel = 400;
    private const int MaxDescription = 600;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "ask_user",
        Label = "Question",
        Category = "general",
        Description =
            "Ask the user 1–4 questions and wait for the answers: for a decision or information only the user has, not to " +
            "confirm routine steps. Give the context in your message first. Keep an option's label to the choice itself " +
            $"(at most {MaxLabel} characters, shorter is better) and put what it means in its description " +
            $"(at most {MaxDescription} characters); longer text is cut, so the user could not read the whole option.",
        Help =
            "For a choice between approaches, a preference, a missing detail. Not for confirming routine steps or reporting " +
            "progress. The questions appear right below your message. Offer options (label, and optionally what it means) when " +
            "the answer is one of a few choices; the user can always answer in their own words instead. An option's label is " +
            "what the user picks, so make it the whole choice — what is decided, not a reference like 'option 1' — and put " +
            "the reasoning, the consequence and anything you would otherwise leave out in its description, which is shown " +
            "under the label. multiple: the user may pick several. Call it on its own and act on the answers once they arrive.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["questions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["question"] = new JsonObject { ["type"] = "string" },
                            ["options"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["label"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["description"] = $"The choice itself, at most {MaxLabel} characters; longer is cut.",
                                        },
                                        ["description"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["description"] = $"What the option means, why, what follows it; at most {MaxDescription} characters.",
                                        },
                                    },
                                    ["required"] = new JsonArray("label"),
                                },
                            },
                            ["multiple"] = new JsonObject { ["type"] = "boolean" },
                        },
                        ["required"] = new JsonArray("question"),
                    },
                },
            },
            ["required"] = new JsonArray("questions"),
        },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!TryParse(args, out var questions, out var error)) return Task.FromResult(ToolResult.Error(error));
        var agent = context.Services.Get<IAgentRuntime>()?.Get(context.AgentId);
        if (agent?.IsSubagent == true)
            return Task.FromResult(ToolResult.Error("Only the main agent can ask the user: nobody watches a subagent's chat. Put the question in your report instead."));
        // the run ends here and goes on with the answer (PendingAsks.AnswerAsync), however long that takes
        return Task.FromResult(ToolResult.WaitForUser(new UserWait
        {
            Id = "ask_" + Ids.Short(12),
            Kind = AskPlugin.Kind,
            Activity = "waiting for your answer",
            Data = new JsonObject { ["questions"] = PendingAsks.QuestionsJson(questions), ["agentName"] = agent?.Name },
            IfSteered = ToolResult.Ok("No answer: the user wrote a new message instead; it follows.", Details(questions, null, "steered")),
            IfCancelled = ToolResult.Ok("No answer: the chat was stopped before the user answered.", Details(questions, null, "cancelled")),
        }));
    }

    internal static JsonObject Details(IReadOnlyList<AskQuestion> questions, AskAnswer? answer, string status) => new()
    {
        ["questions"] = PendingAsks.QuestionsJson(questions),
        ["answers"] = answer is null ? null : PendingAsks.AnswersJson(answer),
        ["text"] = answer?.Text,
        ["status"] = status,
    };

    /// <summary>What the model reads: the picks per question, and the user's own words.</summary>
    internal static string Render(IReadOnlyList<AskQuestion> questions, AskAnswer a)
    {
        string Picks(int i) => i < a.Selected.Count && a.Selected[i].Count > 0 ? string.Join(", ", a.Selected[i]) : "";
        if (questions.Count == 1)
        {
            var picks = Picks(0);
            if (picks.Length == 0) return $"The user answered in their own words: {a.Text}";
            return a.Text is null ? $"The user answered: {picks}" : $"The user answered: {picks}\nThey added: {a.Text}";
        }
        var sb = new StringBuilder("The user answered:");
        var any = false;
        for (var i = 0; i < questions.Count; i++)
        {
            var picks = Picks(i);
            any |= picks.Length > 0;
            sb.Append('\n').Append(i + 1).Append(". ").Append(questions[i].Question).Append("\n   → ").Append(picks.Length > 0 ? picks : "(nothing picked)");
        }
        if (a.Text is not null) sb.Append('\n').Append(any ? "They added: " : "In their own words: ").Append(a.Text);
        return sb.ToString();
    }

    /// <summary>
    /// Lenient: <c>questions</c> as objects or plain strings, or one question at the top level (<c>question</c> with
    /// <c>options</c>); options as strings or objects (label / text / value, description); multiple / multiSelect.
    /// Arguments sent as a JSON string are unwrapped.
    /// </summary>
    internal static bool TryParse(JsonElement args, out List<AskQuestion> questions, out string error)
    {
        questions = [];
        error = "";
        var a = new ToolArgs(args);
        var items = new List<JsonElement>();
        if (a.Raw.ValueKind == JsonValueKind.Array)
            items.AddRange(a.Raw.EnumerateArray());
        else if (a.TryGet(out var list, "questions"))
        {
            if (list.ValueKind == JsonValueKind.String) list = ToolArgs.Unwrap(list);
            if (list.ValueKind == JsonValueKind.Array) items.AddRange(list.EnumerateArray());
            else if (list.ValueKind == JsonValueKind.Object) items.Add(list);
            else if (a.Has("question")) items.Add(a.Raw); // one question at the top level ("questions" was not a list)
        }
        else if (a.Has("question"))
            items.Add(a.Raw); // one question at the top level
        var unusable = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            if (Question(items[i]) is { } parsed) questions.Add(parsed);
            else unusable.Add($"#{i + 1} ({(items[i].ValueKind is JsonValueKind.String or JsonValueKind.Object ? "no question text" : "a " + items[i].ValueKind.ToString().ToLowerInvariant() + " instead of a string or an object")})");
        }
        if (unusable.Count > 0)
        {
            // never ask a shorter set than the model meant: the answers are read by position
            error = $"These questions are unusable: {string.Join(", ", unusable)}. Each needs a non-empty \"question\" (or be a plain string); fix or drop them and call ask_user again.";
            questions.Clear();
            return false;
        }
        if (questions.Count == 0)
        {
            error = "ask_user needs \"questions\": an array of { \"question\", \"options\"?: [{ \"label\", \"description\"? }], \"multiple\"? }.";
            return false;
        }
        if (questions.Count > MaxQuestions)
        {
            error = $"Ask at most {MaxQuestions} questions at once ({questions.Count} were given).";
            questions.Clear();
            return false;
        }
        return true;
    }

    private static AskQuestion? Question(JsonElement q)
    {
        string? text;
        var options = new List<AskOption>();
        var multiple = false;
        if (q.ValueKind == JsonValueKind.String) text = q.GetString();
        else if (q.ValueKind == JsonValueKind.Object)
        {
            var a = new ToolArgs(q);
            text = a.Str("question", "text", "prompt", "title");
            if (a.TryGet(out var opts, "options", "choices", "answers"))
            {
                if (opts.ValueKind == JsonValueKind.String) opts = ToolArgs.Unwrap(opts);
                if (opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var label = o.ValueKind == JsonValueKind.String ? o.GetString() : o.ValueKind == JsonValueKind.Object ? new ToolArgs(o).Str("label", "text", "value", "title", "name") : null;
                        label = Clip(label, MaxLabel);
                        if (label is null || options.Any(x => x.Label == label)) continue;
                        var description = o.ValueKind == JsonValueKind.Object ? Clip(new ToolArgs(o).Str("description", "detail", "details", "hint"), MaxDescription) : null;
                        options.Add(new AskOption(label, description));
                        if (options.Count == MaxOptions) break;
                    }
                }
            }
            multiple = a.Bool("multiple", "multiSelect", "multi_select", "allowMultiple") == true;
        }
        else return null;
        text = Clip(text, MaxQuestion);
        return text is null ? null : new AskQuestion(text, options, multiple && options.Count > 1);
    }

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max] + "…" : s;
    }
}
