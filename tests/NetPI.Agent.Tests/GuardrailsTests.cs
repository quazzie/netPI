using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Guardrails;

namespace NetPI.Agent.Tests;

public static class GuardrailsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("guardrails: a blocked command never runs, and the model reads why", BlockedCommand);
        t.Add("guardrails: protected paths: write and edit refuse them, shell commands that name them too", ProtectedPaths);
        t.Add("guardrails: ask rules wait for the user's OK with the instance given back (allow, no, a message instead)", AskRules);
        t.Add("guardrails: in a subagent an ask rule blocks; switched off, nothing is checked", SubagentAndOff);
        t.Add("guardrails: the default rules block the catastrophic, not everyday work; spellings of a path", DefaultRules);
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
        Check.Equal((string?)asked["callId"], (string?)(await h.Rpc.CallAsync("guard.pending", new { sessionId = s.Id }))!.AsArray().Single()!["callId"]);
        await h.Rpc.InvokeAsync("guard.answer", new { callId = (string)asked["callId"]!, allow = true });
        await h.IdleAsync(s.Id);
        Check.Equal(1, ran.Count, "allowed: it ran");
        Check.Equal("allowed", (string?)FakeBus.Data(h.Bus.OfType("guard.closed").Last())["status"]);

        // no: it doesn't
        await h.SendAsync(s.Id, "push again");
        asked = await Asked(2);
        await h.Rpc.InvokeAsync("guard.answer", new { callId = (string)asked["callId"]!, allow = false });
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
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("guard.answer", new { callId = "call_x", allow = true }))).Code);
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
        Check.Equal("a|b|c|d|e 2>|1", string.Join("|", RuleSet.Parts("a && b || c; d | e 2>&1")));
        return Task.CompletedTask;
    }
}
