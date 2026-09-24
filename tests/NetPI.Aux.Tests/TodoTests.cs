using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Todo;

namespace NetPI.Aux.Tests;

public static class TodoTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("todo-home"));
        public SessionInfo Session { get; }

        public Env() => Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s" });

        public async Task StartAsync() => await new TodoPlugin().StartAsync(Ctx, CancellationToken.None);

        public Task<ToolResult> Run(JsonElement args, string? sessionId = null) =>
            Ctx.ToolsFake.Get("todo_write")!.ExecuteAsync(new ToolContext
            {
                SessionId = sessionId ?? Session.Id, AgentId = "agt_1", CallId = "call_1", Cwd = Path.GetTempPath(),
                Services = Ctx.Services, Events = Ctx.Events,
            }, args, CancellationToken.None);

        public JsonArray? Stored => Ctx.SessionsFake.GetSession(Session.Id)?.Meta?["todo"] as JsonArray;
    }

    private static string Statuses(JsonArray? list) => string.Join(",", (list ?? []).Select(i => (string?)i?["status"]));

    public static void Register(TestRunner r)
    {
        r.Add("todo: todo_write stores the list in the session meta and echoes it", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var tool = env.Ctx.ToolsFake.Get("todo_write")!;
            Check.True(tool.Definition is { Label: "Todo", Category: "todo", ReadOnly: false });
            Check.Contains(string.Join(" ", tool.Definition.PromptGuidelines!), "todo_write");

            var res = await env.Run(T.Args(new
            {
                items = new object[]
                {
                    new { text = "Read the parser", status = "done" },
                    new { text = "Fix the bug", status = "in_progress" },
                    new { text = "Run the suites", status = "pending" },
                },
            }));
            Check.False(res.IsError, res.Content);
            Check.Equal("Todo list updated (1/3 done):\n[x] Read the parser\n[>] Fix the bug\n[ ] Run the suites", res.Content);
            Check.Equal("done,in_progress,pending", Statuses(env.Stored));
            Check.Equal("Fix the bug", (string?)env.Stored![1]!["text"]);
            var d = NetPiJson.ToElement(res.Details);
            Check.Equal(1, d.GetProperty("done").GetInt32());
            Check.Equal(3, d.GetProperty("total").GetInt32());

            // the next call replaces the list
            await env.Run(T.Args(new { items = new[] { new { text = "Only one", status = "done" } } }));
            Check.Equal("done", Statuses(env.Stored));
            env.Ctx.Unload();
        });

        r.Add("todo: lenient input (strings, synonyms, other field names, a JSON string, a bare array)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.Run(T.Args("""{ "todos": ["plain string", { "content": "c", "status": "completed" }, { "title": "t", "state": "active" }, { "task": "k", "done": true }] }"""));
            Check.Equal("pending,done,in_progress,done", Statuses(env.Stored));
            Check.Equal("plain string,c,t,k", string.Join(",", env.Stored!.Select(i => (string?)i?["text"])));

            await env.Run(JsonSerializer.SerializeToElement("""{"items":[{"text":"from a string","status":"in-progress"}]}"""));
            Check.Equal("in_progress", Statuses(env.Stored));

            await env.Run(T.Args("""[{"text":"bare","status":"todo"}]"""));
            Check.Equal("pending", Statuses(env.Stored));

            var many = Enumerable.Range(1, 60).Select(i => new { text = $"step {i}", status = "pending" }).ToArray();
            await env.Run(T.Args(new { items = many }));
            Check.Equal(50, env.Stored!.Count);
            env.Ctx.Unload();
        });

        r.Add("todo: after compaction removed the last todo_write, a notice brings the open list back once", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var hook = env.Ctx.ServicesFake.GetAll<IAgentHook>().OfType<IAgentHook>().Single(h => h.Order == 530);
            await env.Run(T.Args(new { items = new object[] { new { text = "Parse", status = "done" }, new { text = "Fix", status = "in_progress" }, new { text = "Test", status = "pending" } } }));
            var run = T.Run(env.Ctx, T.Model(), env.Session);
            var reloads = 0;
            AgentTurnContext Turn(List<ChatMessage> messages) => T.Turn(run, messages, reload: () => { reloads++; return Task.CompletedTask; });

            // the last todo_write is still in the context: nothing to add
            var withCall = new List<ChatMessage> { T.User("go"), T.Assistant("", T.Call("c1", "todo_write")), T.ToolResult(("c1", "todo_write", "Todo list updated")) };
            await hook.OnBeforeModelCallAsync(Turn(withCall));
            Check.Equal(0, env.Ctx.SessionsFake.Appended.Count);

            // compacted away: the open list comes back as a notice
            await hook.OnBeforeModelCallAsync(Turn([T.User("summary of earlier work"), T.User("continue")]));
            var notice = env.Ctx.SessionsFake.Appended.Single();
            Check.Equal("todo", notice.MetaString("kind"));
            Check.Contains(notice.Text, "Your todo list (its earlier updates were compacted away)");
            Check.Contains(notice.Text, "[x] Parse\n[>] Fix\n[ ] Test");
            Check.Equal(1, reloads);

            // once in the context, not again
            await hook.OnBeforeModelCallAsync(Turn([T.User("continue"), notice]));
            Check.Equal(1, env.Ctx.SessionsFake.Appended.Count);

            // a finished list is not brought back
            await env.Run(T.Args(new { items = new[] { new { text = "All", status = "done" } } }));
            await hook.OnBeforeModelCallAsync(Turn([T.User("next task")]));
            Check.Equal(1, env.Ctx.SessionsFake.Appended.Count);
            env.Ctx.Unload();
        });

        r.Add("todo: an empty list clears it; bad input and unknown sessions are errors", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.Run(T.Args(new { items = new[] { new { text = "a", status = "pending" } } }));
            var cleared = await env.Run(T.Args(new { items = Array.Empty<object>() }));
            Check.Equal("Todo list cleared.", cleared.Content);
            Check.Equal(0, env.Stored!.Count);

            var bad = await env.Run(T.Args(new { list = 5 }));
            Check.True(bad.IsError);
            Check.Contains(bad.Content, "\"items\"");

            var unknown = await env.Run(T.Args(new { items = new[] { "x" } }), sessionId: "ses_missing");
            Check.True(unknown.IsError);
            env.Ctx.Unload();
        });
    }
}
