extern alias claudecli;

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI;
using CL = claudecli::NetPI.Providers.ClaudeCli;

namespace NetPI.Providers.Tests;

/// <summary>A chat keeps one CLI session: later turns resume it and send only what is new.</summary>
internal static class ClaudeCliResumeTests
{
    private const string Init = """{"type":"system","subtype":"init","apiKeySource":"none"}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"ok","usage":{"input_tokens":1,"output_tokens":1}}""";

    private static async IAsyncEnumerable<string> Lines(IEnumerable<string> lines, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var l in lines) { ct.ThrowIfCancellationRequested(); yield return l; await Task.Yield(); }
    }

    private static ChatMessage Msg(long id, MessageRole role, string text, string? provider = null) => new()
    {
        Id = id, Seq = id, Role = role, Parts = [new TextPart { Text = text }], Provider = provider,
    };

    private static ModelRequest Req(IReadOnlyList<ChatMessage> messages, string purpose = "agent") => new()
    {
        Model = new ModelInfo { Provider = "claude-cli", Id = "claude-sonnet-5-5" },
        SystemPrompt = "You are a planner.",
        Messages = messages,
        SessionId = "ses_1",
        Purpose = purpose,
    };

    private static async Task<List<ModelStreamEvent>> Collect(IModelProvider p, ModelRequest r)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in p.StreamAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    private sealed record Call(List<string> Args, string Stdin)
    {
        public string? Arg(string name) => Args.Contains(name) ? Args[Args.IndexOf(name) + 1] : null;
    }

    private static (CL.ClaudeCliProvider Provider, List<Call> Calls, CL.MemoryCliSessions Store) Make(Func<int, IEnumerable<string>>? script = null)
    {
        var calls = new List<Call>();
        var store = new CL.MemoryCliSessions();
        var p = new CL.ClaudeCliProvider(() => new JsonObject { ["enabled"] = true }, null, (call, ct) =>
        {
            calls.Add(new Call(call.Args.ToList(), call.Stdin));
            return Lines(script?.Invoke(calls.Count) ?? [Init, Result], ct);
        }, sessions: store);
        return (p, calls, store);
    }

    public static async Task RunAsync(TestRunner t)
    {
        await t.Run("claude-cli resume: the first turn starts the chat's CLI session, the next resumes it and sends only the new message", async () =>
        {
            var (p, calls, store) = Make();
            var first = new List<ChatMessage> { Msg(1, MessageRole.User, "Read the repo and plan X.") };
            await Collect(p, Req(first));
            t.Check(calls[0].Args.Contains("--session-id") && !calls[0].Args.Contains("--resume") && !calls[0].Args.Contains("--no-session-persistence"), "turn 1 starts a session");
            t.Eq("Read the repo and plan X.", calls[0].Stdin, "turn 1 sends the prompt");
            var cliId = calls[0].Arg("--session-id")!;
            t.Check(Guid.TryParse(cliId, out _), "the CLI session id is a guid");
            t.Eq(cliId, store.Get("ses_1")?.CliId, "remembered for the chat");
            t.Eq(1L, store.Get("ses_1")?.LastSeenId, "up to the newest message it was given");

            // what NetPI stored after turn 1: the CLI's answer (with a tool it ran itself, and that tool's result), then the user's next message
            var ran = new ToolCallPart { Id = "tu_1", Name = "Read", Arguments = "{}", ProviderData = new JsonObject { [ToolCallPart.ExecutedKey] = true } };
            var answer = new ChatMessage { Id = 2, Seq = 2, Role = MessageRole.Assistant, Provider = "claude-cli", Parts = [ran, new TextPart { Text = "The plan is X." }] };
            var toolMsg = new ChatMessage { Id = 3, Seq = 3, Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "tu_1", Name = "Read", Content = "HUGE FILE" }] };
            var second = new List<ChatMessage>(first) { answer, toolMsg, Msg(4, MessageRole.User, "Now do step 2.") };
            await Collect(p, Req(second));
            t.Eq(cliId, calls[1].Arg("--resume"), "turn 2 resumes the same session");
            t.Check(!calls[1].Args.Contains("--session-id"), "and does not start another");
            t.Eq("Now do step 2.", calls[1].Stdin, "only the new message is sent: not the history, not the CLI's own answer, not its tools' results");
            t.Eq(4L, store.Get("ses_1")?.LastSeenId, "moved on");
        });

        await t.Run("claude-cli resume: another model's answer in between is passed on, a summary is not, and nothing new says to carry on", async () =>
        {
            var (p, calls, store) = Make();
            store.Put("ses_1", new CL.CliSession("11111111-1111-1111-1111-111111111111", 2));
            var msgs = new List<ChatMessage>
            {
                Msg(1, MessageRole.User, "old"), Msg(2, MessageRole.Assistant, "old answer", "claude-cli"),
                Msg(3, MessageRole.Assistant, "I fixed it differently", "openrouter"),
                Msg(4, MessageRole.Summary, "a compaction summary of everything"),
                Msg(5, MessageRole.Notice, "Plan mode is on."),
                Msg(6, MessageRole.User, "Continue from there."),
            };
            await Collect(p, Req(msgs));
            t.Eq("[An earlier answer, from another model]\nI fixed it differently\n\nPlan mode is on.\n\nContinue from there.", calls[0].Stdin, "labelled, in order, without the summary");

            // a retry: the CLI has everything already
            store.Put("ses_1", new CL.CliSession("11111111-1111-1111-1111-111111111111", 6));
            await Collect(p, Req(msgs));
            t.Eq("Continue.", calls[1].Stdin, "nothing new");
            t.Eq("11111111-1111-1111-1111-111111111111", calls[1].Arg("--resume"), "still the same session");
        });

        await t.Run("claude-cli resume: a chat whose history was cut back starts a new CLI session with the whole conversation", async () =>
        {
            var (p, calls, store) = Make();
            store.Put("ses_1", new CL.CliSession("22222222-2222-2222-2222-222222222222", 9));
            await Collect(p, Req([Msg(1, MessageRole.User, "first"), Msg(2, MessageRole.Assistant, "answer", "claude-cli"), Msg(3, MessageRole.User, "again")]));
            t.Check(calls[0].Args.Contains("--session-id") && calls[0].Arg("--session-id") != "22222222-2222-2222-2222-222222222222", "a new session");
            t.Check(calls[0].Stdin.Contains("first") && calls[0].Stdin.Contains("again"), "with the conversation so far");
            t.Eq(3L, store.Get("ses_1")?.LastSeenId, "and remembered");
        });

        await t.Run("claude-cli resume: a CLI session that is gone is replaced, once, with the conversation, and the chat is told", async () =>
        {
            var gone = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"","errors":["No conversation found with session ID: 33333333-3333-3333-3333-333333333333"]}""";
            var (p, calls, store) = Make(n => n == 1 ? [gone] : [Init, Result]);
            store.Put("ses_1", new CL.CliSession("33333333-3333-3333-3333-333333333333", 1));
            var events = await Collect(p, Req([Msg(1, MessageRole.User, "first"), Msg(2, MessageRole.Assistant, "answer", "claude-cli"), Msg(3, MessageRole.User, "second")]));
            t.Eq(2, calls.Count, "tried the resume, then started over");
            t.Eq("33333333-3333-3333-3333-333333333333", calls[0].Arg("--resume"), "first the resume");
            t.Check(calls[1].Args.Contains("--session-id") && calls[1].Stdin.Contains("first") && calls[1].Stdin.Contains("second"), "then a new session with the whole conversation");
            t.Check(events.OfType<StreamNotice>().Any(n => n.Text.Contains("is gone")), "the chat is told");
            t.Check(events.OfType<StreamCompleted>().Count() == 1, "one answer");
            t.Eq(calls[1].Arg("--session-id"), store.Get("ses_1")?.CliId, "the new session is the chat's now");
        });

        await t.Run("claude-cli resume: a side call leaves no session, and a call that never started the CLI is not remembered", async () =>
        {
            var (p, calls, store) = Make();
            await Collect(p, Req([Msg(1, MessageRole.User, "title me")], purpose: "title"));
            t.Check(calls[0].Args.Contains("--no-session-persistence") && !calls[0].Args.Contains("--session-id"), "a title call has no session");
            t.Check(store.Get("ses_1") is null, "and does not touch the chat's");

            var (q, _, store2) = Make(_ => ["""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in"}"""]);
            try { await Collect(q, Req([Msg(1, MessageRole.User, "go")])); } catch (ModelException) { /* expected */ }
            t.Check(store2.Get("ses_1") is null, "no init line, nothing remembered: the next turn sends everything again");
        });

        await t.Run("claude-cli resume: the stored pointers survive in the plugin's data and old ones are pruned", () =>
        {
            using var ctx = new FakePluginContext(new JsonObject());
            var col = ctx.Data.Collection("cli_sessions", new CollectionSpec().Integer("updatedAt"));
            var store = new CL.DataCliSessions(col);
            store.Put("a", new CL.CliSession("cli-a", 7));
            t.Eq("cli-a", store.Get("a")?.CliId, "stored");
            t.Eq(7L, store.Get("a")?.LastSeenId, "with its point");
            col.Put("old", new JsonObject { ["cliId"] = "cli-old", ["lastSeenId"] = 1, ["updatedAt"] = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds() });
            store.Prune();
            t.Check(store.Get("old") is null && store.Get("a") is not null, "a session nobody continued for two months is forgotten, a recent one stays");
            store.Remove("a");
            t.Check(store.Get("a") is null, "removed");
            return Task.CompletedTask;
        });

        await t.Run("claude-cli resume: live: the second turn remembers the first (the real CLI, set NETPI_CLAUDE_CLI_LIVE=1)", async () =>
        {
            if (Environment.GetEnvironmentVariable("NETPI_CLAUDE_CLI_LIVE") != "1") t.Skip("NETPI_CLAUDE_CLI_LIVE is not 1");
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new CL.MemoryCliSessions();
                var p = new CL.ClaudeCliProvider(() => new JsonObject { ["enabled"] = true, ["executable"] = File.Exists(exe) ? exe : "claude", ["tools"] = "none" },
                    null, _ => dir, null, null, store);
                var m1 = new List<ChatMessage> { Msg(1, MessageRole.User, "Remember this code word: TANGERINE-42. Reply with just: stored.") };
                var r1 = (await Collect(p, Req(m1))).OfType<StreamCompleted>().Single().Message;
                var m2 = new List<ChatMessage>(m1)
                {
                    new() { Id = 2, Seq = 2, Role = MessageRole.Assistant, Provider = "claude-cli", Parts = [new TextPart { Text = r1.Text }] },
                    Msg(3, MessageRole.User, "What was the code word? Reply with just the word."),
                };
                var r2 = (await Collect(p, Req(m2))).OfType<StreamCompleted>().Single().Message;
                t.Check(r2.Text.Contains("TANGERINE", StringComparison.OrdinalIgnoreCase), $"the second turn remembers: {r2.Text}");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
        });
    }
}
