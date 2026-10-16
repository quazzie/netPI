using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NetPI.Tools.Ssh;
using static NetPI.Aux.Tests.SshTests;

namespace NetPI.Aux.Tests;

/// <summary>ssh background jobs, grep and find on the remote side, host notes, and the edit's eol.</summary>
public static class SshJobsTests
{
    public static void Register(TestRunner r)
    {
        r.Add("ssh run background: the script is saved and started detached, the answer is a job id; job output/wait/kill build their commands and read the status", RunBackground);
        r.Add("ssh job: a real bash runs the start, output, wait and kill commands (detached, exit code recorded, the group killed)", RealBashJob);
        r.Add("ssh grep / find: bounded remote commands, path:line: text rows, notes, truncation, glob translation", GrepAndFind);
        r.Add("ssh host notes: the first call of a chat to a host gets its note (files, folders, truncated), once, again after compaction", HostNotes);
        r.Add("ssh_edit: the eol it reports is crlf, lf or mixed", EolDetail);
    }

    private static async Task RunBackground()
    {
        var env = new Env();
        env.Fake.Reply = (args, _) => new SshExec(0, SshJobs.StatusMarker + System.Text.RegularExpressions.Regex.Match(Remote(args), @"id='(j\w+)'").Groups[1].Value + "\n", "", false, false);
        const string script = "echo \"it's $HOME\"\r\nsleep 1000";
        var res = await env.Run("ssh_run", new { host = "nuc", script, cwd = "/srv/x", background = true });
        Check.False(res.IsError, res.Content);
        var id = D(res).GetProperty("id").GetString()!;
        Check.True(SshJobs.IsValidId(id), id);
        Check.Contains(res.Content, $"Started job {id} on nuc");
        Check.Contains(res.Content, $"~/.cache/netpi-jobs/{id}.log");
        var call = env.Fake.Calls.Single();
        var remote = Remote(call.Args);
        Check.True(remote.StartsWith("cd -- '/srv/x' || exit 125; "), remote);
        Check.Contains(remote, "nohup bash -c");
        Check.Contains(remote, "setsid");
        Check.Contains(remote, ".exit.tmp");
        Check.Equal("echo \"it's $HOME\"\nsleep 1000\n", Utf8(call.Stdin), "the script as it is (LF), through stdin");
        Check.False(res.Content.Contains("timed out"));

        // a working directory that is not there, and a host that answers nothing useful
        env.Fake.Reply = (_, _) => new SshExec(125, "", "", false, false);
        Check.Contains((await env.Run("ssh_run", new { host = "nuc", script = "ls", cwd = "/nope", background = true })).Content, "working directory /nope does not exist");
        env.Fake.Reply = (_, _) => new SshExec(5, "", "cannot write", false, false);
        Check.Contains((await env.Run("ssh_run", new { host = "nuc", script = "ls", background = true })).Content, "failed: cannot write");

        // job: output (default), wait, kill
        env.Fake.Calls.Clear();
        env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_job=running\nline1\nline2\n", "", false, false);
        var output = await env.Run("ssh_job", new { host = "nuc", id = "j1abc", tail = 50 });
        Check.False(output.IsError, output.Content);
        Check.Equal("[job j1abc: running]\nline1\nline2", output.Content);
        Check.Contains(Remote(env.Fake.Calls[^1].Args), "tail -n 50 -- \"$d/$id.log\"");
        Check.Contains(Remote(env.Fake.Calls[^1].Args), "id='j1abc'");

        env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_job=exited 3\nboom\n", "", false, false);
        Check.Equal("[job j1abc: finished, exit code 3]\nboom", (await env.Run("ssh_job", new { host = "nuc", id = "j1abc", op = "wait", timeout = 5 })).Content);
        Check.Contains(Remote(env.Fake.Calls[^1].Args), "-lt 5 ]", "the wait is bounded by timeout");

        env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_job=running\n", "", false, false);
        var still = await env.Run("ssh_job", new { host = "nuc", id = "j1abc", op = "wait", timeout = 3 });
        Check.Contains(still.Content, "[job j1abc: still running after 3s]");
        Check.Contains(still.Content, "(no output yet)");

        env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_job=lost\n", "", false, false);
        Check.Contains((await env.Run("ssh_job", new { host = "nuc", id = "j1abc" })).Content, "lost");

        env.Fake.Reply = (_, _) => new SshExec(0, "__netpi_job=killed\nbye\n", "", false, false);
        var killed = await env.Run("ssh_job", new { host = "nuc", id = "j1abc", op = "kill" });
        Check.Equal("[job j1abc: killed]\nbye", killed.Content);
        Check.Contains(Remote(env.Fake.Calls[^1].Args), "kill -TERM -- \"-$p\"");
        Check.Contains(Remote(env.Fake.Calls[^1].Args), "kill -KILL");

        env.Fake.Reply = (_, _) => new SshExec(2, "", "No such job: j9\n", false, false);
        Check.Equal("nuc: No such job: j9", (await env.Run("ssh_job", new { host = "nuc", id = "j9" })).Content);
        var before = env.Fake.Calls.Count;
        Check.Contains((await env.Run("ssh_job", new { host = "nuc", id = "a'; rm -rf /" })).Content, "is not a job id");
        Check.Contains((await env.Run("ssh_job", new { host = "nuc" })).Content, "needs id");
        Check.Contains((await env.Run("ssh_job", new { host = "nuc", id = "j1", op = "pause" })).Content, "Unknown op");
        Check.Equal(before, env.Fake.Calls.Count, "refused before any ssh");
    }

    private static string? Bash()
    {
        foreach (var candidate in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe", "/bin/bash", "/usr/bin/bash" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static async Task<(int Exit, string Out, string Err)> RunBash(string bash, string home, string command, string? stdin = null)
    {
        var psi = new ProcessStartInfo(bash)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["HOME"] = home.Replace('\\', '/');
        using var p = Process.Start(psi)!;
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); await Task.WhenAll(o, e).WaitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new AssertException("the command did not return: the job is not detached from the call's pipes"); }
        return (p.ExitCode, await o, await e);
    }

    private static async Task RealBashJob()
    {
        var bash = Bash();
        if (bash is null) Check.Skip("no bash");
        var home = T.TempDir("ssh-job-home").Replace('\\', '/');
        var id = SshJobs.NewId();
        var started = await RunBash(bash!, home, SshJobs.StartCommand(id, null), "echo hi\nsleep 1\necho done >&2\nexit 3\n");
        Check.Equal(0, started.Exit);
        Check.Contains(started.Out, SshJobs.StatusMarker + id);

        var waited = await RunBash(bash!, home, SshJobs.WaitCommand(id, 100, 30));
        var (status, log) = SshJobs.Split(waited.Out);
        Check.Equal("exited 3", status);
        Check.Contains(log, "hi");
        Check.Contains(log, "done", "stderr is in the log too");
        var again = SshJobs.Split((await RunBash(bash!, home, SshJobs.OutputCommand(id, 1))).Out);
        Check.Equal("exited 3", again.Status);
        Check.Equal("done", again.Log.Trim(), "tail -n 1");

        var missing = await RunBash(bash!, home, SshJobs.OutputCommand("jnothere", 10));
        Check.Equal(2, missing.Exit);
        Check.Contains(missing.Err, "No such job: jnothere");

        // a job that runs: seen as running, killed, recorded as 143. (Where setsid gives the job a group of its own the kill
        // reaches its children too; this bash may have none, so what the job starts is short-lived: an orphan holds the
        // runner's pipes open until it ends.)
        var long1 = SshJobs.NewId();
        await RunBash(bash!, home, SshJobs.StartCommand(long1, null), "echo started\nsleep 4\n");
        var running = SshJobs.Split((await RunBash(bash!, home, SshJobs.OutputCommand(long1, 10))).Out);
        Check.Equal("running", running.Status);
        var killed = SshJobs.Split((await RunBash(bash!, home, SshJobs.KillCommand(long1, 10))).Out);
        Check.Equal("killed", killed.Status);
        var after = SshJobs.Split((await RunBash(bash!, home, SshJobs.OutputCommand(long1, 10))).Out);
        Check.Equal("exited 143", after.Status);
        Check.Contains(after.Log, "started");
    }

    private static async Task GrepAndFind()
    {
        var env = new Env();
        env.Fake.Reply = (_, _) => new SshExec(0, "./src/a.cs:12:    var x = 1;\n./src/b.cs:3:\tusing X; // " + new string('y', 400) + "\ngrep: /root: Permission denied\n", "", false, false);
        var res = await env.Run("ssh_grep", new { host = "nuc", pattern = "var x", path = "/srv/it's", glob = "*.cs, *.ts", ignoreCase = true, maxResults = 50 });
        Check.False(res.IsError, res.Content);
        var remote = Remote(env.Fake.Calls[^1].Args);
        Check.Contains(remote, "p='/srv/it'\\''s'");
        Check.Contains(remote, "grep -rnIHEi --exclude-dir=.git --exclude-dir=node_modules --include='*.cs' --include='*.ts' -e 'var x' -- \"$p\"");
        Check.Contains(remote, "head -n 100");
        Check.True(res.Content.StartsWith("src/a.cs:12:     var x = 1;"), res.Content[..40]);
        Check.Contains(res.Content, "src/b.cs:3: \tusing X; // yyy");
        Check.Contains(res.Content, "…", "a long line is cut");
        Check.Contains(res.Content, "[grep said: /root: Permission denied]");
        Check.Equal(2, D(res).GetProperty("matches").GetInt32());

        env.Fake.Reply = (_, _) => new SshExec(0, string.Concat(Enumerable.Range(1, 80).Select(i => $"f.txt:{i}:hit\n")), "", false, false);
        var cut = await env.Run("ssh_grep", new { host = "nuc", pattern = "hit", maxResults = 10 });
        Check.Equal(11, cut.Content.Split('\n').Count(l => l.StartsWith("f.txt:", StringComparison.Ordinal)) + 1);
        Check.Contains(cut.Content, "[Results truncated at 10 matches.");
        Check.True(D(cut).GetProperty("truncated").GetBoolean());

        env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
        Check.Equal("No matches for /zzz/ in nuc:..", (await env.Run("ssh_grep", new { host = "nuc", pattern = "zzz" })).Content, "(the path . and the sentence's dot)");
        env.Fake.Reply = (_, _) => new SshExec(0, "grep: Unmatched ( or \\(\n", "", false, false);
        var bad = await env.Run("ssh_grep", new { host = "nuc", pattern = "(" });
        Check.True(bad.IsError && bad.Content.Contains("Unmatched"), bad.Content);
        env.Fake.Reply = (_, _) => new SshExec(2, "", "No such file or directory: /x\n", false, false);
        Check.Contains((await env.Run("ssh_grep", new { host = "nuc", pattern = "a", path = "/x" })).Content, "No such file or directory");
        Check.Contains((await env.Run("ssh_grep", new { host = "nuc" })).Content, "needs a pattern");

        // find
        Check.Equal("-name '*.cs'", SshFindTool.Test("*.cs"));
        Check.Equal("\\( -name '*.ts' -o -name '*.tsx' \\)", SshFindTool.Test("*.{ts,tsx}"));
        Check.Equal("-path '*/src/*/test_*.py'", SshFindTool.Test("src/**/test_*.py"));
        Check.Equal("-path '*/*.cs'", SshFindTool.Test("**/*.cs"));
        env.Fake.Reply = (_, _) => new SshExec(0, "./a.cs\n./src/b.cs\n./src/c.cs\n", "", false, false);
        var found = await env.Run("ssh_find", new { host = "nuc", pattern = "*.cs", path = ".", maxResults = 2 });
        var findRemote = Remote(env.Fake.Calls[^1].Args);
        Check.Contains(findRemote, "find \"$p\" \\( -name .git -o -name node_modules \\) -prune -o -name '*.cs' -print 2>/dev/null | head -n 3");
        Check.Equal("a.cs\nsrc/b.cs\n\n[Showing the first 2 results. Use a more specific pattern or path, or raise maxResults.]", found.Content);
        env.Fake.Reply = (_, _) => new SshExec(0, "", "", false, false);
        Check.Equal("No files matching *.zzz in nuc:/srv.", (await env.Run("ssh_find", new { host = "nuc", pattern = "*.zzz", path = "/srv" })).Content);
        Check.Contains((await env.Run("ssh_find", new { host = "nuc" })).Content, "needs a pattern");
    }

    private static async Task HostNotes()
    {
        var sameTurn = SshHostNotes.SameTurn;
        SshHostNotes.SameTurn = TimeSpan.FromMilliseconds(300);
        try { await HostNotesCore(); }
        finally { SshHostNotes.SameTurn = sameTurn; }
    }

    private static async Task HostNotesCore()
    {
        var env = new Env();
        var notes = T.TempDir("ssh-notes");
        File.WriteAllText(Path.Combine(notes, "nuc.md"), "nuc: the media box. Do not reboot during backups.\n");
        var elsewhere = Path.Combine(T.TempDir("ssh-notes-file"), "server-notes.txt");
        File.WriteAllText(elsewhere, new string('s', 13 * 1024));
        env.Ctx.SettingsFake.Set("ssh.hostNotes", JsonNode.Parse("{\"folders\":[" + System.Text.Json.JsonSerializer.Serialize(notes) + "],\"files\":{\"server\":" + System.Text.Json.JsonSerializer.Serialize(elsewhere) + "}}"));
        var session = env.Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "chat", ProjectId = null });
        env.Fake.Reply = (_, _) => new SshExec(0, "ok\n", "", false, false);
        Task<ToolResult> Call(string host, string callId, string action = "run") => env.Tools["ssh"].ExecuteAsync(new ToolContext
        {
            SessionId = session.Id, AgentId = "agt_1", CallId = callId, Cwd = env.Dir, Services = env.Ctx.Services, Events = env.Ctx.Events,
        }, T.Args(new JsonObject { ["action"] = action, ["host"] = host, ["script"] = "echo ok", ["path"] = "/x", ["pattern"] = "x" }.ToJsonString()), CancellationToken.None);

        var first = await Call("nuc", "c1");
        Check.True(first.Content.StartsWith("ok\n\n[Host notes for nuc: "), first.Content);
        Check.Contains(first.Content, "nuc: the media box. Do not reboot during backups.");
        Check.Contains(first.Content, "nuc.md]");

        // its result is not in the context yet and a sibling call of the same turn starts: no second copy
        Check.Equal("ok", (await Call("nuc", "c2")).Content, "the same turn's other call");
        // once the first result is in the chat's context, no copy either
        env.Ctx.SessionsFake.AppendMessage(session.Id, T.ToolResult(("c1", "ssh", first.Content)));
        Check.Equal("ok", (await Call("nuc", "c3", "find")).Content, "any action, once");

        // a host with a file entry; the note is cut at 12 KB with a pointer to the file
        var server = await Call("server", "c4");
        Check.Contains(server.Content, "[Host notes for server: ");
        Check.Contains(server.Content, "[Truncated: the note is 13 KB; only the first 12 KB are shown. Full file: " + elsewhere + "]");
        Check.True(server.Content.Length < 12 * 1024 + 600, "bounded: " + server.Content.Length);

        // compaction dropped the note: it is shown again (the old messages are no longer in the context)
        env.Ctx.SessionsFake.Messages.Clear();
        await Task.Delay(400);
        var again = await Call("nuc", "c5");
        Check.Contains(again.Content, "[Host notes for nuc: ", "again after it was compacted away");

        // another chat has its own; a host without a note gets nothing; an unknown host is no host
        var other = env.Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "other", ProjectId = null });
        var third = await env.Tools["ssh"].ExecuteAsync(new ToolContext
        {
            SessionId = other.Id, AgentId = "agt_1", CallId = "c6", Cwd = env.Dir, Services = env.Ctx.Services, Events = env.Ctx.Events,
        }, T.Args(new { action = "run", host = "nuc", script = "x" }), CancellationToken.None);
        Check.Contains(third.Content, "[Host notes for nuc: ", "once per chat");
        env.Ctx.SettingsFake.Set("ssh.hostNotes", null);
        var none = await Call("server", "c7");
        Check.Equal("ok", none.Content, "no setting: no notes");
    }

    private static Task EolDetail()
    {
        Check.Equal("lf", SshEditTool.EolOf("a\nb\n"));
        Check.Equal("lf", SshEditTool.EolOf("no breaks"));
        Check.Equal("crlf", SshEditTool.EolOf("a\r\nb\r\n"));
        Check.Equal("mixed", SshEditTool.EolOf("a\r\nb\nc\r\n"));
        Check.Equal("mixed", SshEditTool.EolOf("a\nb\r\n"));
        return Task.CompletedTask;
    }
}
