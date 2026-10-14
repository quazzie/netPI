extern alias claudecli;

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NetPI;
using CL = claudecli::NetPI.Providers.ClaudeCli;

namespace NetPI.Providers.Tests;

/// <summary>The Claude CLI provider against canned stream-json lines (the real CLI needs a subscription login).</summary>
internal static class ClaudeCliTests
{
    private const string Init = """{"type":"system","subtype":"init","apiKeySource":"none","model":"claude-sonnet-5-5","tools":[]}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"Hello world","total_cost_usd":0.42,"usage":{"input_tokens":12,"output_tokens":5,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}""";

    private static string Ev(string json) => $$"""{"type":"stream_event","event":{{json}}}""";

    private static readonly string[] Streamed =
    [
        Init,
        Ev("""{"type":"message_start","message":{"usage":{"input_tokens":12,"cache_read_input_tokens":3,"cache_creation_input_tokens":2,"output_tokens":1}}}"""),
        Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""),
        Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan it."}}"""),
        Ev("""{"type":"content_block_stop","index":0}"""),
        Ev("""{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
        Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello "}}"""),
        Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"world"}}"""),
        Ev("""{"type":"content_block_stop","index":1}"""),
        Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}"""),
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello world"}]}}""",
        Result,
    ];

    private static async IAsyncEnumerable<string> Lines(IEnumerable<string> lines, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var l in lines) { ct.ThrowIfCancellationRequested(); yield return l; await Task.Yield(); }
    }

    private static ModelRequest Req(string model = "claude-sonnet-5-5", IReadOnlyList<ChatMessage>? messages = null) => new()
    {
        Model = new ModelInfo { Provider = "claude-cli", Id = model },
        SystemPrompt = "You are a planner.",
        Messages = messages ?? [ChatMessage.UserText("Plan a thing.")],
        SessionId = "ses_1",
    };

    private static JsonObject Cfg(bool enabled = true, string? exe = null) => new() { ["enabled"] = enabled, ["executable"] = exe ?? "claude" };

    private static async Task<List<ModelStreamEvent>> Collect(IModelProvider p, ModelRequest r)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in p.StreamAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    private static async Task<ModelException?> Fails(IModelProvider p, ModelRequest r)
    {
        try { await Collect(p, r); return null; }
        catch (ModelException ex) { return ex; }
    }

    public static async Task RunAsync(TestRunner t)
    {
        await t.Run("claude-cli: partial messages stream as text and thinking, usage from the CLI, no API cost reported", async () =>
        {
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(Streamed, ct));
            var events = await Collect(p, Req());
            t.Eq("Hello world", string.Concat(events.OfType<TextDelta>().Select(e => e.Text)), "streamed text");
            t.Eq("Plan it.", string.Concat(events.OfType<ThinkingDelta>().Select(e => e.Text)), "streamed thinking");
            var done = events.OfType<StreamCompleted>().Single().Message;
            t.Eq("Hello world", done.Text, "final text");
            t.Eq("claude-cli", done.Provider, "provider");
            t.Eq("stop", done.StopReason, "stop reason");
            t.Eq(12L, done.Usage!.InputTokens, "input tokens");
            t.Eq(5L, done.Usage!.OutputTokens, "output tokens");
            t.Eq(3L, done.Usage!.CacheReadTokens, "cache read");
            t.Eq(2L, done.Usage!.CacheWriteTokens, "cache write");
            t.Check(done.Usage!.CostUsd is null, "total_cost_usd is the API price, not a subscription cost: left unreported");
        });

        await t.Run("claude-cli: without partial events the assistant message, or the result text, is the answer", async () =>
        {
            var whole = new[] { Init, """{"type":"assistant","message":{"content":[{"type":"text","text":"From assistant"}]}}""", Result };
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(whole, ct));
            t.Eq("From assistant", (await Collect(p, Req())).OfType<StreamCompleted>().Single().Message.Text, "assistant message");
            var onlyResult = new[] { Init, Result };
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(onlyResult, ct));
            t.Eq("Hello world", (await Collect(p, Req())).OfType<StreamCompleted>().Single().Message.Text, "result text");
        });

        await t.Run("claude-cli: a login error is a clear non-transient failure; a 5xx is transient", async () =>
        {
            var notLoggedIn = new[]
            {
                Init,
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed"}""",
                """{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","terminal_reason":"api_error"}""",
            };
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(notLoggedIn, ct));
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false } && e.Message.Contains("claude auth login") && e.Message.Contains("Not logged in"), $"login error: {e?.Message}");
            t.Eq("authentication_failed", e?.ErrorType, "error type");

            var overloaded = new[] { Init, """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Overloaded","api_error_status":529}""" };
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(overloaded, ct));
            e = await Fails(p, Req());
            t.Check(e is { Transient: true, StatusCode: 529 }, "529 is transient");

            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([Init], ct));
            e = await Fails(p, Req());
            t.Check(e is { Transient: true, ErrorType: "cli_incomplete" }, "a run without a result line is reported, not an empty answer");
        });

        await t.Run("claude-cli: a user's pictures go to the CLI as stream-json image blocks", async () =>
        {
            SeenCall? seen = null;
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) => { seen = new SeenCall(call.Args.ToList(), call.Stdin, false); return Lines([Init, Result], ct); });
            var msg = new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "What is this?" }, new ImagePart { MediaType = "image/png", Data = "QUJD" }] };
            await Collect(p, Req(messages: [msg]));
            t.Check(seen!.Args.Contains("--input-format") && seen.Args[seen.Args.IndexOf("--input-format") + 1] == "stream-json", "structured input is on");
            var block = System.Text.Json.Nodes.JsonNode.Parse(seen.Stdin)!["message"]!["content"]!.AsArray();
            t.Eq("What is this?", block[0]!["text"]!.GetValue<string>(), "the text block");
            t.Eq("QUJD", block[1]!["source"]!["data"]!.GetValue<string>(), "the image block");
            t.Check((await p.ListModelsAsync(false, default)) is var _ && CL.ClaudeCliProvider.UserImages(Req(messages: [ChatMessage.UserText("x")]), 0) is null, "no pictures: plain text");
        });

        await t.Run("claude-cli: the CLI runs with its own tools in the session folder: no session, no skills, no connectors, the system prompt from a file, effort passed", async () =>
        {
            SeenCall? seen = null;
            string? promptText = null;
            var sessionDir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            string? workDir = null;
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) =>
            {
                var args = call.Args.ToList();
                var file = args[args.IndexOf("--system-prompt-file") + 1];
                promptText = File.ReadAllText(file);
                workDir = call.WorkDir;
                seen = new SeenCall(args, call.Stdin, File.Exists(file));
                return Lines([Init, Result], ct);
            }, sid => sid == "ses_1" ? sessionDir : null);
            var req = Req("claude-opus-5-5");
            req.ReasoningEffort = "high";
            req.Tools = [new ToolDefinition { Name = "read", Description = "d", Parameters = new JsonObject { ["type"] = "object" } }];
            var events = await Collect(p, req);
            var a = seen!.Args;
            t.Eq("claude-opus-5-5", a[a.IndexOf("--model") + 1], "model id");
            t.Eq("default", a[a.IndexOf("--tools") + 1], "every tool of the CLI");
            t.Eq("bypassPermissions", a[a.IndexOf("--permission-mode") + 1], "no prompts in a print-mode call");
            t.Eq(sessionDir, workDir, "the CLI works in the session's folder");
            t.Check(a.Contains("--session-id") && !a.Contains("--no-session-persistence") && a.Contains("--disable-slash-commands") && a.Contains("--strict-mcp-config"), "a session the chat can resume, no skills, no account connectors");
            t.Eq("", a[a.IndexOf("--setting-sources") + 1], "no settings files");
            t.Eq("high", a[a.IndexOf("--effort") + 1], "effort");
            t.Check(!a.Contains("--system-prompt"), "the system prompt is not on the command line");
            t.Check(promptText!.StartsWith("You are a planner.") && promptText.EndsWith(CL.ClaudeCliProvider.CliToolsNote), "the system prompt file carries the prompt, then which tools it has");
            t.Check(promptText.Contains("do not write their calls"), "the model is told not to write NetPI tool-call markup");
            t.Eq("Plan a thing.", seen.Stdin, "a single user message goes in as it is");
            t.Check(events.OfType<StreamNotice>().Any(n => n.Text.Contains("does not use NetPI's tools") && n.Text.Contains(sessionDir)), "a request with tools says they are not used, and where the CLI works");
            t.Check(!File.Exists(a[a.IndexOf("--system-prompt-file") + 1]), "the prompt file is removed after the call");
            Directory.Delete(sessionDir, true);

            // tools "none": text only, as before
            var none = new JsonObject { ["enabled"] = true, ["tools"] = "none" };
            var noneArgs = new List<string>();
            string? nonePrompt = null;
            p = new CL.ClaudeCliProvider(() => none, null, (call, ct) =>
            {
                noneArgs.AddRange(call.Args);
                nonePrompt = File.ReadAllText(call.Args[call.Args.ToList().IndexOf("--system-prompt-file") + 1]);
                return Lines([Init, Result], ct);
            });
            await Collect(p, Req());
            t.Eq("", noneArgs[noneArgs.IndexOf("--tools") + 1], "tools none: switched off");
            t.Check(!noneArgs.Contains("--permission-mode"), "no tools: no permission mode");
            t.Check(nonePrompt!.EndsWith(CL.ClaudeCliProvider.NoToolsNote), "no tools: the model is told it has none");
        });

        await t.Run("claude-cli: the CLI's own tool use becomes tool-call parts with their results, and usage is the last turn's, not the total", async () =>
        {
            var lines = new[]
            {
                Init,
                Ev("""{"type":"message_start","message":{"usage":{"input_tokens":3,"cache_read_input_tokens":1000,"output_tokens":1}}}"""),
                Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tu_1","name":"Read","input":{}}}"""),
                Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\":\"src/x"}}"""),
                Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":".cs\"}"}}"""),
                Ev("""{"type":"content_block_stop","index":0}"""),
                Ev("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":3,"cache_read_input_tokens":1000,"output_tokens":20}}"""),
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"sub_1","name":"Grep","input":{"pattern":"foo"}}]},"parent_tool_use_id":"tu_1"}""",
                """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"file text","is_error":false}]},"parent_tool_use_id":null}""",
                """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":"tu_other"}""",
                Ev("""{"type":"message_start","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":2000,"output_tokens":1}}}"""),
                Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Done."}}"""),
                Ev("""{"type":"content_block_stop","index":0}"""),
                Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":5,"cache_read_input_tokens":2000,"output_tokens":7}}"""),
                """{"type":"result","subtype":"success","is_error":false,"result":"Done.","usage":{"input_tokens":8,"output_tokens":27,"cache_read_input_tokens":3000}}""",
            };
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(lines, ct));
            var events = await Collect(p, Req());
            t.Check(events.OfType<ToolCallStarted>().Any(c => c.Id == "tu_1" && c.Name == "Read"), "the call streams as a tool call");
            var done = events.OfType<StreamCompleted>().Single().Message;
            t.Eq("Done.", done.Text, "answer text: a subagent's lines are not part of it");
            var call = done.Parts.OfType<ToolCallPart>().Single();
            t.Check(call.ProviderExecuted && call.Name == "Read" && call.Arguments == "{\"file_path\":\"src/x.cs\"}", $"the call is marked as run by the CLI, with its arguments: {call.Arguments}");
            t.Eq("file text", call.ProviderData![ToolCallPart.ResultKey]!["content"]!.GetValue<string>(), "its result travels with the call");
            t.Eq("stop", done.StopReason, "the message is an answer, not a request for tools");

            // the lifecycle is reported while the CLI works, so the chat does not show the call as still being written
            var order = events.Select(e => e switch
            {
                ProviderToolRunning r when r.Id == "tu_1" && r.Name == "Read" && r.Arguments == "{\"file_path\":\"src/x.cs\"}" => "running",
                ProviderToolOutput o when o.Id == "tu_1" && o.Text == "↳ Grep foo\n" => "output",
                ProviderToolDone d when d.Id == "tu_1" && !d.IsError && d.Content == "file text" => "done",
                _ => null,
            }).Where(x => x is not null).ToList();
            t.Eq("running,output,done", string.Join(",", order), "running, then a subagent's progress, then done with its result");
            t.Check(call.ProviderLive, "the call is marked as reported live");
            t.Eq("read", call.ProviderAs, "named for the hooks as the NetPI tool it stands for");
            t.Eq(2000L, done.Usage!.CacheReadTokens, "context size is the last turn's");
            t.Eq(7L, done.Usage!.OutputTokens, "output of the last turn");
        });

        await t.Run("claude-cli: the CLI's own tools follow what NetPI gives the run: read-only in plan mode, a tool switched off is held back, one NetPI lacks is left alone", () =>
        {
            static ToolDefinition T(string n) => new() { Name = n, Description = "d", Parameters = new JsonObject { ["type"] = "object" } };
            static ModelRequest R(IEnumerable<string> tools, bool readOnly = false) => new()
            {
                Model = new ModelInfo { Provider = "claude-cli", Id = "claude-opus-5-5" }, Messages = [ChatMessage.UserText("go")],
                Tools = [.. tools.Select(T)], Purpose = "agent", ReadOnlyTools = readOnly,
            };
            string[] all = ["bash", "pwsh", "edit", "write", "read", "grep", "find", "ls", "web_fetch", "web_search", "agent_spawn", "todo_write"];
            var o = CL.ClaudeCliOptions.Parse(new JsonObject { ["enabled"] = true });

            var (tools, off, note) = CL.ClaudeCliProvider.ToolPolicy(R(all), o, all);
            t.Eq("default", tools, "everything offered: the CLI's whole set");
            t.Eq(0, off.Count, "nothing held back");
            t.Check(note is null, "and nothing to say");

            (tools, off, note) = CL.ClaudeCliProvider.ToolPolicy(R(all.Where(n => n is not ("bash" or "pwsh"))), o, all);
            t.Check(off.Contains("Bash") && off.Contains("BashOutput") && !off.Contains("Edit") && !off.Contains("Read"), $"bash switched off: the CLI's Bash goes with it ({string.Join(",", off)})");
            t.Check(note!.Contains("without Bash"), $"and the chat is told: {note}");

            var noWeb = all.Where(n => !n.StartsWith("web_", StringComparison.Ordinal)).ToArray();
            (_, off, _) = CL.ClaudeCliProvider.ToolPolicy(R(noWeb), o, noWeb);
            t.Check(!off.Contains("WebFetch") && !off.Contains("WebSearch"), "a tool NetPI does not have at all leaves its CLI counterpart alone");

            (tools, off, note) = CL.ClaudeCliProvider.ToolPolicy(R(all, readOnly: true), o, all);
            t.Eq("Read,Grep,Glob,WebFetch,WebSearch,TodoWrite", tools, "a run that may only read keeps the reading tools alone");
            t.Check(note!.Contains("read-only"), $"and says so: {note}");
            (tools, _, _) = CL.ClaudeCliProvider.ToolPolicy(R(["read", "grep"], readOnly: true), o, all);
            t.Eq("Read,Grep", tools, "and only those NetPI gives it too");

            var listed = CL.ClaudeCliOptions.Parse(new JsonObject { ["enabled"] = true, ["tools"] = "Read,Bash,Edit" });
            (tools, _, _) = CL.ClaudeCliProvider.ToolPolicy(R(all.Where(n => n is not ("bash" or "pwsh"))), listed, all);
            t.Eq("Read,Edit", tools, "a configured list loses what the run is not given");

            var args = CL.ClaudeCliProvider.BuildArgs(R(all), o, "default", "p.md", new CL.ClaudeCliProvider.CliAttempt(false, "", "", false), ["Bash", "BashOutput"]);
            t.Eq("Bash,BashOutput", args[args.IndexOf("--disallowedTools") + 1], "what is held back goes to --disallowedTools");
            return Task.CompletedTask;
        });

        await t.Run("claude-cli: a history is sent as a labelled transcript; an API-key login is called out", async () =>
        {
            string? stdin = null;
            var apiKey = """{"type":"system","subtype":"init","apiKeySource":"ANTHROPIC_API_KEY"}""";
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) => { stdin = call.Stdin; return Lines([apiKey, Result], ct); });
            var messages = new List<ChatMessage>
            {
                ChatMessage.UserText("First?"),
                new() { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "Answer." }] },
                ChatMessage.UserText("Second?"),
            };
            var events = await Collect(p, Req(messages: messages));
            t.Eq("The conversation so far:\n\nUser:\nFirst?\n\nAssistant:\nAnswer.\n\nUser:\nSecond?\n\nReply to the last user message.", stdin, "transcript");
            t.Check(events.OfType<StreamNotice>().Any(n => n.Text.Contains("billed to the API")), "API-key sign-in is announced");
        });

        await t.Run("claude-cli: a failure after the CLI ran tools is reported, never retried from the top", async () =>
        {
            var tool = new[]
            {
                Init,
                Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tu_1","name":"Bash","input":{}}}"""),
                Ev("""{"type":"content_block_stop","index":0}"""),
            };
            var overloaded = tool.Append("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Overloaded","api_error_status":529}""");
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(overloaded, ct));
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false, StatusCode: 529 }, "a 529 after tool use is not transient: a retry would run and bill the job again");
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines(tool, ct));
            e = await Fails(p, Req());
            t.Check(e is { Transient: false, ErrorType: "cli_incomplete" } && e.Message.Contains("already run tools"), $"a CLI that died mid-job: {e?.Message}");
        });

        await t.Run("claude-cli: results of the CLI's own tools stay out of the next prompt", async () =>
        {
            string? stdin = null;
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) => { stdin = call.Stdin; return Lines([Init, Result], ct); });
            var ran = new ToolCallPart { Id = "tu_1", Name = "Read", Arguments = "{}", ProviderData = new JsonObject { [ToolCallPart.ExecutedKey] = true } };
            var other = new ToolCallPart { Id = "c_2", Name = "peek", Arguments = "{}" };
            var messages = new List<ChatMessage>
            {
                ChatMessage.UserText("Look."),
                new() { Role = MessageRole.Assistant, Parts = [ran, other, new TextPart { Text = "Looked." }] },
                new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "tu_1", Name = "Read", Content = "HUGE FILE" }] },
                new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "c_2", Name = "peek", Content = "peeked" }] },
                ChatMessage.UserText("And?"),
            };
            await Collect(p, Req(messages: messages));
            t.Check(stdin!.Contains("peeked") && !stdin.Contains("HUGE FILE"), "only the results of calls NetPI ran are in the transcript");
        });

        await t.Run("claude-cli: models only while enabled; a disabled provider fails with the setting named", async () =>
        {
            var p = new CL.ClaudeCliProvider(() => Cfg(enabled: false), null, (_, ct) => Lines([], ct));
            t.Eq(0, (await p.ListModelsAsync(true, CancellationToken.None)).Count, "disabled: no models");
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false } && e.Message.Contains("`enabled`"), "disabled error");
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([], ct));
            var models = await p.ListModelsAsync(true, CancellationToken.None);
            t.Eq(3, models.Count, "default models");
            t.Check(models.All(m => m.Provider == "claude-cli" && m.Reasoning is { Supported: true }), "models carry the provider and efforts");
            t.Check(models.All(m => m.StallTimeoutSeconds == 1800), "models carry the CLI's silence tolerance (the global 180 s would reset a running subagent)");

            var cfgStall = Cfg();
            cfgStall["stallTimeoutSeconds"] = 600;
            p = new CL.ClaudeCliProvider(() => cfgStall, null, (_, ct) => Lines([], ct));
            models = await p.ListModelsAsync(true, CancellationToken.None);
            t.Check(models.All(m => m.StallTimeoutSeconds == 600), "a configured tolerance is carried");

            // Enabling it (or editing the models) after the catalog listed it must be reported, or no agent can pick it.
            var cfg = Cfg(enabled: false);
            var live = new CL.ClaudeCliProvider(() => cfg, null, (_, ct) => Lines([], ct));
            t.Check(!live.ModelsChangedSinceLastList(), "nothing listed yet: nothing changed");
            await live.ListModelsAsync(true, CancellationToken.None);
            t.Check(!live.ModelsChangedSinceLastList(), "unchanged since the list");
            cfg["enabled"] = true;
            t.Check(live.ModelsChangedSinceLastList(), "enabling changes the catalog");
            await live.ListModelsAsync(true, CancellationToken.None);
            cfg["models"] = new JsonArray("claude-opus-5-5");
            t.Check(live.ModelsChangedSinceLastList(), "editing the models changes the catalog");
        });

        await t.Run("claude-cli: a CLI with a shell is told how it reaches NetPI itself (netpi.mjs beside the app, NetPI's home), one without a shell is not", () =>
        {
            var p = new CL.ClaudeCliProvider(() => Cfg()) { RpcScript = @"C:\NetPI\app\netpi.mjs", Home = @"C:\Users\me\.netpi" };
            t.Eq("node \"C:/NetPI/app/netpi.mjs\" --home \"C:/Users/me/.netpi\"", p.RpcCommand, "forward slashes: one spelling for bash and node");
            t.Eq(null, new CL.ClaudeCliProvider(() => Cfg()).RpcCommand, "no script beside the app: no command");
            var noted = CL.ClaudeCliProvider.SystemPromptWithNote("S", true, p.RpcCommand);
            t.Check(noted.Contains("# NetPI from your shell") && noted.Contains("node \"C:/NetPI/app/netpi.mjs\" --home") && noted.Contains("mcp.read")
                && noted.Contains("ideas.add"), "the command and what it is for");
            t.Check(!CL.ClaudeCliProvider.SystemPromptWithNote("S", false, p.RpcCommand).Contains("NetPI from your shell"), "no tools: no shell to run it with");
            t.Check(CL.ClaudeCliProvider.HasShell("default", []), "every tool: a shell");
            t.Check(!CL.ClaudeCliProvider.HasShell("default", ["Bash", "BashOutput"]), "Bash held back: none");
            t.Check(CL.ClaudeCliProvider.HasShell("Read, Bash", []) && !CL.ClaudeCliProvider.HasShell("Read,Grep,Glob", []), "a configured list");
            return Task.CompletedTask;
        });

        await t.Run("claude-cli: live call through the installed CLI (set NETPI_CLAUDE_CLI_LIVE=1; uses the subscription)", async () =>
        {
            if (Environment.GetEnvironmentVariable("NETPI_CLAUDE_CLI_LIVE") != "1") t.Skip("NETPI_CLAUDE_CLI_LIVE is not 1");
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
            File.WriteAllText(Path.Combine(dir, "planet.txt"), "The red planet is Mars.");
            var p = new CL.ClaudeCliProvider(() => Cfg(exe: File.Exists(exe) ? exe : null), null, cwdOf: _ => dir);
            var req = Req("claude-sonnet-5-5");
            req.SystemPrompt = "You are terse.";
            req.Messages = [ChatMessage.UserText("Read planet.txt, then create result.txt containing only the name of the planet it mentions. Reply with that name.")];
            var events = await Collect(p, req);
            var done = events.OfType<StreamCompleted>().Single().Message;
            t.Check(done.Text.Contains("Mars", StringComparison.OrdinalIgnoreCase), $"answer: {done.Text}");
            t.Check(File.Exists(Path.Combine(dir, "result.txt")) && File.ReadAllText(Path.Combine(dir, "result.txt")).Contains("Mars"), "the CLI wrote the file in the session folder");
            t.Check(done.Parts.OfType<ToolCallPart>().Any(c => c.ProviderExecuted), "its tool use came back as tool calls run by the CLI");
            t.Check(events.OfType<TextDelta>().Any(), "text streamed as deltas");
            t.Check(done.Usage is { OutputTokens: > 0 }, "usage reported");
            t.Check(!events.OfType<StreamNotice>().Any(n => n.Text.Contains("billed")), "subscription login, not an API key");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
        });

        await t.Run("claude-cli: the real process runner reports a missing executable and reads a real process", async () =>
        {
            var p = new CL.ClaudeCliProvider(() => Cfg(exe: Path.Combine(Path.GetTempPath(), "netpi-no-such-claude.exe")), null);
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false, ErrorType: "cli_not_found" } && e.Message.Contains("claude auth login"), $"missing executable: {e?.Message}");

            if (!OperatingSystem.IsWindows()) t.Skip("the fake CLI is a Windows batch file");
            var dir = Path.Combine(Path.GetTempPath(), "netpi-claude-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(Path.Combine(dir, "out.jsonl"), [Init, Result]);
                var cmd = Path.Combine(dir, "claude.cmd");
                File.WriteAllText(cmd, "@echo off\r\ntype \"%~dp0out.jsonl\"\r\n");
                p = new CL.ClaudeCliProvider(() => Cfg(exe: cmd), null);
                var done = (await Collect(p, Req())).OfType<StreamCompleted>().Single().Message;
                t.Eq("Hello world", done.Text, "text through a real process");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
        });
    }

    private sealed record SeenCall(List<string> Args, string Stdin, bool PromptFileExisted);
}
