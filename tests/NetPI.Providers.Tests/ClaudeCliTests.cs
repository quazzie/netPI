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
        Ev("""{"type":"message_start","message":{"usage":{"input_tokens":12,"output_tokens":1}}}"""),
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

        await t.Run("claude-cli: the call is text only: no tools, no session, no settings, the system prompt from a file, effort passed", async () =>
        {
            SeenCall? seen = null;
            string? promptText = null;
            var p = new CL.ClaudeCliProvider(() => Cfg(), null, (call, ct) =>
            {
                var args = call.Args.ToList();
                var file = args[args.IndexOf("--system-prompt-file") + 1];
                promptText = File.ReadAllText(file);
                seen = new SeenCall(args, call.Stdin, File.Exists(file));
                return Lines([Init, Result], ct);
            });
            var req = Req("claude-opus-5-5");
            req.ReasoningEffort = "high";
            req.Tools = [new ToolDefinition { Name = "read", Description = "d", Parameters = new JsonObject { ["type"] = "object" } }];
            var events = await Collect(p, req);
            var a = seen!.Args;
            t.Eq("claude-opus-5-5", a[a.IndexOf("--model") + 1], "model id");
            t.Eq("", a[a.IndexOf("--tools") + 1], "tools switched off");
            t.Check(a.Contains("--no-session-persistence") && a.Contains("--disable-slash-commands") && a.Contains("--strict-mcp-config"), "no session, no skills, no account connectors");
            t.Eq("", a[a.IndexOf("--setting-sources") + 1], "no settings files");
            t.Eq("high", a[a.IndexOf("--effort") + 1], "effort");
            t.Check(!a.Contains("--system-prompt"), "the system prompt is not on the command line");
            t.Eq("You are a planner.", promptText, "system prompt file content");
            t.Eq("Plan a thing.", seen.Stdin, "a single user message goes in as it is");
            t.Check(events.OfType<StreamNotice>().Any(n => n.Text.Contains("no tools")), "a request with tools says they are not offered");
            t.Check(!File.Exists(a[a.IndexOf("--system-prompt-file") + 1]), "the prompt file is removed after the call");
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

        await t.Run("claude-cli: models only while enabled; a disabled provider fails with the setting named", async () =>
        {
            var p = new CL.ClaudeCliProvider(() => Cfg(enabled: false), null, (_, ct) => Lines([], ct));
            t.Eq(0, (await p.ListModelsAsync(true, CancellationToken.None)).Count, "disabled: no models");
            var e = await Fails(p, Req());
            t.Check(e is { Transient: false } && e.Message.Contains("providers.claudeCli.enabled"), "disabled error");
            p = new CL.ClaudeCliProvider(() => Cfg(), null, (_, ct) => Lines([], ct));
            var models = await p.ListModelsAsync(true, CancellationToken.None);
            t.Eq(3, models.Count, "default models");
            t.Check(models.All(m => m.Provider == "claude-cli" && m.Reasoning is { Supported: true }), "models carry the provider and efforts");

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

        await t.Run("claude-cli: live call through the installed CLI (set NETPI_CLAUDE_CLI_LIVE=1; uses the subscription)", async () =>
        {
            if (Environment.GetEnvironmentVariable("NETPI_CLAUDE_CLI_LIVE") != "1") t.Skip("NETPI_CLAUDE_CLI_LIVE is not 1");
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            var p = new CL.ClaudeCliProvider(() => Cfg(exe: File.Exists(exe) ? exe : null), null);
            var req = Req("claude-sonnet-5-5");
            req.SystemPrompt = "You are terse. Answer with one word.";
            req.Messages = [ChatMessage.UserText("Which planet is called the red planet?")];
            var events = await Collect(p, req);
            var done = events.OfType<StreamCompleted>().Single().Message;
            t.Check(done.Text.Contains("Mars", StringComparison.OrdinalIgnoreCase), $"answer: {done.Text}");
            t.Check(events.OfType<TextDelta>().Any(), "text streamed as deltas");
            t.Check(done.Usage is { OutputTokens: > 0 }, "usage reported");
            t.Check(!events.OfType<StreamNotice>().Any(n => n.Text.Contains("billed")), "subscription login, not an API key");
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
