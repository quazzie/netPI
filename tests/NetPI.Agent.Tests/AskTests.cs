using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Ask;

namespace NetPI.Agent.Tests;

public static class AskTests
{
    public static void Register(TestRunner t)
    {
        t.Add("ask_user: waits with its instance given back; the option picked is the result", AnswerWithOption);
        t.Add("ask_user: several questions, the user's own words, and what ask.answer refuses", AnswerWithText);
        t.Add("ask_user: a new message from the user ends the wait; aborting cancels it", SteerAndAbort);
        t.Add("ask_user: two chats with the same tool call id each get their own answer", SameCallIdInTwoChats);
        t.Add("ask_user: subagents can't ask", SubagentRefused);
        t.Add("ask_user: lenient arguments, limits, and the answer the model reads", Parsing);
        t.Add("ask_user: an answer given while the provider's budget is spent is not lost - the slot failure lands on the next turn", AnswerSurvivesSlotFailure);
    }

    private static async Task<TestHost> StartAsync()
    {
        var h = await TestHost.StartAsync();
        await h.StartPluginAsync(new AskPlugin());
        return h;
    }

    private static object Q(string question, params string[] options) => new { question, options = options.Select(o => new { label = o }).ToArray() };

    /// <summary>The newest ask.asked event of a session, once there is one.</summary>
    private static async Task<JsonNode> AskedAsync(TestHost h, string sessionId, int count = 1)
    {
        List<JsonNode> asked() => [.. h.Bus.OfType("ask.asked").Select(FakeBus.Data).Where(d => (string?)d["sessionId"] == sessionId)];
        await Wait.Until(() => asked().Count >= count, "a question waits");
        return asked()[count - 1];
    }

    private static ToolResultPart Result(TestHost h, string sessionId) =>
        h.Messages(sessionId).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Last(r => r.Name == "ask_user");

    private static async Task AnswerWithOption()
    {
        await using var h = await StartAsync();
        var s = h.NewSession(model: "fake/solo"); // 1 slot
        var other = h.NewSession(model: "fake/solo");
        h.Catalog.Handler = (r, ct) =>
            r.SessionId == other.Id ? Reply.Text("other chat ran")
            : Reply.HasToolResult(r) ? Reply.Text("Going with it.")
            : Reply.Tool("ask_user", new { questions = new[] { Q("Which parser?", "Rewrite", "Patch") } });
        await h.SendAsync(s.Id, "fix the parser");
        var asked = await AskedAsync(h, s.Id);
        var callId = (string)asked["callId"]!;
        var id = (string)asked["id"]!;
        Check.True(id != callId, "the question has an id of its own, not the model's call id");
        Check.Equal("Which parser?", (string?)asked["questions"]![0]!["question"]);
        Check.Equal("Patch", (string?)asked["questions"]![0]!["options"]![1]!["label"]);

        // the question is out a moment before the runtime shows the agent as yielded
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "the run waits with its slot given back");
        Check.Equal("waiting for your answer", h.Runtime.GetBySession(s.Id)!.Activity);
        await h.SendAsync(other.Id, "meanwhile");
        await h.IdleAsync(other.Id);
        Check.Equal("other chat ran", h.Messages(other.Id)[^1].Text, "another chat took the only slot meanwhile");

        var pending = (await h.Rpc.CallAsync("ask.pending", new { sessionId = s.Id }))!.AsArray();
        Check.Equal(callId, (string?)pending.Single()!["callId"]);
        Check.Equal(id, (string?)pending.Single()!["id"]);
        Check.Equal(true, await h.Rpc.InvokeAsync("ask.answer", new { id, answers = new[] { new[] { "Patch" } } }));
        await h.IdleAsync(s.Id);

        var result = Result(h, s.Id);
        Check.Equal("The user answered: Patch", result.Content);
        Check.False(result.IsError);
        Check.Equal("answered", (string?)result.Details!["status"]);
        Check.Equal("Patch", (string?)result.Details!["answers"]![0]![0]);
        Check.Equal("Going with it.", h.Messages(s.Id)[^1].Text);
        var closed = FakeBus.Data(h.Bus.OfType("ask.closed").Single());
        Check.Equal("answered", (string?)closed["status"]);
        Check.Equal("Patch", (string?)closed["answers"]![0]![0]);
        Check.Equal(0, (await h.Rpc.CallAsync("ask.pending", new { sessionId = s.Id }))!.AsArray().Count, "nothing waits any more");
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "the slot is free again");
    }

    private static async Task AnswerWithText()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        var asked = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (Reply.HasToolResult(r)) return Reply.Text("ok");
            asked++;
            return asked == 1
                ? Reply.Tool("ask_user", new { questions = new object[] { Q("Keep the old API?", "Yes", "No"), new { question = "Which tests?", options = new[] { "unit", "e2e", "ui" }, multiple = true } } })
                : Reply.Tool("ask_user", new { questions = new[] { Q("And a second question?", "yes", "no") } });
        };
        await h.SendAsync(s.Id, "go");
        var first = await AskedAsync(h, s.Id);
        var id = (string)first["id"]!;
        Check.True(id != (string)first["callId"], "the question has an id of its own");
        Check.Equal(true, (bool?)(await AskedAsync(h, s.Id))["questions"]![1]!["multiple"], "options as plain strings, several may be picked");

        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("ask.answer", new { id, text = "  " }), "an empty answer")).Code);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("ask.answer", new { id = "ask_nope", text = "hi" }), "an unknown question")).Code);
        await h.Rpc.InvokeAsync("ask.answer", new { id, answers = new[] { Array.Empty<string>(), new[] { "unit", "e2e" } }, text = "keep it for one release" });
        await h.IdleAsync(s.Id);
        Check.Equal(
            "The user answered:\n1. Keep the old API?\n   → (nothing picked)\n2. Which tests?\n   → unit, e2e\nThey added: keep it for one release",
            Result(h, s.Id).Content);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("ask.answer", new { id, text = "again" }), "answered already")).Code);
        // An older caller that only knows the tool call's id still reaches the question, as long as it says which chat.
        await h.SendAsync(s.Id, "one more");
        var second = await AskedAsync(h, s.Id, 2);
        Check.Equal(true, await h.Rpc.InvokeAsync("ask.answer", new { callId = (string)second["callId"]!, sessionId = s.Id, text = "by call id" }));
        await h.IdleAsync(s.Id);
        Check.Equal("The user answered in their own words: by call id", Result(h, s.Id).Content);
    }

    private static async Task SteerAndAbort()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
            Reply.LastUser(r) == "never mind, do X" ? Reply.Text("doing X")
            : Reply.HasToolResult(r) ? Reply.Text("done")
            : Reply.Tool("ask_user", new { questions = new[] { Q("A or B?", "A", "B") } });

        // the user writes instead of answering: the wait ends, and the message follows the result
        await h.SendAsync(s.Id, "start");
        await AskedAsync(h, s.Id);
        await h.SendAsync(s.Id, "never mind, do X");
        await h.IdleAsync(s.Id);
        var result = Result(h, s.Id);
        Check.Equal("No answer: the user wrote a new message instead; it follows.", result.Content);
        Check.Equal("steered", (string?)result.Details!["status"]);
        var msgs = h.Messages(s.Id);
        var at = msgs.FindIndex(m => m.ToolResults.Any(r => r.Name == "ask_user"));
        Check.True(msgs.Skip(at).Any(m => m.Role == MessageRole.User && m.Text == "never mind, do X"), "the message follows");
        Check.Equal("steered", (string?)FakeBus.Data(h.Bus.OfType("ask.closed").Last())["status"]);

        // aborting the run cancels the question
        await h.SendAsync(s.Id, "again");
        var callId = (string)(await AskedAsync(h, s.Id, 2))["callId"]!;
        await h.Runtime.AbortAsync(s.Id);
        await h.IdleAsync(s.Id);
        Check.Equal("cancelled", (string?)FakeBus.Data(h.Bus.OfType("ask.closed").Last())["status"]);
        Check.Equal(0, (await h.Rpc.CallAsync("ask.pending", new { }))!.AsArray().Count);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("ask.answer", new { callId, text = "late" }), "the question is gone")).Code);
    }

    /// <summary>
    /// A tool call id belongs to the model that made it, and two chats can hold the same one: an answer meant for the
    /// first chat must not end up answering the second.
    /// </summary>
    private static async Task SameCallIdInTwoChats()
    {
        await using var h = await StartAsync();
        var a = h.NewSession(model: "fake/solo"); // one slot: the second chat starts once the first has yielded
        var b = h.NewSession(model: "fake/solo");
        h.Catalog.Handler = (r, ct) =>
        {
            // a tool result, or the message the user wrote instead of answering, ends this chat's part
            if (Reply.HasToolResult(r) || Reply.LastUser(r) == "never mind") return Reply.Text("done");
            var question = r.SessionId == a.Id ? "Which one, in chat A?" : "Which one, in chat B?";
            var pick = r.SessionId == a.Id ? "A" : "B";
            return Reply.Stream(Reply.Message(Reply.Call("ask_user", new { questions = new[] { Q(question, pick, "other") } }, "call_same")));
        };
        await h.SendAsync(a.Id, "ask in A");
        await AskedAsync(h, a.Id);
        await Wait.Until(() => h.Runtime.GetBySession(a.Id)?.Status == AgentStatus.Yielded, "chat A gave its slot back");
        await h.SendAsync(b.Id, "ask in B");
        var inA = await AskedAsync(h, a.Id);
        var inB = await AskedAsync(h, b.Id);
        await Wait.Until(() => h.Runtime.GetBySession(b.Id)?.Status == AgentStatus.Yielded, "chat B gave its slot back");

        Check.Equal("call_same", (string?)inA["callId"]);
        Check.Equal("call_same", (string?)inB["callId"], "both models used the same call id");
        var idA = (string)inA["id"]!;
        var idB = (string)inB["id"]!;
        Check.True(idA != idB, "two questions, two ids");
        Check.Equal(2, (await h.Rpc.CallAsync("ask.pending", new { }))!.AsArray().Count, "both wait");

        // answering the second one leaves the first waiting
        Check.Equal(true, await h.Rpc.InvokeAsync("ask.answer", new { id = idB, answers = new[] { new[] { "B" } } }));
        await h.IdleAsync(b.Id);
        Check.Equal("The user answered: B", Result(h, b.Id).Content);
        Check.Equal(1, (await h.Rpc.CallAsync("ask.pending", new { }))!.AsArray().Count, "chat A's question still waits");
        Check.Equal("not_found",
            (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("ask.answer", new { id = idB, text = "again" }), "chat B's question is gone")).Code);

        // a message in chat A ends that question only
        await h.SendAsync(a.Id, "never mind");
        await h.IdleAsync(a.Id);
        Check.Equal("steered", (string?)Result(h, a.Id).Details!["status"]);
        Check.Equal(0, (await h.Rpc.CallAsync("ask.pending", new { }))!.AsArray().Count, "nothing waits any more");
        Check.Equal("No answer: the user wrote a new message instead; it follows.", Result(h, a.Id).Content);
        var closed = h.Bus.OfType("ask.closed").Select(FakeBus.Data).ToList();
        Check.Equal(2, closed.Count, "each question closed on its own");
        Check.Equal(string.Join(",", new[] { idA, idB }.Order()), string.Join(",", closed.Select(c => (string?)c["id"]!).Order()), "both questions, each with its own id");
    }

    /// <summary>
    /// A question out, then the provider's daily budget is spent while the user is typing: the slot the wait gave back
    /// cannot be re-acquired. Re-acquiring is bookkeeping, so its failure must not throw away the answer: the tool
    /// result keeps it, the card closes as answered, and the next turn's slot check reports the real failure (idea-633b6n).
    /// </summary>
    private static async Task AnswerSurvivesSlotFailure()
    {
        await using var h = await StartAsync();
        var s = h.NewSession(model: "fake/solo");
        h.Catalog.Handler = (r, ct) =>
            r.Messages.SelectMany(m => m.ToolResults).Any(t => (t.Content ?? "").Contains("The user answered: A"))
                ? Reply.Text("done")
                : Reply.Tool("ask_user", new { questions = new[] { Q("Pick one?", "A", "B") } });
        await h.SendAsync(s.Id, "pick");
        var asked = await AskedAsync(h, s.Id);
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "the run waits with its slot given back");
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "the instance is free meanwhile");

        // the provider's daily budget is spent while the user is typing (turn 1 already used tokens)
        h.Settings.Set("budget.providers", new JsonObject { ["fake"] = new JsonObject { ["dailyTokens"] = 1 } });
        await h.Bus.DrainAsync();

        var id = (string)asked["id"]!;
        Check.Equal(true, await h.Rpc.InvokeAsync("ask.answer", new { id, answers = new[] { new[] { "A" } } }));
        await h.IdleAsync(s.Id);

        var result = Result(h, s.Id);
        Check.False(result.IsError, "the answer is not lost to a slot failure");
        Check.Equal("The user answered: A", result.Content);
        Check.Equal("answered", (string?)result.Details!["status"]);
        Check.Equal("answered", (string?)FakeBus.Data(h.Bus.OfType("ask.closed").Single())["status"], "the card closes as answered, not cancelled");
        Check.Equal(1, h.Catalog.Calls, "the next turn's slot check failed before a model call");
        Check.Contains(h.Runtime.GetBySession(s.Id)!.Error ?? "", "daily token budget", "the real failure is reported by the next turn");
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "the slot is free");

        // the budget is lifted: the answer is in the transcript, and it is what the model reads
        h.Settings.Set("budget.providers", null);
        await h.Bus.DrainAsync();
        await h.SendAsync(s.Id, "now what?");
        await h.IdleAsync(s.Id);
        Check.Equal(2, h.Catalog.Calls);
        Check.True(h.Catalog.Requests.Last().Messages.SelectMany(m => m.ToolResults).Any(t => (t.Content ?? "").Contains("The user answered: A")),
            "the model reads the answer given while it was offline");
        Check.Equal("done", h.Messages(s.Id)[^1].Text);
    }

    private static async Task SubagentRefused()
    {
        await using var h = await StartAsync();
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.SessionId != parent.Id)
                return Reply.HasToolResult(r) ? Reply.Text("REPORT: could not ask") : Reply.Tool("ask_user", new { questions = new[] { Q("Which?", "a", "b") } });
            return Reply.HasToolResult(r) ? Reply.Text("parent done") : Reply.Tool("agent_spawn", new { task = "Decide something.", name = "worker" });
        };
        await h.SendAsync(parent.Id, "delegate");
        var p = await h.IdleAsync(parent.Id, 15_000);
        var child = h.Runtime.Get(p.Children.Single())!;
        await h.IdleAsync(child.SessionId);
        var result = Result(h, child.SessionId);
        Check.True(result.IsError);
        Check.Contains(result.Content, "Only the main agent can ask the user");
        Check.Equal(0, h.Bus.OfType("ask.asked").Count, "nothing was asked");
    }

    private static Task Parsing()
    {
        static List<AskQuestion> Parse(string json, out string error)
        {
            using var doc = JsonDocument.Parse(json);
            AskUserTool.TryParse(doc.RootElement, out var qs, out error);
            return qs;
        }

        var one = Parse("""{ "question": "Tabs or spaces?", "options": ["tabs", "spaces", "tabs"] }""", out _);
        Check.Equal(1, one.Count, "one question at the top level");
        Check.Equal("tabs,spaces", string.Join(",", one[0].Options.Select(o => o.Label)), "options as strings, duplicates dropped");

        var wrapped = Parse("""{ "questions": "[{\"text\": \"Deploy now?\", \"choices\": [{\"value\": \"yes\", \"detail\": \"to prod\"}], \"multiSelect\": true}]" }""", out _);
        Check.Equal("Deploy now?", wrapped.Single().Question, "questions sent as a JSON string, text / choices / value / detail");
        Check.Equal("to prod", wrapped[0].Options.Single().Description);
        Check.False(wrapped[0].Multiple, "several picks need several options");

        var plain = Parse("""{ "questions": ["Why?", { "question": "  " }] }""", out _);
        Check.Equal("Why?", plain.Single().Question, "a plain string is a question without options; an empty one is skipped");

        Parse("""{ "questions": [] }""", out var error);
        Check.Contains(error, "ask_user needs \"questions\"");
        Parse("""{ "questions": ["1", "2", "3", "4", "5"] }""", out error);
        Check.Contains(error, "at most 4 questions");

        var many = Parse("""{ "question": "Pick", "options": ["a","b","c","d","e","f","g","h","i","j"] }""", out _);
        Check.Equal(AskUserTool.MaxOptions, many[0].Options.Count, "options are capped");

        var qs = new List<AskQuestion> { new("Which?", [new("A", null)], false) };
        Check.Equal("The user answered in their own words: whatever works", AskUserTool.Render(qs, new AskAnswer([[]], "whatever works")));
        Check.Equal("The user answered: A\nThey added: and hurry", AskUserTool.Render(qs, new AskAnswer([["A"]], "and hurry")));
        var two = new List<AskQuestion> { new("One?", [], false), new("Two?", [], false) };
        Check.Equal("The user answered:\n1. One?\n   → (nothing picked)\n2. Two?\n   → (nothing picked)\nIn their own words: both fine",
            AskUserTool.Render(two, new AskAnswer([[], []], "both fine")));
        return Task.CompletedTask;
    }
}
