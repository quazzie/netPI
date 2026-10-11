using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.MockLlm;

/// <summary>What the mock model answers: optional thinking, text and tool calls, and how to stream them.</summary>
public sealed class Plan
{
    public string Scenario { get; set; } = "default";
    public int Step { get; set; }
    public string? Thinking { get; set; }
    public string? Text { get; set; }
    public List<NCall> Calls { get; } = [];
    /// <summary>stop | length (tool_use is derived from <see cref="Calls"/>).</summary>
    public string Stop { get; set; } = "stop";
    /// <summary>Delay between two streamed chunks (before the speed factor).</summary>
    public int ChunkDelayMs { get; set; } = 8;
    /// <summary>
    /// Keep the stream open for this long after its first chunk (a fixed time, not scaled by --speed; <c>hold=&lt;ms&gt;</c> in the tag, first
    /// answer of the scenario only). A test that has to look at the live state holds it instead of racing a stream that is over in 100 ms.
    /// </summary>
    public int HoldMs { get; set; }
    /// <summary>
    /// Wait at this named gate after the first chunk until a test releases it (<c>gate=&lt;name&gt;</c> in the tag, first answer of the
    /// scenario only; <c>sumgate=&lt;name&gt;</c> gates the summary of a conversation that carries it). See <see cref="Gates"/>.
    /// </summary>
    public string? Gate { get; set; }
    /// <summary>Wait at this named gate before anything is streamed (no first token yet): <c>aftergate=</c>.</summary>
    public string? StartGate { get; set; }
    /// <summary>Drop the connection in the middle of the stream.</summary>
    public bool Drop { get; set; }
    /// <summary>Stop sending (but keep the connection open) for this long in the middle of the stream.</summary>
    public int StallMs { get; set; }
    /// <summary>Fail inside the stream (after some text) with this error type (response.failed / error chunk / error event).</summary>
    public string? StreamErrorType { get; set; }
    public int? ErrorStatus { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }

    public Plan Call(string name, JsonObject args)
    {
        Calls.Add(new NCall { Name = name, Args = args.ToJsonString() });
        return this;
    }
}

/// <summary>A <c>[s:name key=value …]</c> tag found in a user message.</summary>
public sealed partial class ScenarioTag
{
    public required string Name { get; init; }
    public Dictionary<string, string> Params { get; } = new(StringComparer.OrdinalIgnoreCase);
    public required string Raw { get; init; }

    public string P(string key, string fallback) => Params.TryGetValue(key, out var v) ? v : fallback;
    public int PInt(string key, int fallback) => Params.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    [GeneratedRegex(@"\[s:([a-z0-9_-]+)((?:\s+[a-z0-9_]+=(?:""[^""]*""|[^\s\]""]+))*)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"([a-z0-9_]+)=(?:""([^""]*)""|([^\s\]""]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ParamRx();

    /// <summary>The last tag in <paramref name="text"/>.</summary>
    public static ScenarioTag? FindLast(string text)
    {
        var matches = TagRx().Matches(text);
        if (matches.Count == 0) return null;
        var m = matches[^1];
        var tag = new ScenarioTag { Name = m.Groups[1].Value.ToLowerInvariant(), Raw = m.Value };
        foreach (Match p in ParamRx().Matches(m.Groups[2].Value))
            tag.Params[p.Groups[1].Value] = p.Groups[2].Success ? p.Groups[2].Value : p.Groups[3].Value;
        return tag;
    }

    public static IEnumerable<string> All(string text) => TagRx().Matches(text).Select(m => m.Value);

    public static string Strip(string text) => TagRx().Replace(text, "").Trim();
}

/// <summary>
/// Picks the answer from the conversation. The scenario is the <c>[s:…]</c> tag of the latest user message that is not a
/// harness notice (no tag = plain chat); the step is the number of assistant messages after it. Subagents (system prompt says so) and the compaction
/// summarizer get their own answers. See docs/TESTING.md for the scenario list.
/// </summary>
public sealed partial class ScenarioEngine
{
    /// <summary>Attempts per conversation fingerprint (drop / error scenarios fail the first attempts).</summary>
    private readonly ConcurrentDictionary<string, int> _attempts = new();

    /// <summary>The compaction summaries an answer was given after (aftergate=): only the first answer after one waits.</summary>
    private readonly ConcurrentDictionary<string, byte> _summariesAnswered = new();

    public void Reset()
    {
        _attempts.Clear();
        _summariesAnswered.Clear();
    }

    [GeneratedRegex(@"LONGSTEP (\d+)/(\d+)")]
    private static partial Regex LongStepRx();

    [GeneratedRegex(@"You are ""([^""]+)"" \(agent id")]
    private static partial Regex AgentNameRx();

    [GeneratedRegex(@"\[s:[^\]]*\bsumgate=([a-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SumGateRx();

    public Plan Decide(NRequest req)
    {
        if (req.IsSummarizer)
        {
            var summary = Summary(req);
            summary.Gate = SumGateRx().Matches(string.Join('\n', req.Messages.Select(m => m.Text))) is { Count: > 0 } g ? g[^1].Groups[1].Value : null;
            return summary;
        }
        var msgs = req.Messages;
        var last = msgs.Count > 0 ? msgs[^1] : new NMsg();

        // The latest real user message (not a harness notice) picks the scenario; without a tag it is a plain chat. A goal's
        // notice counts as one when its objective (which every goal notice repeats) carries a tag: a goal set with /goal has
        // no user message of its own.
        var anchor = msgs.FindLastIndex(m => m.Role == "user" && (!m.IsNotice || m.NoticeKind == "goal" && ScenarioTag.FindLast(m.Text) is not null));
        var tag = anchor >= 0 ? ScenarioTag.FindLast(msgs[anchor].Text) : null;
        var after = anchor < 0 ? msgs : msgs.Skip(anchor + 1).ToList();
        var step = after.Count(m => m.Role == "assistant");
        var name = tag?.Name ?? (req.IsSubagent ? "sub" : "default");
        tag ??= new ScenarioTag { Name = name, Raw = "" };

        Plan plan;
        if (last.NoticeKind == "nudge") plan = Final("Resumed after the nudge: the task is complete. NUDGE-RESUMED");
        else if (last.NoticeKind == "agent-result" && name != "spawn") plan = AgentResult(last);
        else
        {
            plan = name switch
            {
                "mcp" => step switch {
                    0 => new Plan().Call("tool_search", new JsonObject { ["query"] = tag.P("id", ""), ["limit"] = 1 }),
                    1 => new Plan().Call(tag.P("id", ""), new JsonObject { ["city"] = "Oslo" }),
                    _ => Final("Weather received. MCP-DONE")
                },
                "tools" => Tools(tag, step, after),
                "parallel" => Parallel(tag, step, after),
                "bash" => Bash(step, after),
                "fgwait" => FgWait(tag, step),
                "web" => Web(tag, step),
                "ssh" => SshWork(step),
                "bgproc" => BgProc(tag, step),
                "where" => Where(tag, step, after),
                "textcall" => TextCall(step, after),
                "doccall" => DocCall(step, after),
                "cutoff" => Cutoff(step),
                "drop" => Drop(req, tag),
                "stall" => Stall(req, tag),
                "midfail" => MidFail(req, tag),
                "say" => Final(tag.P("text", "OK.")),
                "thinktags" => Final("<think>The user wants tags. I will reason inside think tags like Qwen does without a reasoning parser.</think>Answer after the think block. THINKTAGS-DONE"),
                "empty" => step == 0 ? new Plan() : Final("Sorry, here is the answer after all. EMPTY-RECOVERED"),
                "badargs" => BadArgs(tag, step, after),
                "error" => Error(req, tag),
                "fail" => new Plan { ErrorStatus = 400, ErrorType = "invalid_request_error", ErrorMessage = "mock: this request is rejected on purpose" },
                "slow" => Slow(tag),
                "slowtools" => SlowTools(step, after),
                "spawn" => Spawn(tag, step, after),
                "spawnbg" => SpawnBackground(tag, step),
                "nest" => Nest(tag, step, after),
                "subspawn" => SubSpawn(req, tag, step, after),
                "sub" => Sub(req, tag),
                "ideas" => Ideas(tag, step, after),
                "ask" => Ask(tag, step, after),
                "guard" => Guard(step, after),
                "watchrun" => WatchRun(req, tag, step),
                "multiedit" => MultiEdit(step, after),
                "planui" => PlanUi(tag, step, after),
                "goal" => GoalFlow(msgs),
                "codemode" => CodeMode(step, after),
                "plan" => PlanFlow(tag, step, after),
                "long" => Long(tag, msgs),
                _ => Echo(req, anchor >= 0 ? msgs[anchor] : msgs.LastOrDefault(m => m.Role == "user" && !m.IsNotice), step),
            };
        }
        plan.Scenario = name;
        plan.Step = step;
        if (step == 0)
        {
            plan.HoldMs = Math.Clamp(tag.PInt("hold", 0), 0, 60_000);
            plan.Gate = tag.Params.TryGetValue("gate", out var gate) && gate.Length > 0 ? gate : null;
        }
        // aftergate=<name>: the first answer after a compaction waits there, before its first token: the first request that
        // carries a summary this engine has not answered before (what the compaction kept can hold answers from before it,
        // so counting those says nothing)
        if (tag.Params.TryGetValue("aftergate", out var afterGate) && afterGate.Length > 0
            && msgs.FirstOrDefault(m => m.Text.Contains("<conversation-summary>", StringComparison.Ordinal)) is { } summaryMsg
            && _summariesAnswered.TryAdd(summaryMsg.Text, 0))
            plan.StartGate = afterGate;
        return plan;
    }

    // ------------------------------------------------------------------ helpers

    private static Plan Final(string text, string? thinking = null) => new() { Text = text, Thinking = thinking };

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var nl = s.IndexOf('\n');
        var line = nl < 0 ? s : s[..nl];
        return line.Length > 160 ? line[..160] : line;
    }

    private static List<NMsg> ToolResults(List<NMsg> after) => after.Where(m => m.Role == "tool").ToList();

    private static JsonObject Obj(params (string K, JsonNode? V)[] props)
    {
        var o = new JsonObject();
        foreach (var (k, v) in props) o[k] = v;
        return o;
    }

    // ------------------------------------------------------------------ scenarios

    private static Plan Echo(NRequest req, NMsg? user, int step)
    {
        var text = ScenarioTag.Strip(user?.Text ?? "");
        if (text.Length > 400) text = text[..400] + "…";
        var sb = new StringBuilder();
        sb.Append("**Echo** from `").Append(req.Model).Append("` via ").Append(req.Api).Append(": ").Append(text.Length == 0 ? "(empty)" : text).Append("\n\n");
        sb.Append("| field | value |\n|---|---|\n");
        sb.Append("| messages | ").Append(req.Messages.Count).Append(" |\n");
        sb.Append("| tools | ").Append(req.Tools.Count).Append(" |\n");
        sb.Append("| turn | ").Append(step).Append(" |\n");
        sb.Append("| images | ").Append(user?.Images ?? 0).Append(" |\n\n");
        sb.Append("```csharp\nConsole.WriteLine(\"hello from the mock\");\n```\n\nECHO-DONE");
        return new Plan
        {
            Thinking = $"The user wrote \"{(text.Length > 60 ? text[..60] : text)}\". I'll answer with a short markdown echo.",
            Text = sb.ToString(),
        };
    }

    private static Plan AgentResult(NMsg notice)
    {
        var report = notice.Text;
        var i = report.IndexOf("<agent-result", StringComparison.Ordinal);
        var body = i < 0 ? report : report[i..];
        var nl = body.IndexOf('\n');
        var content = nl < 0 ? body : body[(nl + 1)..];
        return Final("Received the agent result: " + FirstLine(content.Trim()) + "\n\nAGENT-RESULT-RECEIVED",
            "A subagent reported back; I'll acknowledge its result.");
    }

    private static Plan Tools(ScenarioTag tag, int step, List<NMsg> after)
    {
        var file = tag.P("file", "notes.txt");
        var oldText = tag.P("old", "alpha");
        var newText = tag.P("new", "ALPHA-EDITED");
        var create = tag.P("create", "created/by-mock.txt");
        switch (step)
        {
            case 0:
                return new Plan { Thinking = "The user wants me to inspect and edit files. First I need to see what is in the workspace.", Text = "Let me look at the workspace first." }
                    .Call("ls", Obj(("path", ".")));
            case 1:
                return new Plan { Thinking = $"The listing is in. Next I read {file}." }
                    .Call("read", Obj(("path", file)));
            case 2:
                return new Plan { Thinking = $"I'll replace \"{oldText}\" with \"{newText}\" and create {create}.", Text = "Applying the edit and creating a new file." }
                    .Call("edit", Obj(("path", file), ("oldText", oldText), ("newText", newText)))
                    .Call("write", Obj(("path", create), ("content", "created by the mock model\nsecond line\n")));
            default:
            {
                var results = ToolResults(after);
                var edit = results.Count >= 2 ? FirstLine(results[^2].Text) : "?";
                var write = results.Count >= 1 ? FirstLine(results[^1].Text) : "?";
                return Final($"## Done\n\n- listed the workspace\n- read `{file}`\n- edit: {edit}\n- write: {write}\n\nTOOLS-DONE",
                    "All tool calls finished; I'll summarize.");
            }
        }
    }

    private static Plan Parallel(ScenarioTag tag, int step, List<NMsg> after)
    {
        var file = tag.P("file", "notes.txt");
        if (step == 0)
            return new Plan { Thinking = "Several independent lookups: I'll run them at once.", Text = "Running four read-only lookups in parallel." }
                .Call("read", Obj(("path", file)))
                .Call("grep", Obj(("pattern", tag.P("pattern", "alpha"))))
                .Call("find", Obj(("pattern", "*.txt")))
                .Call("ls", Obj(("path", ".")));
        var n = ToolResults(after).Count;
        return Final($"Got {n} results from the parallel lookups. PARALLEL-DONE");
    }

    private static Plan Bash(int step, List<NMsg> after)
    {
        if (step == 0)
            return new Plan { Thinking = "I'll run a small shell command.", Text = "Running a command." }
                .Call("bash", Obj(("command", "echo hello from bash; for i in 1 2 3; do echo \"line $i\"; sleep 0.3; done")));
        var output = ToolResults(after).LastOrDefault()?.Text ?? "(no output)";
        return Final("Command output:\n\n```text\n" + output.Trim() + "\n```\n\nBASH-DONE");
    }

    /// <summary>A one-pixel PNG as a data: URL: an image show_image can show without a file or a request.</summary>
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    /// <summary>
    /// A run with the web tools: a plan (<c>todo_write</c>; the first answer, thinking as it streams, waits at gate=), a search
    /// (its answer waits at plangate=, the plan on screen meanwhile), a fetched page, a screenshot of a page that logs an
    /// error and an image shown in the chat, the plan done, and an answer with links to two files, a web link, a remote
    /// image, raw HTML and a forged copy button. base= is this mock's address: its /web pages, and web.search.searxngUrl
    /// = base/web/sx/ for the search. The screenshot needs a model that reads images.
    /// </summary>
    private static Plan Web(ScenarioTag tag, int step)
    {
        var web = tag.P("base", "http://127.0.0.1:7479").TrimEnd('/') + "/web";
        static JsonArray Todo(params string[] status) => new([.. new[] { "Find what breaks the demo", "Read the $state docs", "Show the fix" }
            .Select((t, i) => (JsonNode)Obj(("text", t), ("status", status[i])))]);
        switch (step)
        {
            case 0:
                return new Plan
                {
                    Thinking = "The demo page renders a count that never shows. In Svelte 5 a plain let is not reactive any more, so the markup may read a name before it exists. I'll plan the steps, then read the docs and look at the page.",
                    Text = "Let me plan this.",
                }.Call("todo_write", Obj(("items", Todo("in_progress", "pending", "pending"))));
            case 1:
            {
                var plan = new Plan { Text = "Searching the docs." }.Call("web_search", Obj(("query", "svelte 5 $state reactive")));
                plan.Gate = tag.Params.GetValueOrDefault("plangate");
                return plan;
            }
            case 2:
                return new Plan { Text = "Reading the page." }.Call("web_fetch", Obj(("url", web + "/state")));
            case 3:
                return new Plan { Text = "Looking at the demo." }
                    .Call("browser", Obj(("action", "screenshot"), ("url", web + "/demo")))
                    .Call("show_image", Obj(("source", Pixel), ("caption", "The demo as it renders now")));
            case 4:
                return new Plan { Text = "All three done." }.Call("todo_write", Obj(("items", Todo("done", "done", "done"))));
            default:
                return Final(
                    "The demo breaks because `count` is read before it is declared: with runes a plain `let` is not reactive.\n\n" +
                    "The fix goes in [App.svelte](web/src/App.svelte), and the counter it reads is in [counter.js](web/src/lib/counter.js). " +
                    "The [docs](https://svelte.dev/docs/svelte/$state) explain `$state`.\n\n" +
                    "![pixel](https://example.com/pixel.png)\n\n" +
                    "<a href=\"https://example.com\" style=\"position:fixed;inset:0\">continue</a>\n\n" +
                    "<button class=\"code-copy\" data-copy=\"1\">Copy</button>\n\nWEB-DONE");
        }
    }

    /// <summary>
    /// The ssh tool on host <c>nuc</c>: a <c>run</c> in <c>demo</c>, a <c>read</c> and an <c>edit</c> of <c>demo/nginx.conf</c>
    /// (<c>listen 80;</c> → <c>listen 8080;</c>), a <c>copy</c> of <c>demo/error.log</c> down to <c>logs/nuc-error.log</c>. The
    /// host's home needs both files; the run prints <c>nginx: active</c>.
    /// </summary>
    private static Plan SshWork(int step) => step switch
    {
        0 => new Plan { Text = "Checking the site on nuc." }
            .Call("ssh", Obj(("action", "run"), ("host", "nuc"), ("cwd", "demo"), ("script", "grep -c listen nginx.conf >/dev/null && echo \"nginx: active\""))),
        1 => new Plan { Text = "Reading its config." }.Call("ssh", Obj(("action", "read"), ("host", "nuc"), ("path", "demo/nginx.conf"))),
        2 => new Plan { Text = "Moving it to 8080." }.Call("ssh", Obj(("action", "edit"), ("host", "nuc"), ("path", "demo/nginx.conf"),
            ("edits", new JsonArray(Obj(("oldText", "listen 80;"), ("newText", "listen 8080;")))))),
        3 => new Plan { Text = "Keeping its error log." }.Call("ssh", Obj(("action", "copy"), ("host", "nuc"), ("direction", "download"), ("from", "demo/error.log"), ("to", "logs/nuc-error.log"))),
        _ => Final("The demo site on nuc listens on 8080 now. SSH-DONE"),
    };

    /// <summary>A foreground command that runs until a file appears in the session's folder: the test ends it by writing the file.</summary>
    private static Plan FgWait(ScenarioTag tag, int step)
    {
        var file = tag.P("file", ".release");
        if (step == 0)
            return new Plan { Text = "Indexing the docs." }
                .Call("bash", Obj(("command", $"echo \"indexing docs/\"; until [ -f {file} ]; do sleep 0.2; done; echo indexed")));
        return Final("The docs are indexed. FGWAIT-DONE");
    }

    /// <summary>A background command that prints a line every <c>ms</c> (<c>lines</c> of them), and the run ends at once.</summary>
    private static Plan BgProc(ScenarioTag tag, int step)
    {
        if (step == 0)
            return new Plan { Text = "Starting the dev server in the background." }
                .Call("bash", Obj(("command", $"for i in $(seq 1 {tag.PInt("lines", 900)}); do echo \"dev server: request $i\"; sleep {(tag.PInt("ms", 250) / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}; done"), ("background", true)));
        return Final("The dev server runs in the background. BGPROC-STARTED");
    }

    private static Plan Where(ScenarioTag tag, int step, List<NMsg> after)
    {
        var file = tag.P("file", "where.txt");
        if (step == 0)
            return new Plan { Text = "Checking the working directory." }
                // Git Bash's pwd -W prints the Windows path (C:/Users/…), so the e2e compares real paths instead of guessing
                // its mount table (/tmp is TEMP only while both are spelled alike, idea-gkx7io); plain pwd elsewhere
                .Call("bash", Obj(("command", "pwd -W 2>/dev/null || pwd")))
                .Call("write", Obj(("path", file), ("content", "written by the mock\n")));
        var results = ToolResults(after);
        var pwd = results.Count >= 2 ? results[^2].Text.Trim() : "?";
        return Final($"PWD={FirstLine(pwd)}\n\nWHERE-DONE");
    }

    private static Plan TextCall(int step, List<NMsg> after)
    {
        // A standalone envelope: the whole message is the call, which is what repair accepts.
        if (step == 0)
            return new Plan
            {
                Thinking = "I should check the agents.",
                Text = "<tool_call>\n<function=agent_choices>\n</function>\n</tool_call>",
            };
        var result = ToolResults(after).LastOrDefault();
        return result is null
            ? Final("The tool call was not repaired. TEXTCALL-NOT-REPAIRED")
            : Final("Repaired call worked: " + FirstLine(result.Text) + "\n\nTEXTCALL-DONE");
    }

    private static Plan DocCall(int step, List<NMsg> after)
    {
        // A documentation example: prose around a fenced call. Repair must leave it as text, and the nudge asks again.
        if (step == 0)
            return new Plan
            {
                Text = "Here is an example of a tool call, and I am not running it:\n\n```xml\n<tool_call>\n<function=agent_choices>\n</function>\n</tool_call>\n```",
            };
        return Final("The example above is documentation, not a call. DOCCALL-STILL-TEXT");
    }

    private static Plan Cutoff(int step) => new()
    {
        Thinking = "Let me think about this carefully. The request needs a long analysis of every file, then a plan, then " +
                   "the implementation. I will start by enumerating the files and then consider each of them in turn, which " +
                   "takes a while because there are many details to consider before calling any tool",
        Stop = "length",
    };

    private Plan Drop(NRequest req, ScenarioTag tag)
    {
        var attempt = _attempts.AddOrUpdate(req.Fingerprint(), 1, (_, n) => n + 1);
        var failures = tag.PInt("n", 1);
        if (attempt <= failures)
            return new Plan
            {
                Thinking = "Starting to answer (this attempt will lose its connection).",
                Text = "This text is streamed and then the connection drops in the middle of the sentence, so the client must discard it and retry.",
                Drop = true,
                ChunkDelayMs = 20,
            };
        return Final($"Recovered after {attempt - 1} dropped connection(s). DROP-RECOVERED", "Answering again after the retry.");
    }

    private Plan Stall(NRequest req, ScenarioTag tag)
    {
        var attempt = _attempts.AddOrUpdate(req.Fingerprint(), 1, (_, n) => n + 1);
        if (attempt <= tag.PInt("n", 1))
            return new Plan
            {
                Text = "This stream starts normally and then goes silent in the middle, so the client must give up and retry.",
                StallMs = tag.PInt("ms", 60_000),
                ChunkDelayMs = 20,
            };
        return Final($"Recovered after {attempt - 1} stalled stream(s). STALL-RECOVERED");
    }

    private Plan MidFail(NRequest req, ScenarioTag tag)
    {
        var attempt = _attempts.AddOrUpdate(req.Fingerprint(), 1, (_, n) => n + 1);
        if (attempt <= tag.PInt("n", 1))
            return new Plan
            {
                Text = "Streaming normally until the backend reports an error inside the stream.",
                StreamErrorType = req.Api == "anthropic" ? "overloaded_error" : "server_error",
                ChunkDelayMs = 10,
            };
        return Final($"Recovered after {attempt - 1} in-stream error(s). MIDFAIL-RECOVERED");
    }

    private static Plan BadArgs(ScenarioTag tag, int step, List<NMsg> after)
    {
        var file = tag.P("file", "notes.txt");
        switch (step)
        {
            case 0:
                // truncated JSON (as if cut off) and a hallucinated tool name in one batch
                var plan = new Plan { Text = "Reading the file." };
                plan.Calls.Add(new NCall { Name = "read", Args = "{\"path\": \"" + file + "\"" });
                plan.Calls.Add(new NCall { Name = "read_file", Args = "{\"path\": \"" + file + "\"}" });
                return plan;
            case 1:
                var errors = ToolResults(after).Where(r => r.IsError || r.Text.StartsWith("Invalid JSON", StringComparison.Ordinal) || r.Text.StartsWith("Unknown tool", StringComparison.Ordinal)).ToList();
                return new Plan { Text = $"Got {errors.Count} errors; retrying with valid arguments." }.Call("read", Obj(("path", file)));
            default:
                return Final("Read it on the second try: " + FirstLine(ToolResults(after).LastOrDefault()?.Text) + "\n\nBADARGS-DONE");
        }
    }

    private Plan Error(NRequest req, ScenarioTag tag)
    {
        var attempt = _attempts.AddOrUpdate(req.Fingerprint(), 1, (_, n) => n + 1);
        if (attempt <= tag.PInt("n", 1))
        {
            var status = tag.PInt("status", 503);
            return new Plan
            {
                ErrorStatus = status,
                ErrorType = status == 503 ? "backend_unavailable" : status == 429 ? "rate_limit_error" : "server_error",
                ErrorMessage = $"mock: simulated HTTP {status} (attempt {attempt})",
            };
        }
        return Final($"Recovered after {attempt - 1} HTTP error(s). ERROR-RECOVERED");
    }

    private static Plan Slow(ScenarioTag tag)
    {
        var ms = tag.PInt("ms", 6000);
        var sb = new StringBuilder("Slow answer:");
        for (var i = 1; i <= 24; i++) sb.Append(" part ").Append(i).Append('.');
        sb.Append(" SLOW-DONE");
        var text = sb.ToString();
        var chunks = StreamChunks.Split(text).Count + StreamChunks.Split("Thinking slowly about it.").Count;
        return new Plan { Thinking = "Thinking slowly about it.", Text = text, ChunkDelayMs = Math.Max(1, ms / Math.Max(1, chunks)) };
    }

    private static Plan SlowTools(int step, List<NMsg> after)
    {
        if (step == 0)
            return new Plan { Thinking = "Three commands to run, one after the other.", Text = "Running three commands in sequence.", ChunkDelayMs = 60 }
                .Call("bash", Obj(("command", "sleep 2; echo first")))
                .Call("bash", Obj(("command", "echo second")))
                .Call("bash", Obj(("command", "echo third")));
        var results = ToolResults(after);
        var skipped = results.Count(r => r.Text.StartsWith("Skipped", StringComparison.Ordinal));
        return Final($"Ran {results.Count - skipped} command(s), {skipped} skipped. SLOWTOOLS-DONE");
    }

    private static Plan Spawn(ScenarioTag tag, int step, List<NMsg> after)
    {
        var n = tag.PInt("n", 3);
        var delay = tag.PInt("delay", 1500);
        var stagger = tag.PInt("stagger", 0); // worker i runs delay + (i-1)*stagger ms
        var model = tag.Params.GetValueOrDefault("model");
        switch (step)
        {
            case 0:
            {
                var plan = new Plan { Thinking = $"This splits into {n} independent parts; I'll delegate them.", Text = $"Delegating to {n} workers." };
                for (var i = 1; i <= n; i++)
                {
                    var args = Obj(("task", $"Worker task #{i}: square the number {i} and report the result. [s:sub i={i} delay={delay + (i - 1) * stagger} hold={tag.PInt("workerhold", 0)}]"), ("name", $"worker-{i}"), ("background", true));
                    if (model is not null) args["model"] = model;
                    if (tag.Params.TryGetValue("agent", out var agent)) args["agent"] = agent;
                    plan.Call("agent_spawn", args);
                }
                return plan;
            }
            case 1:
            {
                var failed = ToolResults(after).Where(r => r.IsError || r.Text.Contains("error", StringComparison.OrdinalIgnoreCase)
                                                             && !r.Text.Contains("Spawned", StringComparison.OrdinalIgnoreCase)).ToList();
                if (failed.Count > 0) return Final("Spawning failed: " + FirstLine(failed[0].Text) + " SPAWN-FAILED");
                return new Plan { Thinking = "Workers are running; I'll wait for their reports.", Text = "Waiting for the workers." }
                    .Call("agent", new JsonObject { ["action"] = "wait" });
            }
            default:
            {
                var wait = ToolResults(after).LastOrDefault()?.Text ?? "";
                var reports = wait.Split('\n').Where(l => l.Contains("Report from", StringComparison.Ordinal)).Select(l => "- " + l.Trim()).ToList();
                return Final($"All workers finished ({reports.Count} reports):\n\n{string.Join("\n", reports)}\n\nSPAWN-DONE",
                    "The reports are in; I'll summarize them.");
            }
        }
    }

    private static Plan SpawnBackground(ScenarioTag tag, int step)
    {
        if (step == 0)
        {
            var args = Obj(("task", $"Background task: report the number 7. [s:sub i=7 delay={tag.PInt("delay", 800)}{(tag.Params.TryGetValue("workergate", out var wg) ? $" gate={wg}" : "")}]"), ("name", tag.P("name", "bg-worker")), ("background", true));
            if (tag.Params.TryGetValue("agent", out var agent)) args["agent"] = agent;
            return new Plan { Text = "Starting a background worker." }.Call("agent_spawn", args);
        }
        return Final("The worker runs in the background; I will report when it finishes. SPAWNBG-STARTED");
    }

    /// <summary>Orchestrator → lead subagent → helper sub-subagent (agent_spawn waits for it), all on the same pool.</summary>
    private static Plan Nest(ScenarioTag tag, int step, List<NMsg> after)
    {
        switch (step)
        {
            case 0:
                return new Plan { Text = "Delegating to a lead." }
                    .Call("agent_spawn", Obj(("task", $"Lead the work: get a helper to square 5, then report. [s:subspawn delay={tag.PInt("delay", 800)}]"), ("name", "lead"), ("background", true)));
            case 1:
                return new Plan { Text = "Waiting for the lead." }.Call("agent", new JsonObject { ["action"] = "wait" });
            default:
            {
                var wait = ToolResults(after).LastOrDefault()?.Text ?? "";
                var line = wait.Split('\n').FirstOrDefault(l => l.Contains("Lead report", StringComparison.Ordinal)) ?? "(no lead report)";
                return Final("The lead reported: " + line.Trim() + "\n\nNEST-DONE");
            }
        }
    }

    private static Plan SubSpawn(NRequest req, ScenarioTag tag, int step, List<NMsg> after)
    {
        if (step == 0)
            return new Plan { Text = "Telling my parent, then delegating to a helper and waiting for it." }
                .Call("agent", Obj(("action", "send"), ("to", "parent"), ("message", "lead started")))
                .Call("agent_spawn", Obj(("task", $"Square 5. [s:sub i=5 delay={tag.PInt("delay", 800)}]"), ("name", "helper")));
        var result = ToolResults(after).LastOrDefault()?.Text ?? "";
        var report = result.Split('\n').FirstOrDefault(l => l.Contains("Report from", StringComparison.Ordinal))?.Trim() ?? "(no helper report)";
        return Final($"Lead report: helper said \"{report}\"");
    }

    private static Plan Sub(NRequest req, ScenarioTag tag)
    {
        var i = tag.PInt("i", 1);
        var delay = tag.PInt("delay", 1000);
        var name = AgentNameRx().Match(req.System) is { Success: true } m ? m.Groups[1].Value : "subagent";
        var thinking = $"I am {name}. The task is simple: {i} squared is {i * i}. Let me double-check that carefully before reporting.";
        var chunks = StreamChunks.Split(thinking).Count + 4;
        return new Plan
        {
            Thinking = thinking,
            Text = $"Report from {name}: {i} squared is {i * i}. SUB-REPORT-{i}",
            ChunkDelayMs = Math.Max(1, delay / chunks),
        };
    }

    private static Plan Ideas(ScenarioTag tag, int step, List<NMsg> after)
    {
        if (step == 0)
            return new Plan { Thinking = "This is worth remembering as an idea.", Text = "Recording an idea." }
                .Call("ideas", Obj(
                    ("action", "add"),
                    ("title", tag.P("title", "Mock idea from the E2E test")),
                    ("summary", "Added by the mock model through the ideas tool."),
                    ("tags", new JsonArray("e2e", "mock")),
                    ("priority", "high"),
                    ("sections", new JsonArray(Obj(("kind", "research"), ("title", "Findings"), ("content", "The mock found nothing surprising."))))));
        return Final("Idea recorded: " + FirstLine(ToolResults(after).LastOrDefault()?.Text) + "\n\nIDEAS-DONE");
    }

    private static Plan Ask(ScenarioTag tag, int step, List<NMsg> after)
    {
        if (step == 0)
        {
            var questions = tag.PInt("n", 1) >= 2
                ? new JsonArray(
                    Obj(("question", "How should I fix the reconnect?"),
                        ("options", new JsonArray(Obj(("label", "Patch the client")), Obj(("label", "Rewrite the client"))))),
                    Obj(("question", "Which tests should I run afterwards?"),
                        ("options", new JsonArray(Obj(("label", "unit")), Obj(("label", "e2e")), Obj(("label", "ui")))), ("multiple", true)))
                : new JsonArray(Obj(("question", "Which way?"), ("options", new JsonArray(Obj(("label", "Fast")), Obj(("label", "Thorough"))))));
            return new Plan { Thinking = "Two ways to do this; the user decides.", Text = "I can do it two ways." }
                .Call("ask_user", Obj(("questions", questions)));
        }
        return Final("Answer received: " + FirstLine(ToolResults(after).LastOrDefault()?.Text) + "\n\nASK-DONE");
    }

    /// <summary>
    /// A run as a person watches it: a read (its first answer waits at gate= when the tag has one), a find and a grep (more
    /// than three steps: the finished group folds), an edit, a stream that drops halfway on its first attempt (the retry,
    /// stream.reset), a shell command whose output comes a line every 250 ms, and the answer.
    /// </summary>
    private Plan WatchRun(NRequest req, ScenarioTag tag, int step)
    {
        var file = tag.P("file", "notes.txt");
        switch (step)
        {
            case 0:
                return new Plan { Thinking = "The scheduler throws on a missing pool; let me read the code first.", Text = "Let me look at the code." }
                    .Call("read", Obj(("path", file)));
            case 1:
                return new Plan { Thinking = "Where else is the pool looked up?" }
                    .Call("find", Obj(("pattern", "*.txt")))
                    .Call("grep", Obj(("pattern", tag.P("old", "alpha"))));
            case 2:
                return new Plan { Thinking = "Make the failure say what is missing.", Text = "Making the error clear." }
                    .Call("edit", Obj(("path", file), ("oldText", tag.P("old", "alpha")), ("newText", tag.P("new", "ALPHA-CLEAR"))));
            case 3:
                if (_attempts.AddOrUpdate(req.Fingerprint(), 1, (_, n) => n + 1) == 1)
                    return new Plan { Text = "Now I will build the solution to make sure it compiles before I answer, so that the change is checked and nothing else broke.", Drop = true, ChunkDelayMs = 20 };
                return new Plan { Text = "Building it." }
                    .Call("bash", Obj(("command", $"for i in $(seq 1 {tag.PInt("lines", 16)}); do echo \"build line $i\"; sleep 0.25; done")));
            default:
                return Final("The scheduler now fails with a clear message when a pool is missing. WATCHRUN-DONE");
        }
    }

    /// <summary>
    /// An edit of two files in one call, then a shell command that changes a third, clean one (a command's diff covers the
    /// files that were clean before it): the session's folder needs web/src/lib/rpc.svelte.js, docs/PROTOCOL.md and
    /// web/src/lib/config.js, committed.
    /// </summary>
    private static Plan MultiEdit(int step, List<NMsg> after) => step switch
    {
        0 => new Plan { Text = "Changing the client and the protocol together." }.Call("edit", Obj(("files", new JsonArray(
            Obj(("path", "web/src/lib/rpc.svelte.js"), ("edits", new JsonArray(Obj(("oldText", "const RECONNECT_MS = 1000;"), ("newText", "const RECONNECT_MS = 1000; // re-subscribes after a reconnect"))))),
            Obj(("path", "docs/PROTOCOL.md"), ("edits", new JsonArray(Obj(("oldText", "## Reconnect"), ("newText", "## Reconnect (with re-subscription)"))))))))),
        1 => new Plan { Text = "And a shorter first retry." }.Call("bash", Obj(("command", "sed -i 's/RECONNECT_MS = 1000/RECONNECT_MS = 250/' web/src/lib/config.js"))),
        _ => Final("Both files changed, and the first retry is quicker. MULTIEDIT-DONE"),
    };

    /// <summary>A command a guardrail rule may ask about: git status, then git push origin main (in the session's folder).</summary>
    private static Plan Guard(int step, List<NMsg> after) => step switch
    {
        0 => new Plan { Thinking = "Check what is left to push.", Text = "Checking the tree:" }.Call("bash", Obj(("command", "git status --short"))),
        1 => new Plan { Thinking = "The fix is committed; push it.", Text = "Committed. Pushing to origin:" }.Call("bash", Obj(("command", "git push origin main"))),
        _ => Final(ToolResults(after).LastOrDefault() is { IsError: true } r ? "I did not push: " + FirstLine(r.Text) + " GUARD-REFUSED" : "Pushed. GUARD-DONE"),
    };

    /// <summary>
    /// Plan mode as a person meets it: a plan of three steps and two files; asked for changes, a second revision with a
    /// fourth step that names them; approved, cancelled or moved to a new chat, it says so and stops. enter=1 first offers
    /// plan mode (plan_enter) and plans once the user accepts.
    /// </summary>
    private static Plan PlanUi(ScenarioTag tag, int step, List<NMsg> after)
    {
        var last = ToolResults(after).LastOrDefault()?.Text ?? "";
        Plan Submit(string? change) => new Plan { Thinking = "A plan first." }.Call("plan_submit", Obj(
            ("title", "Re-subscribe to the open chats"),
            ("summary", "After a reconnect the client re-subscribes the chats it shows, so no event is lost."),
            ("steps", change is null
                ? new JsonArray(Obj(("text", "Keep the open chats' ids")), Obj(("text", "Re-subscribe to the open chats on reconnect")), Obj(("text", "Reload what was missed")))
                : new JsonArray(Obj(("text", "Keep the open chats' ids")), Obj(("text", "Re-subscribe to the open chats on reconnect")), Obj(("text", "Reload what was missed")), Obj(("text", "Also: " + change)))),
            ("files", new JsonArray(Obj(("path", "web/src/lib/rpc.svelte.js"), ("note", "re-subscribe")), Obj(("path", "web/src/lib/state/app.svelte.js"), ("note", "reload"))))));
        if (step == 0)
            return tag.PInt("enter", 0) == 1
                ? new Plan { Text = "This is a bigger change." }.Call("plan_enter", Obj(("reason", "This touches the reconnect path in three places; a plan first is safer.")))
                : Submit(null);
        if (last.StartsWith("Plan mode is on", StringComparison.Ordinal)) return Submit(null);
        if (last.Contains("declined plan mode", StringComparison.Ordinal)) return Final("Going ahead without a plan. PLANUI-DECLINED");
        if (last.Contains("asked for changes", StringComparison.Ordinal))
        {
            var lines = last.Split('\n');
            return Submit(lines.Length > 1 ? lines[1].Trim() : "the change asked");
        }
        if (last.Contains("moved it to a new chat", StringComparison.Ordinal)) return Final("Moved to the new chat.");
        if (last.Contains("approved your plan", StringComparison.Ordinal)) return Final("Carrying the plan out. PLANUI-APPROVED");
        if (last.Contains("cancelled the plan", StringComparison.Ordinal)) return Final("Stopped planning. PLANUI-CANCELLED");
        return Final("PLANUI-DONE: " + FirstLine(last));
    }

    /// <summary>
    /// A goal (its objective carries the tag; every goal notice repeats it): a first pass that only looks, a second that does
    /// something (a tool call: progress), a third that completes the goal with goal_update. Counted over the whole chat.
    /// </summary>
    private static Plan GoalFlow(List<NMsg> msgs) => msgs.Count(m => m.Role == "assistant") switch
    {
        0 => Final("Looking at the demo page: the import is broken."),
        1 => new Plan { Text = "Fixing the import." }.Call("ls", Obj(("path", "."))),
        2 => Final("The import is fixed."),
        3 => new Plan { Text = "It renders: done." }.Call("goal_update", Obj(("status", "complete"), ("summary", "The page renders again: the import is fixed."))),
        _ => Final("The goal is reached. GOAL-DONE"),
    };

    /// <summary>Code mode: one script calls two tools at once; only its output comes back.</summary>
    private static Plan CodeMode(int step, List<NMsg> after)
    {
        if (step == 0)
            return new Plan { Thinking = "Two lookups at once, in one script." }
                .Call("codemode", Obj(("code", """
                    const [choices, listing] = await Promise.all([tools.agent_choices({}), tools.ls({ path: '.' })]);
                    text('choices: ' + (choices.length > 0));
                    return 'CODEMODE-LISTED ' + typeof listing;
                    """)));
        var result = ToolResults(after).LastOrDefault()?.Text ?? "";
        return Final((result.Contains("CODEMODE-LISTED string") ? "The script ran." : "The script did not run: " + FirstLine(result)) + "\n\nCODEMODE-DONE");
    }

    /// <summary>Plan mode: a write that is blocked, the plan, and (once the user decided) the write again.</summary>
    private static Plan PlanFlow(ScenarioTag tag, int step, List<NMsg> after)
    {
        var file = tag.P("file", "plan-e2e.txt");
        switch (step)
        {
            case 0:
                return new Plan { Thinking = "Plan mode is on, but let me try the write first.", Text = "Trying to write first." }
                    .Call("write", Obj(("path", file), ("content", "too early\n")));
            case 1:
                return new Plan { Thinking = "The write is blocked: submit the plan instead." }
                    .Call("plan_submit", Obj(
                        ("title", "Add the file"),
                        ("summary", "Create " + file + " with one line."),
                        ("steps", new JsonArray(Obj(("text", "Create the file")), Obj(("text", "Read it back")))),
                        ("files", new JsonArray(Obj(("path", file), ("note", "new")))),
                        ("tests", new JsonArray("read it back"))));
            case 2:
                return new Plan { Text = "Plan decided: " + FirstLine(ToolResults(after).LastOrDefault()?.Text) }
                    .Call("write", Obj(("path", file), ("content", "after approval\n")));
            default:
                return Final("Written after the decision. PLAN-DONE");
        }
    }

    private static Plan Long(ScenarioTag tag, List<NMsg> msgs)
    {
        var n = tag.PInt("n", 8);
        var lines = tag.PInt("lines", 60);
        var done = 0;
        foreach (var m in msgs)
            foreach (Match x in LongStepRx().Matches(m.Text))
                done = Math.Max(done, int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture));
        var next = done + 1;
        if (next > n) return Final($"LONG-DONE after {n} steps.");
        // tool=ls: a cheap step (no process per step) that carries its marker in the answer's own text: a long history fast
        if (tag.P("tool", "bash") == "ls")
            return new Plan { Text = $"LONGSTEP {next}/{n}: step {next} of {n}." }.Call("ls", Obj(("path", ".")));
        return new Plan { Thinking = $"Step {next} of {n}: produce more output.", Text = $"Step {next} of {n}." }
            .Call("bash", Obj(("command", $"echo \"LONGSTEP {next}/{n}\"; seq -f \"filler line %03g: lorem ipsum dolor sit amet, consectetur adipiscing elit\" 1 {lines}")));
    }

    private static Plan Summary(NRequest req)
    {
        var prompt = string.Join("\n", req.Messages.Where(m => m.Role == "user").Select(m => m.Text));
        var tags = ScenarioTag.All(prompt).Distinct().ToList();
        var steps = LongStepRx().Matches(prompt).Select(m => (K: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), N: m.Groups[2].Value)).ToList();
        var sb = new StringBuilder();
        sb.Append("## Task\nContinue the scripted mock scenario (MOCK-SUMMARY of ").Append(prompt.Length).Append(" prompt characters).\n");
        sb.Append("## Current state & next steps\n");
        if (tags.Count > 0) sb.Append("Scenario: ").Append(tags[^1]).Append('\n');
        if (steps.Count > 0)
        {
            var max = steps.MaxBy(s => s.K);
            sb.Append("Progress: LONGSTEP ").Append(max.K).Append('/').Append(max.N).Append(" completed.\n");
        }
        sb.Append("## Open questions\n(none)");
        return new Plan { Text = sb.ToString(), Scenario = "summary" };
    }
}

/// <summary>Splits streamed text into small word-aligned chunks.</summary>
public static class StreamChunks
{
    public static List<string> Split(string text, int size = 14)
    {
        var list = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var end = Math.Min(text.Length, i + size);
            if (end < text.Length)
            {
                var space = text.IndexOf(' ', end);
                if (space > 0 && space - i < size * 2) end = space + 1;
            }
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end++;
            list.Add(text[i..end]);
            i = end;
        }
        return list;
    }
}
