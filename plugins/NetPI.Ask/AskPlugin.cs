using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Ask;

/// <summary>
/// <c>ask_user</c>: the agent asks the user questions, with options to pick from, and waits for the answers with its
/// instance given back meanwhile (<see cref="IAgentRuntime.WaitYieldedAsync"/>, like agent wait). The UI shows the
/// questions inline in the chat and answers through <c>ask.answer</c>; <c>ask.pending</c> lists the questions that wait.
/// Events (unscoped, the session in their data, so a window without the chat open hears of it too): <c>ask.asked</c>
/// when a question starts waiting, <c>ask.closed</c> when it stops (answered, steered: the user wrote a new message
/// instead, withdrawn: this plugin stopped, cancelled: the run was stopped). Subagents can't ask: nobody watches their chat.
/// </summary>
[NetPiPlugin("netpi.ask", Name = "Ask the user", Description = "ask_user: the agent asks you questions inline in the chat and waits for your answers", Order = 66)]
public sealed class AskPlugin : INetPiPlugin
{
    private PendingAsks? _pending;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var pending = _pending = new PendingAsks(context);
        context.Tools.Register(new AskUserTool(pending));
        context.Rpc.Register("ask.pending", (req, _) => Task.FromResult<object?>(pending.List(req.Str("sessionId"))),
            "Questions waiting for the user: { sessionId? } → { sessionId, callId, agentId, agentName, questions, askedAt }[]");
        context.Rpc.Register("ask.answer", (req, _) => Task.FromResult<object?>(pending.Answer(req)),
            "Answer a waiting question: { callId, answers?: string[][] (the options picked, per question), text? } → true");
        return Task.CompletedTask;
    }

    /// <summary>Waiting questions end as withdrawn, so their runs go on (with their slot back) instead of hanging.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _pending?.WithdrawAll();
        return Task.CompletedTask;
    }
}

internal sealed record AskOption(string Label, string? Description);

internal sealed record AskQuestion(string Question, IReadOnlyList<AskOption> Options, bool Multiple);

/// <summary>Per question the options picked (an empty list: none), and the user's own words.</summary>
internal sealed record AskAnswer(IReadOnlyList<IReadOnlyList<string>> Selected, string? Text)
{
    public static readonly AskAnswer Withdrawn = new([], null);
}

/// <summary>The questions that wait for an answer, by the tool call's id.</summary>
internal sealed class PendingAsks(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, Entry> _byCall = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public required string SessionId { get; init; }
        public required string CallId { get; init; }
        public required string AgentId { get; init; }
        public string? AgentName { get; init; }
        public required IReadOnlyList<AskQuestion> Questions { get; init; }
        public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<AskAnswer> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Entry Add(ToolContext context, IReadOnlyList<AskQuestion> questions, string? agentName)
    {
        var e = new Entry { SessionId = context.SessionId, CallId = context.CallId, AgentId = context.AgentId, AgentName = agentName, Questions = questions };
        _byCall[e.CallId] = e;
        ctx.Events.Publish("ask.asked", Json(e)); // unscoped: every window learns that a chat needs the user
        return e;
    }

    /// <summary>Stops waiting: <paramref name="status"/> is answered | steered | withdrawn | cancelled.</summary>
    public void Close(Entry e, string status, AskAnswer? answer = null)
    {
        if (!_byCall.TryRemove(new KeyValuePair<string, Entry>(e.CallId, e))) return;
        ctx.Events.Publish("ask.closed", new JsonObject
        {
            ["sessionId"] = e.SessionId,
            ["callId"] = e.CallId,
            ["status"] = status,
            ["answers"] = answer is null ? null : AnswersJson(answer),
            ["text"] = answer?.Text,
        });
    }

    public JsonArray List(string? sessionId) =>
        new([.. _byCall.Values.Where(e => sessionId is null || e.SessionId == sessionId).OrderBy(e => e.AskedAt).Select(e => (JsonNode)Json(e))]);

    public bool Answer(RpcRequest req)
    {
        var callId = req.Required("callId");
        if (!_byCall.TryGetValue(callId, out var e)) throw new RpcException("not_found", "No question waits with that id (it was answered, or its run ended).");
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
        while (selected.Count < e.Questions.Count) selected.Add([]);
        if (selected.Count > e.Questions.Count) selected.RemoveRange(e.Questions.Count, selected.Count - e.Questions.Count);
        var text = req.Str("text")?.Trim();
        if (string.IsNullOrEmpty(text)) text = null;
        if (text is null && selected.All(s => s.Count == 0)) throw new RpcException("bad_request", "An answer needs an option picked or some text.");
        var answer = new AskAnswer(selected, text);
        if (!e.Answer.TrySetResult(answer)) throw new RpcException("not_found", "The question was already answered.");
        Close(e, "answered", answer);
        return true;
    }

    public void WithdrawAll()
    {
        foreach (var e in _byCall.Values) e.Answer.TrySetResult(AskAnswer.Withdrawn);
    }

    private static JsonObject Json(Entry e) => new()
    {
        ["sessionId"] = e.SessionId,
        ["callId"] = e.CallId,
        ["agentId"] = e.AgentId,
        ["agentName"] = e.AgentName,
        ["questions"] = QuestionsJson(e.Questions),
        ["askedAt"] = e.AskedAt.ToString("O"),
    };

    internal static JsonArray QuestionsJson(IReadOnlyList<AskQuestion> questions) => new([.. questions.Select(q => (JsonNode)new JsonObject
    {
        ["question"] = q.Question,
        ["options"] = new JsonArray([.. q.Options.Select(o => (JsonNode)new JsonObject { ["label"] = o.Label, ["description"] = o.Description })]),
        ["multiple"] = q.Multiple,
    })]);

    internal static JsonArray AnswersJson(AskAnswer a) => new([.. a.Selected.Select(s => (JsonNode)new JsonArray([.. s.Select(x => (JsonNode)x)]))]);
}

internal sealed class AskUserTool(PendingAsks pending) : IAgentTool
{
    public const int MaxQuestions = 4;
    public const int MaxOptions = 8;
    private const int MaxQuestion = 1000;
    private const int MaxLabel = 120;
    private const int MaxDescription = 300;

    public ToolDefinition Definition { get; } = new()
    {
        Name = "ask_user",
        Label = "Question",
        Category = "general",
        Description =
            "Ask the user 1–4 questions and wait for the answers: for a decision or information only the user has, not to " +
            "confirm routine steps. Give the context in your message first.",
        Help =
            "For a choice between approaches, a preference, a missing detail. Not for confirming routine steps or reporting " +
            "progress. The questions appear right below your message. Offer options (label, and optionally what it means) when " +
            "the answer is one of a few choices; the user can always answer in their own words instead. multiple: the user may " +
            "pick several. Call it on its own and act on the answers once they arrive.",
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
                                        ["label"] = new JsonObject { ["type"] = "string" },
                                        ["description"] = new JsonObject { ["type"] = "string" },
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

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!TryParse(args, out var questions, out var error)) return ToolResult.Error(error);
        var runtime = context.Services.Get<IAgentRuntime>();
        var agent = runtime?.Get(context.AgentId);
        if (agent?.IsSubagent == true)
            return ToolResult.Error("Only the main agent can ask the user: nobody watches a subagent's chat. Put the question in your report instead.");

        var entry = pending.Add(context, questions, agent?.Name);
        var status = "cancelled";
        try
        {
            var answered = runtime is null
                ? await WaitAsync(entry.Answer.Task, ct).ConfigureAwait(false)
                : await runtime.WaitYieldedAsync(context.AgentId, entry.Answer.Task, "waiting for your answer", null, ct).ConfigureAwait(false);
            if (!answered)
            {
                status = "steered";
                return ToolResult.Ok("No answer: the user wrote a new message instead; it follows.", Details(questions, null, status));
            }
            var answer = await entry.Answer.Task.ConfigureAwait(false);
            if (ReferenceEquals(answer, AskAnswer.Withdrawn))
            {
                status = "withdrawn";
                return ToolResult.Ok("No answer: the question was withdrawn (the ask plugin stopped). Ask again if you still need it.",
                    Details(questions, null, status));
            }
            status = "answered";
            return ToolResult.Ok(Render(questions, answer), Details(questions, answer, status));
        }
        finally
        {
            pending.Close(entry, status); // a no-op after ask.answer closed it
        }
    }

    private static async Task<bool> WaitAsync(Task until, CancellationToken ct)
    {
        await until.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static object Details(IReadOnlyList<AskQuestion> questions, AskAnswer? answer, string status) => new JsonObject
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
        args = Unwrap(args);
        var list = args.ValueKind == JsonValueKind.Array ? args : Get(args, "questions");
        if (list is { ValueKind: JsonValueKind.String } s) list = Unwrap(s);
        var items = new List<JsonElement>();
        if (list is { ValueKind: JsonValueKind.Array } arr) items.AddRange(arr.EnumerateArray());
        else if (list is { ValueKind: JsonValueKind.Object } one) items.Add(one);
        else if (Get(args, "question") is not null) items.Add(args); // one question at the top level
        foreach (var q in items)
        {
            if (Question(q) is { } parsed) questions.Add(parsed);
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
            text = Str(q, "question", "text", "prompt", "title");
            var opts = Get(q, "options", "choices", "answers");
            if (opts is { ValueKind: JsonValueKind.String } os) opts = Unwrap(os);
            if (opts is { ValueKind: JsonValueKind.Array } oa)
            {
                foreach (var o in oa.EnumerateArray())
                {
                    var label = o.ValueKind == JsonValueKind.String ? o.GetString() : o.ValueKind == JsonValueKind.Object ? Str(o, "label", "text", "value", "title", "name") : null;
                    label = Clip(label, MaxLabel);
                    if (label is null || options.Any(x => x.Label == label)) continue;
                    var description = o.ValueKind == JsonValueKind.Object ? Clip(Str(o, "description", "detail", "details", "hint"), MaxDescription) : null;
                    options.Add(new AskOption(label, description));
                    if (options.Count == MaxOptions) break;
                }
            }
            multiple = Get(q, "multiple", "multiSelect", "multi_select", "allowMultiple") is { ValueKind: JsonValueKind.True };
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

    private static JsonElement Unwrap(JsonElement e)
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

    private static JsonElement? Get(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, params string[] names) => Get(e, names) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
