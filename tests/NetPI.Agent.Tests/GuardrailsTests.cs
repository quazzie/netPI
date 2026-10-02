using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Guardrails;

namespace NetPI.Agent.Tests;

public static class GuardrailsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("guardrails: duplicate provider call ids have independent approvals", DuplicateIds);
        t.Add("guardrails: a blocked command never runs, and the model reads why", BlockedCommand);
        t.Add("guardrails: protected paths: write and edit refuse them, shell commands that name them too", ProtectedPaths);
        t.Add("guardrails: ask rules wait for the user's OK with the instance given back (allow, no, a message instead)", AskRules);
        t.Add("guardrails: allowed for this chat, the rule stops asking there (not in other chats, never for a no)", AllowForSession);
        t.Add("guardrails: in a subagent an ask rule blocks; switched off, nothing is checked", SubagentAndOff);
        t.Add("guardrails: the default rules block the catastrophic, not everyday work; spellings of a path", DefaultRules);
        t.Add("guardrails: second opinion: a confidently read-only command runs without asking, the rest ask with the model's view", SecondOpinionClears);
        t.Add("guardrails: second opinion never relaxes a block or write/edit, is off by default, and asks when the model fails", SecondOpinionLimits);
        t.Add("guardrails: the call that runs is the call that is checked: tool-name case, argument aliases and their order, arrays, spaced keys, string-encoded arguments", RunsWhatIsChecked);
        t.Add("guardrails: a redirection, comment, quotes, sudo or a wrapper do not hide a blocked command; a rule that times out blocks", Decorations);
        t.Add("guardrails: an ssh download into a protected path is refused; an upload from it, and a download elsewhere, are not", SshDownload);
    }

    private static string Home => OperatingSystem.IsWindows() ? @"C:\Users\me" : "/home/me";

    private static Verdict? Judge(RuleSet rules, string tool, object args) => JudgeJson(rules, tool, JsonSerializer.Serialize(args));

    private static Verdict? JudgeJson(RuleSet rules, string tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return rules.Check(tool, doc.RootElement.Clone(), p => p);
    }

    /// <summary>The guard must judge the call the tool will run: the runner finds the tool ignoring case and the tools read
    /// their arguments by alias order, so the guard does the same, or a call can say one thing to it and another to the tool.</summary>
    private static Task RunsWhatIsChecked()
    {
        var rules = RuleSet.Parse(RuleSet.DefaultCommands, RuleSet.DefaultPaths, Home);
        var key = Path.Combine(Home, ".ssh", "authorized_keys");
        bool Blocks(string tool, object args) => Judge(rules, tool, args) is { Action: GuardAction.Block };

        foreach (var tool in (string[])["Bash", "BASH", "bAsH", "PWSH", "Pwsh"])
            Check.True(Blocks(tool, new { command = "rm -rf /" }), $"blocked as {tool}");
        foreach (var tool in (string[])["Write", "EDIT", "wRiTe"])
            Check.True(Judge(rules, tool, new { path = key, content = "x" }) is { Action: GuardAction.Block, Kind: "path" }, $"~/.ssh blocked as {tool}");

        // every name the shell tools read a command by (ShellService: command, cmd, script, code, commands, input)
        foreach (var name in (string[])["command", "cmd", "script", "code", "commands", "input", "Command", "COMMAND", "com mand", "co_mm-and"])
            Check.True(Blocks("bash", new Dictionary<string, string> { [name] = "rm -rf /" }), $"command read from '{name}'");
        // an array is its lines; the tool joins it with new lines
        Check.True(Blocks("bash", new { command = new[] { "ls", "rm -rf /" } }), "an array of lines");
        Check.True(Blocks("bash", new { command = new[] { "rm -rf /" } }), "an array of one line");
        // the tool tries the names in its own order, so the first present one is what runs: the guard judges that one
        Check.True(Blocks("bash", new { cmd = "ls", command = "rm -rf /" }), "command comes before cmd, whatever order the model wrote them in");
        Check.True(Judge(rules, "bash", new { command = "ls", cmd = "rm -rf /" }) is null, "ls runs, so ls is what is judged");
        // a string-encoded arguments object is unwrapped by the tools
        Check.True(JudgeJson(rules, "bash", JsonSerializer.Serialize(JsonSerializer.Serialize(new { command = "rm -rf /" }))) is { Action: GuardAction.Block }, "string-encoded arguments");

        // ssh run reads script first, then command, cmd, code
        foreach (var name in (string[])["script", "command", "cmd", "code"])
            Check.True(Blocks("ssh", new Dictionary<string, string> { ["action"] = "run", [name] = "rm -rf /" }), $"ssh run {name}");
        Check.True(Blocks("ssh", new { action = "run", command = "ls", script = "rm -rf /" }), "ssh: script comes first");
        Check.True(Judge(rules, "ssh", new { action = "run", script = "ls", command = "rm -rf /" }) is null, "ssh: script ls runs");

        // the file tools read path, file_path, file, filename, target in that order
        foreach (var name in (string[])["path", "file_path", "filePath", "file", "filename", "fileName", "target", "File-Path", "FILE"])
            Check.True(Blocks("write", new Dictionary<string, string> { [name] = key, ["content"] = "x" }), $"write path read from '{name}'");
        Check.True(Blocks("edit", new { path = new[] { key }, oldText = "a", newText = "b" }), "an array path");
        Check.True(Blocks("write", new { target = "/tmp/x", path = key, content = "x" }), "path comes before target");
        Check.True(Judge(rules, "write", new { path = "/tmp/x", file = key, content = "x" }) is null, "/tmp/x is what is written");

        // a shell command that names a protected path is caught under any tool-name case too
        Check.True(Blocks("Bash", new { command = "cat ~/.ssh/id_rsa" }), "path rule via Bash");
        return Task.CompletedTask;
    }

    /// <summary>The default rules anchor on the whole part, so what a shell ignores — a trailing comment, a redirection, quotes,
    /// sudo and its options, env, command, time, then/do — must not decide whether they match. The extra forms only widen.</summary>
    private static Task Decorations()
    {
        var rules = RuleSet.Parse(RuleSet.DefaultCommands, RuleSet.DefaultPaths, Home);
        bool Blocks(string command) => Judge(rules, "bash", new { command }) is { Action: GuardAction.Block, Kind: "command" };

        string[] blocked =
        [
            "rm -rf / 2>/dev/null", "rm -rf ~ # tidy", "rm -rf ~/ >/dev/null 2>&1", "rm -rf \"$HOME\"", "sudo -n rm -rf /",
            "sudo -u root rm -rf /", "if x; then rm -rf /; fi", "rm -rf \\\n/", "env FOO=1 rm -rf /", "time rm -rf /", "command rm -rf /",
            "nohup rm -rf / &", "rm -rf / &> out.log", "rm -rf '/'", "ls && rm -rf / # why not", "rm -rf / >> log", "rm -rf /  2> err.txt",
            "Remove-Item -Recurse -Force C:\\ 2>$null", "sudo shutdown -h now # bye",
        ];
        foreach (var c in blocked) Check.True(Blocks(c), $"blocked: {c}");

        string[] fine =
        [
            "rm -rf build 2>&1", "rm -rf ./node_modules # clean", "echo \"sudo rm -rf /\" >> log", "git commit -m 'rm -rf /'",
            "grep 'rm -rf /' notes.txt", "ls > /dev/null 2>&1 && echo ok", "cat a.txt | sort &> out.txt", "sudo apt update", "time dotnet build",
            "env FOO=1 dotnet test", "nohup ./server &", "rm -rf ./out 2>/dev/null",
        ];
        foreach (var c in fine) Check.True(Judge(rules, "bash", new { command = c }) is null, $"allowed: {c}");

        // the & of a redirection is not a separator; a backslash at the end of a line continues it
        Check.Equal("a &> out|b >&2|c|d", string.Join("|", RuleSet.Parts("a &> out; b >&2 && c & d")));
        Check.Equal("rm -rf  /", string.Join("|", RuleSet.Parts("rm -rf \\\n/")));
        // the part as written is always one of the forms
        Check.True(RuleSet.Forms("sudo rm -rf / # x").Contains("sudo rm -rf / # x"), "as written");
        Check.True(RuleSet.Forms("sudo -n rm -rf / 2>&1").Contains("rm -rf /"), "decoration off");

        // a rule that times out on a part matches it: not knowing is not a pass
        var slow = RuleSet.Parse(["^(([a-z])+.)+[A-Z]([a-z])+$"], [], Home);
        Check.True(Judge(slow, "bash", new { command = new string('a', 45) + "!" }) is { Action: GuardAction.Block }, "a timeout blocks");
        return Task.CompletedTask;
    }

    /// <summary>scp writes the destination of a download on this machine, where the call says: it is a write like write and edit.</summary>
    private static Task SshDownload()
    {
        var rules = RuleSet.Parse(RuleSet.DefaultCommands, RuleSet.DefaultPaths, Home);
        var key = Path.Combine(Home, ".ssh", "authorized_keys");
        bool Blocks(string tool, object args) => Judge(rules, tool, args) is { Action: GuardAction.Block, Kind: "path" };

        Check.True(Blocks("ssh", new { action = "copy", direction = "download", host = "nuc", from = "/tmp/k", to = key }), "copy + direction");
        Check.True(Blocks("SSH", new { action = "copy", direction = "download", host = "nuc", from = "/tmp/k", to = key }), "tool name case");
        Check.True(Blocks("ssh", new { action = "download", host = "nuc", from = "/tmp/k", to = key }), "the action says the direction");
        Check.True(Blocks("ssh", new { action = "scp", mode = "download", host = "nuc", from = "/tmp/k", destination = key }), "scp, mode, destination");
        Check.True(Blocks("ssh", new { action = "copy", direction = "download", from = "/tmp/k", target = key }), "target is a destination name");
        Check.True(Blocks("ssh", new { action = "copy", direction = "download", script = "echo hi", to = key }), "a script does not hide the download");

        Check.True(Judge(rules, "ssh", new { action = "copy", direction = "upload", host = "nuc", from = key, to = "/tmp/k" }) is null, "an upload only reads");
        Check.True(Judge(rules, "ssh", new { action = "upload", from = key, to = "/tmp/k" }) is null, "action upload");
        Check.True(Judge(rules, "ssh", new { action = "run", script = "ls", to = key }) is null, "not a copy");
        Check.True(Judge(rules, "ssh", new { action = "copy", direction = "download", from = "/tmp/k", to = Path.Combine(Home, "project", "k") }) is null, "a download elsewhere");
        return Task.CompletedTask;
    }

    private static async Task DuplicateIds()
    {
        await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
        var approvals = new Approvals(new TestPluginContext(h, "guard-test"));
        var verdict = new Verdict(GuardAction.Ask, "ask: ^git push", "command", "git push");
        var call = new ToolCallPart { Id = "call_0", Name = "bash", Arguments = "{}" };
        var a = approvals.Add("sessionA", "agentA", call, verdict, null);
        var b = approvals.Add("sessionB", "agentB", call, verdict, null);
        Check.Equal(2, approvals.List(null).Count);
        Check.True(a.ApprovalId != b.ApprovalId);
        approvals.Answer(a.ApprovalId, true);
        Check.True(await a.Allowed.Task);
        Check.False(b.Allowed.Task.IsCompleted);
        await Check.ThrowsAsync<RpcException>(() => Task.Run(() => approvals.Answer(a.ApprovalId, true)));
        approvals.Answer(b.ApprovalId, false);
        Check.False(await b.Allowed.Task);
        Check.Equal(0, approvals.List(null).Count);
    }

    private static async Task<TestHost> StartAsync()
    {
        var h = await TestHost.StartAsync();
        await h.StartPluginAsync(new GuardrailsPlugin());
        return h;
    }

    /// <summary>A tool that records what it was asked to run.</summary>
    private static FakeTool Recorder(string name, List<string> ran) => new(name, (ctx, args, ct) =>
    {
        lock (ran) ran.Add(args.GetRawText());
        return Task.FromResult(ToolResult.Ok($"{name} ran"));
    });

    private static List<ToolResultPart> Results(TestHost h, string sessionId) =>
        [.. h.Messages(sessionId).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults)];

    private static async Task BlockedCommand()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("ok")
            : Reply.Tools(Reply.Call("bash", new { command = "cd build && rm -rf /" }), Reply.Call("bash", new { command = "git status --short" }));
        await h.SendAsync(s.Id, "clean up");
        await h.IdleAsync(s.Id);
        var results = Results(h, s.Id);
        Check.True(results[0].IsError);
        Check.Contains(results[0].Content, "Blocked: `rm -rf /` matches the guardrail");
        Check.Contains(results[0].Content, "Nothing ran");
        Check.False(results[1].IsError, "a harmless command runs");
        Check.Equal(1, ran.Count);
        Check.Contains(ran[0], "git status");
    }

    private static async Task ProtectedPaths()
    {
        await using var h = await StartAsync();
        var dir = Path.Combine(h.Workspace, "protected");
        Directory.CreateDirectory(dir);
        h.Settings.Set("guardrails.paths", new JsonArray(dir));
        var ran = new List<string>();
        h.AddTool(Recorder("write", ran));
        h.AddTool(Recorder("edit", ran));
        h.AddTool(Recorder("read", ran));
        h.AddTool(Recorder("bash", ran));
        var s = h.NewSession();
        var fwd = dir.Replace('\\', '/');
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r)
            ? Reply.Text("ok")
            : Reply.Tools(
                Reply.Call("write", new { path = Path.Combine(dir, "a.txt"), content = "x" }),
                Reply.Call("edit", new { file_path = fwd + "/sub/b.txt", oldText = "a", newText = "b" }),
                Reply.Call("read", new { path = Path.Combine(dir, "a.txt") }),
                Reply.Call("bash", new { command = $"cat \"{fwd}/a.txt\"" }),
                Reply.Call("write", new { path = dir + "-other/c.txt", content = "x" }));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        var results = Results(h, s.Id);
        Check.Contains(results[0].Content, "is protected by the guardrail");
        Check.Contains(results[1].Content, "is protected by the guardrail", "edit, file_path, forward slashes, a sub folder");
        Check.False(results[2].IsError, "read stays allowed");
        Check.Contains(results[3].Content, "is protected by the guardrail", "a shell command that names the path");
        Check.False(results[4].IsError, "a sibling folder with the same prefix is not protected");
        Check.Equal(2, ran.Count, "read and the sibling write ran: " + string.Join(" | ", ran));
    }

    private static async Task AskRules()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        var s = h.NewSession(model: "fake/solo");
        h.Catalog.Handler = (r, ct) =>
            Reply.LastUser(r) == "stop, don't" ? Reply.Text("stopped")
            : Reply.HasToolResult(r) ? Reply.Text("done")
            : Reply.Tool("bash", new { command = "git push origin main" });

        async Task<JsonNode> Asked(int n)
        {
            await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= n, "a tool call waits for the OK");
            return FakeBus.Data(h.Bus.OfType("guard.asked")[n - 1]);
        }

        // allowed: it runs
        await h.SendAsync(s.Id, "push");
        var asked = await Asked(1);
        Check.Equal("bash", (string?)asked["tool"]);
        Check.Equal("git push origin main", (string?)asked["subject"]);
        Check.Equal("ask: ^git push", (string?)asked["rule"]);
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "the call waits with the slot given back");
        Check.Equal("waiting for your OK", h.Runtime.GetBySession(s.Id)!.Activity);
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "the instance is free meanwhile");
        Check.Equal((string?)asked["approvalId"], (string?)(await h.Rpc.CallAsync("guard.pending", new { sessionId = s.Id }))!.AsArray().Single()!["approvalId"]);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)asked["approvalId"]!, allow = true });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count, "allowed: it ran");
        Check.Equal("allowed", (string?)FakeBus.Data(h.Bus.OfType("guard.closed").Last())["status"]);

        // no: it doesn't
        await h.SendAsync(s.Id, "push again");
        asked = await Asked(2);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)asked["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count, "refused: it did not run");
        Check.Contains(Results(h, s.Id).Last().Content, "Blocked: the user said no to `git push origin main`");

        // a message instead of an answer
        await h.SendAsync(s.Id, "push once more");
        await Asked(3);
        await h.SendAsync(s.Id, "stop, don't");
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count);
        Check.Contains(Results(h, s.Id).Last().Content, "the user wrote a new message instead");
        Check.Equal("stopped", h.Messages(s.Id)[^1].Text);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("guard.answer", new { approvalId = "call_x", allow = true }))).Code);
    }

    private static async Task AllowForSession()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push", "ask: ^git reset"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        h.Catalog.Handler = (r, ct) =>
            Reply.HasToolResult(r) ? Reply.Text("done")
            : Reply.Tool("bash", new { command = Reply.LastUser(r) == "reset" ? "git reset --hard" : "git push origin main" });
        async Task<string> Asked(int n)
        {
            await Wait.Until(() => h.Bus.OfType("guard.asked").Count >= n, "a tool call waits for the OK");
            return (string)FakeBus.Data(h.Bus.OfType("guard.asked")[n - 1])["approvalId"]!;
        }

        // "no" with scope session stores nothing: the next push asks again
        var s = h.NewSession(model: "fake/solo");
        await h.SendAsync(s.Id, "push");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = await Asked(1), allow = false, scope = "session" });
        await h.IdleAsync(s.Id);
        Check.Equal(0, ran.Count);
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?["guardrailsAllowed"] is null, "a refusal is not remembered");

        // allowed for this chat: it runs, and the next push in this chat runs without asking
        await h.SendAsync(s.Id, "push");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = await Asked(2), allow = true, scope = "session" });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count);
        Check.Equal("session", (string?)FakeBus.Data(h.Bus.OfType("guard.closed").Last())["scope"]);
        Check.Equal("ask: ^git push", (string?)h.Sessions.GetSession(s.Id)!.Meta!["guardrailsAllowed"]![0]);
        await h.SendAsync(s.Id, "push");
        await h.IdleAsync(s.Id);
        Check.Equal(2, ran.Count, "ran without asking");
        Check.Equal(2, h.Bus.OfType("guard.asked").Count, "not asked again");
        var cleared = FakeBus.Data(h.Bus.OfType("guard.cleared").Last());
        Check.Equal("session", (string?)cleared["by"]);
        Check.Equal("ask: ^git push", (string?)cleared["rule"]);

        // only that rule: another ask rule still asks in this chat
        await h.SendAsync(s.Id, "reset");
        await Asked(3);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = await Asked(3), allow = false });
        await h.IdleAsync(s.Id);

        // only this chat: another one asks
        var other = h.NewSession(model: "fake/solo");
        await h.SendAsync(other.Id, "push");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = await Asked(4), allow = true });
        await h.IdleAsync(other.Id);
        Check.Equal(3, ran.Count);
        Check.True(h.Sessions.GetSession(other.Id)!.Meta?["guardrailsAllowed"] is null, "allow once is not remembered");
    }

    private static async Task SubagentAndOff()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git push"));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.SessionId != parent.Id)
                return Reply.HasToolResult(r) ? Reply.Text("REPORT: blocked") : Reply.Tool("bash", new { command = "git push" });
            return Reply.HasToolResult(r) ? Reply.Text("parent done") : Reply.Tool("agent_spawn", new { task = "Push it.", name = "pusher" });
        };
        await h.SendAsync(parent.Id, "delegate");
        var p = await h.IdleAsync(parent.Id, 15_000);
        var child = h.Runtime.Get(p.Children.Single())!;
        await h.IdleAsync(child.SessionId);
        var result = Results(h, child.SessionId).Single(r => r.Name == "bash");
        Check.Contains(result.Content, "a subagent can't ask");
        Check.Equal(0, ran.Count);
        Check.Equal(0, h.Bus.OfType("guard.asked").Count, "nobody was asked");

        // switched off: nothing is checked
        h.Settings.Set("guardrails.enabled", JsonValue.Create(false));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tool("bash", new { command = "git push" });
        await h.SendAsync(s.Id, "push");
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count, "ran unchecked");
    }

    /// <summary>A fake decide.ask: answers by the command, and records every command it was asked about.</summary>
    private static List<string> FakeDecide(TestHost h, Func<string, (double ReadOnly, double Risk)?> answer)
    {
        var asked = new List<string>();
        h.Rpc.Register("decide.ask", (r, ct) =>
        {
            var command = r.Prop("state")?.GetProperty("command").GetString() ?? "";
            lock (asked) asked.Add(command);
            Check.Equal(4, r.Prop("questions")!.Value.EnumerateObject().Count(), "the four guard questions");
            if (answer(command) is not { } a) throw new RpcException("model_not_loaded", "qwen3.8-27b: HTTP 404 model_not_loaded");
            JsonObject Yes(double p) => new() { ["type"] = "noul", ["noul"] = p };
            return Task.FromResult<object?>(new JsonObject
            {
                ["destructive"] = Yes(a.Risk), ["stops_process"] = Yes(a.Risk / 2), ["remote_change"] = Yes(a.Risk), ["read_only"] = Yes(a.ReadOnly),
            });
        });
        return asked;
    }

    private static async Task SecondOpinionClears()
    {
        await using var h = await StartAsync();
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git"));
        h.Settings.Set("guardrails.secondOpinion", JsonValue.Create(true));
        var decided = FakeDecide(h, c => c.StartsWith("git status") ? (0.97, 0.02) : (0.05, 0.9));
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("bash", new { command = Reply.LastUser(r) });

        // read-only: cleared, it runs, nobody is asked
        await h.SendAsync(s.Id, "git status --short");
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count, "cleared: it ran");
        Check.Equal(0, h.Bus.OfType("guard.asked").Count, "nobody was asked");
        var cleared = FakeBus.Data(h.Bus.OfType("guard.cleared").Single());
        Check.Equal("git status --short", (string?)cleared["subject"]);
        Check.Equal(true, (bool?)cleared["opinion"]!["harmless"]);
        Check.Equal("qwen3.8-27b", (string?)cleared["opinion"]!["model"]);
        Check.Equal(0.97, (double?)cleared["opinion"]!["p"]!["read_only"]);

        // not read-only: asks as before, with the model's view on the card
        await h.SendAsync(s.Id, "git push origin main");
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count == 1, "it asks");
        var asked = FakeBus.Data(h.Bus.OfType("guard.asked").Single());
        Check.Equal(false, (bool?)asked["opinion"]!["harmless"]);
        Check.Equal(0.9, (double?)asked["opinion"]!["p"]!["remote_change"]);
        Check.Equal(false, (bool?)(await h.Rpc.CallAsync("guard.pending", new { sessionId = s.Id }))!.AsArray().Single()!["opinion"]!["harmless"]);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)asked["approvalId"]!, allow = true });
        await h.IdleAsync(s.Id);
        Check.Equal(2, ran.Count, "allowed by the user");
        Check.Equal("git status --short|git push origin main", string.Join("|", decided));

        // the threshold: risk answers of 0.02 are too much at 0.01
        h.Settings.Set("guardrails.secondOpinionThreshold", JsonValue.Create(0.01));
        await h.SendAsync(s.Id, "git status");
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count == 2, "a stricter threshold asks");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[1])["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);

        Check.True(SecondOpinion.IsHarmless(new Dictionary<string, double> { ["read_only"] = 0.85, ["destructive"] = 0.1, ["stops_process"] = 0.1, ["remote_change"] = 0.19 }, 0.2));
        Check.False(SecondOpinion.IsHarmless(new Dictionary<string, double> { ["read_only"] = 0.85, ["destructive"] = 0.1, ["stops_process"] = 0.1, ["remote_change"] = 0.2 }, 0.2), "a risk at the threshold");
        Check.True(SecondOpinion.IsHarmless(new Dictionary<string, double> { ["read_only"] = 0.1, ["destructive"] = 0, ["stops_process"] = 0, ["remote_change"] = 0 }, 0.2), "read-only is shown, not required");
        Check.False(SecondOpinion.IsHarmless(new Dictionary<string, double> { ["read_only"] = 1 }, 0.2), "a missing answer never clears");
        Check.False(SecondOpinion.IsHarmless(new Dictionary<string, double> { ["read_only"] = 1, ["destructive"] = 0, ["stops_process"] = 0 }, 0.2), "every risk must be answered");
    }

    private static async Task SecondOpinionLimits()
    {
        await using var h = await StartAsync();
        var dir = Path.Combine(h.Workspace, "asked");
        Directory.CreateDirectory(dir);
        h.Settings.Set("guardrails.commands", new JsonArray("ask: ^git", @"^rm\s+-rf\s+/$"));
        h.Settings.Set("guardrails.paths", new JsonArray("ask: " + dir));
        var decided = FakeDecide(h, c => c.StartsWith("git fetch") ? null : (1.0, 0.0)); // harmless to everything but fetch, which fails
        var ran = new List<string>();
        h.AddTool(Recorder("bash", ran));
        h.AddTool(Recorder("write", ran));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("bash", new { command = Reply.LastUser(r) });

        // off by default: the model is never asked
        await h.SendAsync(s.Id, "git status");
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count == 1, "asks without a second opinion");
        Check.Equal(0, decided.Count, "off by default");
        Check.True(FakeBus.Data(h.Bus.OfType("guard.asked")[0])["opinion"] is null);
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[0])["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);

        h.Settings.Set("guardrails.secondOpinion", JsonValue.Create(true));

        // a blocking rule is never relaxed, and the model is not asked about it
        await h.SendAsync(s.Id, "rm -rf /");
        await h.IdleAsync(s.Id);
        Check.Contains(Results(h, s.Id).Last().Content, "matches the guardrail");
        Check.Equal(0, decided.Count, "blocks are decided by the rules alone");

        // write into an ask path: always asks, the model is not consulted
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("write", new { path = Path.Combine(dir, "a.txt"), content = "x" });
        await h.SendAsync(s.Id, "write it");
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count == 2, "write asks");
        Check.Equal(0, decided.Count, "write/edit never get a second opinion");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)FakeBus.Data(h.Bus.OfType("guard.asked")[1])["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);

        // the model fails: the user is asked, and the card says why there is no opinion
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tool("bash", new { command = Reply.LastUser(r) });
        await h.SendAsync(s.Id, "git fetch");
        await Wait.Until(() => h.Bus.OfType("guard.asked").Count == 3, "a failed opinion asks");
        var failed = FakeBus.Data(h.Bus.OfType("guard.asked")[2]);
        Check.Equal(false, (bool?)failed["opinion"]!["harmless"]);
        Check.Contains((string?)failed["opinion"]!["error"] ?? "", "model_not_loaded");
        await h.Rpc.InvokeAsync("guard.answer", new { approvalId = (string)failed["approvalId"]!, allow = false });
        await h.IdleAsync(s.Id);
        Check.Equal(0, ran.Count, "nothing ran without the user's OK");
        Check.Equal(0, h.Bus.OfType("guard.cleared").Count);

        // a subagent: an ask rule the model clears runs (nobody could have been asked); one it doesn't clear blocks
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.SessionId != parent.Id)
                return Reply.HasToolResult(r) ? Reply.Text("REPORT: done") : Reply.Tools(Reply.Call("bash", new { command = "git log -1" }), Reply.Call("bash", new { command = "git fetch" }));
            return Reply.HasToolResult(r) ? Reply.Text("parent done") : Reply.Tool("agent_spawn", new { task = "Look.", name = "looker" });
        };
        await h.SendAsync(parent.Id, "delegate");
        var p = await h.IdleAsync(parent.Id, 15_000);
        var child = h.Runtime.Get(p.Children.Single())!;
        await h.IdleAsync(child.SessionId);
        var childResults = Results(h, child.SessionId).Where(r => r.Name == "bash").ToList();
        Check.False(childResults[0].IsError, "cleared in a subagent: it ran");
        Check.Contains(childResults[1].Content, "a subagent can't ask");
        Check.Equal(1, ran.Count);
        Check.Contains(ran[0], "git log -1");
    }

    private static Task DefaultRules()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\me" : "/home/me";
        var rules = RuleSet.Parse(RuleSet.DefaultCommands, RuleSet.DefaultPaths, home);
        Check.Equal(0, rules.Problems.Count, string.Join("; ", rules.Problems));
        Verdict? Bash(string command)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
            return rules.Check("bash", doc.RootElement.Clone(), p => p);
        }

        string[] blocked =
        [
            "rm -rf /", "sudo rm -rf / --no-preserve-root", "rm -rf ~", "rm -fr ~/", "rm -rf $HOME/*", "cd x && rm -rf /*",
            "rm -rf C:/", "rm -rf /c", "Remove-Item -Recurse -Force C:\\", "del /s /q C:\\", "mkfs.ext4 /dev/sda1",
            "dd if=/dev/zero of=/dev/sda bs=1M", "format C: /q", "shutdown /s /t 0", "sudo reboot", "Stop-Computer -Force", ":(){ :|:& };:",
        ];
        foreach (var c in blocked)
            Check.True(Bash(c) is { Action: GuardAction.Block, Kind: "command" }, $"blocked: {c}");

        string[] fine =
        [
            "rm -rf build", "rm -rf ./node_modules /tmp/x", "rm -rf ~/project/bin", "git status --short && git diff", "grep -rn shutdown src",
            "dotnet format", "npm run build 2>&1 | tail -20", "dd if=/dev/urandom of=/dev/null bs=1M count=10", "echo 'rm -rf /' > notes.txt",
            "Remove-Item -Recurse -Force .\\bin", "ls -la /c/Users/me/source", "cat /etc/hostname",
        ];
        foreach (var c in fine)
            Check.True(Bash(c) is null, $"allowed: {c}");

        // ~/.netpi asks first: in every spelling a shell uses; a similar name does not match
        foreach (var c in (string[])["cat ~/.netpi/settings.json", "ls $HOME/.netpi", "type %USERPROFILE%\\.netpi\\settings.json", $"ls \"{Path.Combine(home, ".netpi")}\"", "ls ~/.netpi"])
        {
            if (!OperatingSystem.IsWindows() && c.Contains('%')) continue;
            Check.True(Bash(c) is { Action: GuardAction.Ask, Kind: "path" }, $"asks: {c}");
        }
        Check.True(Bash("ls ~/.netpi-backup") is null, "a similar name is not the protected folder");
        Check.True(Bash("cat ~/.ssh/id_ed25519.pub") is { Action: GuardAction.Block }, "~/.ssh is blocked");
        if (OperatingSystem.IsWindows())
        {
            Check.True(Bash("ls /c/Users/me/.netpi/logs") is { Action: GuardAction.Ask }, "the Git Bash spelling");
            Check.True(Bash("Get-Content $env:USERPROFILE\\.netpi\\settings.json") is { Action: GuardAction.Ask }, "the PowerShell spelling");
        }

        // a blocking rule wins over an asking one; rules parse ask:, block: and comments; a bad regex is reported
        var mixed = RuleSet.Parse(["ask: ^git", "block: push --force", "# a comment", "(unclosed"], [], home);
        Check.Equal(1, mixed.Problems.Count);
        Check.Equal(2, mixed.Count);
        using (var doc = JsonDocument.Parse("""{"command":"git push --force"}"""))
            Check.True(mixed.Check("bash", doc.RootElement.Clone(), p => p) is { Action: GuardAction.Block }, "block beats ask");
        Check.Equal("a|b|c|d|e 2>&1", string.Join("|", RuleSet.Parts("a && b || c; d | e 2>&1")));
        return Task.CompletedTask;
    }
}
