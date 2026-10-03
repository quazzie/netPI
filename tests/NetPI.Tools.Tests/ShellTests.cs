using System.Diagnostics;
using System.Text;
using NetPI.Tools.Shell;

namespace NetPI.Tools.Tests;

public static class ShellTests
{
    private static (ShellService Service, ProcessRegistry Registry, FakeBus Bus) NewService(ISettings? settings = null)
    {
        var bus = new FakeBus();
        var registry = new ProcessRegistry(bus);
        return (new ShellService(registry, settings, Path.Combine(T.TempDir("shell"), "tmp")), registry, bus);
    }

    private static ShellTool Bash(ShellService s) => new("bash", s);

    /// <summary>Runs one short command with live-output and exit callbacks that capture a marker (standing for a plugin's closure).
    /// Not inlined, so nothing here keeps the marker alive once it returns.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Caller, ManagedProcess Process)> StartWithCallbacks(string dir)
    {
        var marker = new object();
        var caller = new WeakReference(marker);
        const string command = "echo hi";
        var spec = ShellLaunch.Bash(ShellLocator.FindBash(null)!, command, dir);
        var mp = ManagedProcess.Start(Ids.New("proc"), spec, command, dir, new OutputCapture(),
            live: _ => GC.KeepAlive(marker), onExited: _ => GC.KeepAlive(marker));
        await mp.Completion;
        return (caller, mp);
    }

    /// <summary>
    /// Is this <b>Windows</b> pid running? Only for pids Windows handed out (ManagedProcess.Pid).
    /// A pid from <c>bash $!</c> is an MSYS pid and must not be passed here — see <see cref="ShellChild"/>.
    /// </summary>
    private static bool ProcessAlive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
        }
        var stat = $"/proc/{pid}/stat";
        if (!File.Exists(stat)) return false;
        try
        {
            var text = File.ReadAllText(stat);
            var state = text[(text.LastIndexOf(')') + 2)..].Split(' ')[0];
            return state != "Z" && state != "X"; // zombies are dead (just not reaped by pid 1 in containers)
        }
        catch { return false; }
    }

    public static void Register(TestRunner r)
    {
        r.Add("shell: locator finds bash, never WSL; git-derived Git Bash path", () =>
        {
            var bash = ShellLocator.FindBash(null);
            Check.True(bash is not null && File.Exists(bash), "bash found");
            var derived = ShellLocator.BashFromGit(Path.Combine("X", "Git", "cmd", "git.exe")).ToList();
            Check.Equal(Path.Combine("X", "Git", "bin", "bash.exe"), derived[0]);
            derived = ShellLocator.BashFromGit(Path.Combine("X", "Git", "mingw64", "bin", "git.exe")).ToList();
            Check.Equal(Path.Combine("X", "Git", "bin", "bash.exe"), derived[0]);
            if (OperatingSystem.IsWindows()) Check.True(ShellLocator.IsWslBash(@"C:\Windows\System32\bash.exe"));
            var settings = new FakeSettings();
            settings.Set("shell.bashPath", bash);
            Check.Equal(bash, ShellLocator.FindBash(settings));
        });

        r.Add("shell: ANSI stripping (split sequences, OSC, C1) and carriage returns", () =>
        {
            var s = new AnsiStripper();
            var outp = s.Process("\x1b[31mre") + s.Process("d\x1b[") + s.Process("0m ok\x1b]0;title\a!\x1b[?25l\n");
            Check.Equal("red ok!\n", outp);
            Check.Equal("ab", AnsiStripper.Strip("a\x1b(Bb"));
            Check.Equal("tab\there", AnsiStripper.Strip("tab\there\a"));
            Check.Equal("c\nnext", ToolOutput.ResolveCarriageReturns("a\rb\rc\r\nnext"));
        });

        r.Add("shell: output tail formatting (lines, bytes, giant line)", () =>
        {
            var text = string.Join("\n", Enumerable.Range(1, 3000));
            var (tail, truncated, total, shown) = ToolOutput.TailLines(text, 2000, 30 * 1024);
            Check.True(truncated); Check.Equal(3000, total);
            Check.True(tail.EndsWith("\n3000"));
            Check.Equal(shown, tail.Split('\n').Length);
            Check.True(Encoding.UTF8.GetByteCount(tail) <= 30 * 1024);
            var (small, t2, _, _) = ToolOutput.TailLines("a\nb\n", 2000, 30 * 1024);
            Check.Equal("a\nb", small); Check.False(t2);
            var (giant, t3, _, shownGiant) = ToolOutput.TailLines(new string('z', 100_000) + "END", 2000, 1000);
            Check.True(t3 && giant.EndsWith("END") && giant.Length <= 1001 && shownGiant == 1);
        });

        r.Add("tools: text limit cuts (surrogate-safe head and head+tail, the truncation note)", () =>
        {
            // head: short is unchanged, cut appends the note with the count
            Check.Equal("ok", TextLimit.Head("ok", 10, "n"));
            Check.Equal("aaaaaaaaaa\n[... truncated: 5 more characters. the rest is lost]", TextLimit.Head(new string('a', 15), 10, "the rest is lost"));
            // a cut must not end on a lone high surrogate: it backs off one character and counts it in
            var surrogates = new string('a', 10) + "\ud83d" + new string('b', 5);
            Check.Equal("aaaaaaaaaa\n[... truncated: 6 more characters. n]", TextLimit.Head(surrogates, 11, "n"));

            // head+tail: short is unchanged, cut keeps both ends and the marker names (dropped, total)
            Check.Equal("ok", TextLimit.HeadTail("ok", 10, 2, (o, t) => $"~{o}~{t}~"));
            var both = TextLimit.HeadTail(new string('a', 50) + "END", 20, 2, (o, t) => $"~{o}~{t}~");
            Check.Equal(13, 20 * 2 / 3, "head gets 2 of 3 of the budget");
            Check.True(both.StartsWith(new string('a', 13)), both[..20]);
            Check.Equal("~33~53~", both[13..20], "the marker names the dropped and the total");
            Check.True(both.EndsWith("aaaaEND"), both[^10..]);

            // the note a tool prepends to a tail
            Check.Equal("[Output truncated: showing the last 200 lines of 3000 lines.]", ToolOutput.Note(200, " of 3000 lines"));
            Check.Equal("[Output truncated: showing the last 500 lines; only the last 8 MB of the output were kept. Those are saved to /tmp/x (use read or grep on it).]",
                ToolOutput.Note(500, "; only the last 8 MB of the output were kept", "/tmp/x", "Those are"));
        });

        r.Add("shell: timeout argument resolution", () =>
        {
            var settings = new FakeSettings();
            var (svc, _, _) = NewService(settings);
            Check.Equal<int?>(120, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { })), false));
            Check.Equal<int?>(null, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { })), true));
            Check.Equal<int?>(30, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout = "30" })), false));
            Check.Equal<int?>(1800, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout = 120000 })), false)); // seconds, clamped to the max — not a millisecond heuristic
            Check.Equal<int?>(1800, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout = 7200 })), false)); // a 2 h request is clamped, not read as 7.2 s
            Check.Equal<int?>(5, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout_ms = 4500 })), false));
            Check.Equal<int?>(1800, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout = 3000 })), false));
            Check.Equal<int?>(1, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { timeout = 0.2 })), false)); // under 1 s clamps to 1 s
            settings.Set("shell.timeoutSeconds", 7);
            Check.Equal<int?>(7, svc.ResolveTimeout(new NetPI.Tools.Shell.ToolArgs(T.Args(new { })), false));
        });

        r.Add("bash: foreground output, stderr merged, exit code, cwd, live streaming", async () =>
        {
            var (svc, registry, bus) = NewService();
            var dir = T.TempDir("bash");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var chunks = new List<string>();
            var ctx = T.Ctx(dir, output: c => { lock (chunks) chunks.Add(c); });
            var res = await Bash(svc).ExecuteAsync(ctx, T.Args(new { command = "echo out; echo err >&2; pwd; echo ünïcödé ✓" , cwd = "sub" }), default);
            Check.Ok(res);
            // stderr is merged into stdout at the source: exact ordering.
            Check.True(res.Content.StartsWith("out\nerr\n"), Check.Show(res.Content));
            Check.True(res.Content.Replace('\\', '/').EndsWith("/sub\nünïcödé ✓"), Check.Show(res.Content));
            Check.NotContains(res.Content, "[exit code");
            var d = T.D(res);
            Check.Equal(0, d.Int("exitCode")); Check.Equal("bash", d.Str("shell")); Check.False(d.Bool("background"));
            Check.False(d.Bool("truncated"));
            Check.True(d.Str("processId").StartsWith("proc_"));
            lock (chunks) Check.Contains(string.Concat(chunks), "out");

            res = await Bash(svc).ExecuteAsync(ctx, T.Args(new { command = "echo before; exit 3" }), default);
            Check.False(res.IsError, "non-zero exit is not an error");
            Check.Contains(res.Content, "before\n[exit code 3]");
            Check.Equal(3, T.D(res).Int("exitCode"));

            res = await Bash(svc).ExecuteAsync(ctx, T.Args(new { command = "true" }), default);
            Check.Equal("(no output)", res.Content);

            Check.Error(await Bash(svc).ExecuteAsync(ctx, T.Args(new { command = "ls", cwd = "nope" }), default), "does not exist");
            Check.Error(await Bash(svc).ExecuteAsync(ctx, T.Args(new { }), default), "command");

            // Foreground runs are recorded and published.
            Check.True(registry.List().Count >= 3);
            Check.True(registry.List().All(p => !p.IsRunning));
            Check.True(bus.OfType(ProcessEvents.Started).Count >= 3);
            Check.True(bus.OfType(ProcessEvents.Exited).Count >= 3);
        });

        r.Add("bash: ANSI codes and progress carriage returns are cleaned", async () =>
        {
            var (svc, _, _) = NewService();
            var res = await T.Run(Bash(svc), T.TempDir("bash"), new { command = @"printf '\033[1;32mgreen\033[0m\n'; printf 'p 10%%\rp 50%%\rp 100%%\n'" });
            Check.Equal("green\np 100%", res.Content);
        });

        r.Add("bash: timeout kills the whole process tree", async () =>
        {
            var (svc, _, _) = NewService();
            var dir = T.TempDir("bash");
            var sw = Stopwatch.StartNew();
            var res = await T.Run(Bash(svc), dir, new { command = "sleep 60 & echo started; sleep 60; echo never", timeout = 1 });
            sw.Stop();
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"returned after {sw.Elapsed}");
            Check.Error(res, "[timed out after 1s");
            Check.Contains(res.Content, "started");
            Check.NotContains(res.Content, "never");
            var d = T.D(res);
            Check.Equal("timeout", d.Str("status"));
            Check.True(d.Bool("timedOut"));
            // The point of the whole test. A `sleep 60` that survives the kill keeps the output pipe
            // open, and the caller then blocks on it long after the tool answered — which is exactly
            // what made the Tools suite take a minute longer than its tests did. Asserting on a pid
            // could never see that: bash's $! is an MSYS pid, and Process.GetProcessById throws on it.
            // The pipe is the honest witness, and it is the thing a caller actually waits on.
            Check.True(d.Bool("outputEof"), "captured output reached EOF: nothing is still holding the pipe");
            Check.NotContains(res.Content, "still running and holding its output open");
        });

        r.Add("bash: cancellation (abort) kills the process tree", async () =>
        {
            var (svc, _, _) = NewService();
            var dir = T.TempDir("bash");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var sw = Stopwatch.StartNew();
            var res = await Bash(svc).ExecuteAsync(T.Ctx(dir), T.Args(new { command = "sleep 30 & sleep 30" }), cts.Token);
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(8));
            Check.Error(res, "[aborted");
            Check.True(T.D(res).Bool("outputEof"), "captured output reached EOF after the abort");
        });

        // E2E regression: after a user abort, the next call of the batch still ran its command with the cancelled token.
        r.Add("bash: an already-cancelled call does not start the command", async () =>
        {
            var (svc, _, _) = NewService();
            var dir = T.TempDir("bash");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var res = await Bash(svc).ExecuteAsync(T.Ctx(dir), T.Args(new { command = "echo ran > ran.txt" }), cts.Token);
            Check.Error(res, "[aborted before the command started");
            await Task.Delay(200);
            Check.False(File.Exists(Path.Combine(dir, "ran.txt")), "the command did not run");
        });

        r.Add("bash: orphaned background child holding the pipe does not block", async () =>
        {
            var (svc, _, _) = NewService();
            var dir = T.TempDir("bash");
            // Here the child is meant to survive: the point is that the tool does not wait for it.
            // So the test owns it and takes it down itself, by the pid Windows actually uses.
            using var child = ShellChild.For(svc, dir, "orphan");
            var sw = Stopwatch.StartNew();
            var res = await T.Run(Bash(svc), dir, new { command = child.Preamble + "echo quick" });
            sw.Stop();
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
            Check.Contains(res.Content, "quick");
            Check.True(await child.IsAlive(5000), "the detached child should still be running when the tool returns");
            await child.RememberNativePid();
            Check.True(child.NativePid is > 0, "resolved the child's Windows pid (not the MSYS one)");
        });

        // The shell exits while a descendant still holds the output pipes: it must stay killable (the tree is alive)
        // and the pumps must keep draining, so the descendant neither goes un-killable nor clogs on a full pipe.
        r.Add("bash: a shell that exits leaving a pipe-holding descendant stays killable", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("zombie");
            using var child = ShellChild.For(svc, dir, "zombie");
            try
            {
                var sw = Stopwatch.StartNew();
                var res = await T.Run(Bash(svc), dir, new { command = child.Preamble + "echo quick" });
                sw.Stop();
                Check.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"returned promptly ({sw.Elapsed})");
                Check.Contains(res.Content, "quick");
                Check.Contains(res.Content, "still running and holding its output", "the result names the surviving descendant");
                var p = registry.Get(T.D(res).Str("processId"))!;
                Check.Equal("exited", p.Status, "the shell reported its exit");
                Check.False(p.OutputReachedEof, "the descendant still holds the pipes");
                Check.True(p.TreeMayBeAlive, "the tree is still alive and killable");
                Check.True(await child.IsAlive(5000), "the descendant is still running");

                // process kill reaches the surviving tree (the old code answered "not running" here).
                var id = p.Id;
                var kill = await T.Run(new ProcessTool(registry), dir, new { action = "kill", id });
                Check.Ok(kill);
                Check.Contains(kill.Content, "Killed");
                Check.True(T.D(kill).Bool("killed"));
                Check.Equal("killed", p.Status, "the status reflects the kill");

                var stopped = false;
                for (var i = 0; i < 25 && !stopped; i++) { await Task.Delay(200); stopped = await child.IsAlive(400) == false; }
                Check.True(stopped, "the descendant was actually killed");
                Check.Contains((await T.Run(new ProcessTool(registry), dir, new { action = "kill", id })).Content, "not running");

                // KillAllAsync reaches a shell that exited leaving a descendant, too (plugin stop).
                using var child2 = ShellChild.For(svc, dir, "zombie2");
                var res2 = await T.Run(Bash(svc), dir, new { command = child2.Preamble + "echo quick2" });
                var p2 = registry.Get(T.D(res2).Str("processId"))!;
                Check.True(p2.TreeMayBeAlive, "second descendant still holding the pipes");
                await registry.KillAllAsync(TimeSpan.FromSeconds(5));
                Check.False(await child2.IsAlive(400), "KillAllAsync reached the surviving descendant");
            }
            finally
            {
                child.Kill();
                await registry.KillAllAsync(TimeSpan.FromSeconds(5));
            }
        });

        r.Add("bash: output truncation keeps the tail and spills the full output", async () =>
        {
            var (svc, _, _) = NewService();
            var res = await T.Run(Bash(svc), T.TempDir("bash"), new { command = "seq 1 5000" });
            Check.Ok(res);
            Check.Contains(res.Content, "[Output truncated: showing the last");
            Check.True(res.Content.EndsWith("\n5000"), "tail kept");
            Check.NotContains(res.Content, "\n1\n2\n");
            var d = T.D(res);
            Check.True(d.Bool("truncated"));
            var path = d.Str("fullOutputPath");
            Check.Contains(res.Content, path);
            var lines = File.ReadAllLines(path);
            Check.Equal(5000, lines.Length);
            Check.Equal("1", lines[0]);

            // > 1MB: spill file is written while the command runs.
            res = await T.Run(Bash(svc), T.TempDir("bash"), new { command = "for i in $(seq 1 60000); do echo \"line $i of a long output stream\"; done" });
            d = T.D(res);
            var big = File.ReadAllLines(d.Str("fullOutputPath"));
            Check.Equal(60000, big.Length);
            Check.Equal("line 1 of a long output stream", big[0]);
            Check.True(res.Content.EndsWith("line 60000 of a long output stream"));
            Check.True(Encoding.UTF8.GetByteCount(res.Content) < 32 * 1024);
        });

        // Windows regression: the test also ran `bash -c <command>` on Windows, where Git Bash's MSYS runtime re-parses the
        // command line and collapses the backslashes; production never launches that way on Windows.
        r.Add("bash: Windows-style invocation (env + eval) preserves quoting and backslashes", async () =>
        {
            var bash = ShellLocator.FindBash(null)!;
            var dir = T.TempDir("bash");
            var command = "x='a\\\\b \"q\"'; printf '%s|%s\\n' \"$x\" 'C:\\Users\\me'\ncat <<'EOF'\nline $HOME \\n\nEOF\necho \"$0\" >/dev/null; echo ${NETPI_COMMAND:-unset}\necho 'héllo ✓ 日本'";
            const string expected = "a\\\\b \"q\"|C:\\Users\\me\nline $HOME \\n\nunset\nhéllo ✓ 日本\n";
            async Task<string> RunSpec(LaunchSpec spec)
            {
                var cap = new OutputCapture();
                var mp = ManagedProcess.Start(Ids.New("proc"), spec, command, dir, cap);
                await mp.Completion;
                return cap.Snapshot();
            }
            Check.Equal(expected, await RunSpec(ShellLaunch.Bash(bash, command, dir)), "default launch for this OS");
            Check.Equal(expected, await RunSpec(ShellLaunch.Bash(bash, command, dir, windowsStyle: true)), "env + eval");
            // argv reaches bash verbatim only outside Windows (the reason the Windows style exists)
            if (!OperatingSystem.IsWindows())
                Check.Equal(expected, await RunSpec(ShellLaunch.Bash(bash, command, dir, windowsStyle: false)), "bash -c");
        });

        // E2E reload.runtime-midrun failed whenever a bash call had run before: the registry keeps the last 50 finished
        // processes, and each kept the caller's live-output and exit callbacks (closures of the runtime plugin), so the old
        // runtime's load context could not be collected after a reload (the host logs "previous load context is still alive").
        r.Add("shell: a finished process lets go of its caller's callbacks, so reloading the calling plugin can unload it", async () =>
        {
            var (caller, mp) = await StartWithCallbacks(T.TempDir("bash"));
            Check.Equal("exited", mp.Status);
            for (var i = 0; i < 5 && caller.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }
            Check.False(caller.IsAlive, "a finished process still references its caller's callbacks");
            GC.KeepAlive(mp); // the record itself stays in the registry
        });

        r.Add("bash: very long commands go through a temp script", async () =>
        {
            var (svc, _, _) = NewService();
            var sb = new StringBuilder("total=0\n");
            for (var i = 0; i < 4000; i++) sb.Append("total=$((total + 1)) # padding padding\n");
            sb.Append("echo total=$total");
            Check.True(sb.Length > ShellLaunch.MaxInlineCommand);
            var res = await T.Run(Bash(svc), T.TempDir("bash"), new { command = sb.ToString() });
            Check.Equal("total=4000", res.Content);
            Check.Equal(0, Directory.GetFiles(svc.TempDir, "cmd-*").Length, "temp script deleted");
        });

        r.Add("background: lifecycle (start, list, output, kill, events)", async () =>
        {
            var (svc, registry, bus) = NewService();
            var dir = T.TempDir("bg");
            try
            {
                var sw = Stopwatch.StartNew();
                // Deliberately emit after the old 900 ms observation window.
                var res = await T.Run(Bash(svc), dir, new { command = "sleep 1.2; for i in 1 2 3; do echo tick $i; sleep 0.2; done; sleep 30 & wait", background = true });
                Check.True(sw.Elapsed < TimeSpan.FromSeconds(3), "returns immediately");
                Check.Ok(res);
                var d = T.D(res);
                var id = d.Str("processId");
                Check.Contains(res.Content, $"Started background process {id}");
                Check.True(d.Bool("background"));
                Check.Equal("running", d.Str("status"));

                var list = await T.Run(new ProcessTool(registry), dir, new { action = "list" });
                Check.Contains(list.Content, $"{id}  running");
                Check.Contains(list.Content, "bg  bash:");

                // Startup and the output pump can be slow under a parallel build/test load. Observe the
                // output we need, rather than assuming three lines arrived after a fixed sleep.
                var outputDeadline = Stopwatch.StartNew();
                while (!registry.Get(id)!.Output.Snapshot().Contains("tick 1\ntick 2\ntick 3", StringComparison.Ordinal)
                    && outputDeadline.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(25);
                var outRes = await T.Run(new ProcessTool(registry), dir, new { action = "output", id });
                Check.Contains(outRes.Content, "tick 1\ntick 2\ntick 3");
                Check.Contains(outRes.Content, "running");
                Check.True(bus.OfType(ProcessEvents.Output).Count > 0, "process.output events for background processes");

                var kill = await T.Run(new ProcessTool(registry), dir, new { action = "kill", id });
                Check.Ok(kill);
                Check.Contains(kill.Content, "Killed");
                var p = registry.Get(id)!;
                Check.Equal("killed", p.Status);
                Check.Contains((await T.Run(new ProcessTool(registry), dir, new { action = "list" })).Content, $"{id}  killed");
                Check.Contains((await T.Run(new ProcessTool(registry), dir, new { action = "kill", id })).Content, "not running");

                var started = bus.OfType(ProcessEvents.Started).Select(e => e.As<ProcEvt>()!).Single(e => e.Process.Id == id);
                Check.True(started.Process.Background);
                var exited = bus.OfType(ProcessEvents.Exited).Select(e => e.As<ProcEvt>()!).Single(e => e.Process.Id == id);
                Check.Equal("killed", exited.Process.Status);
                Check.Error(await T.Run(new ProcessTool(registry), dir, new { action = "output", id = "proc_nope" }), "No process");
            }
            finally
            {
                // A failed assertion must not leave sleep holding the suite's output pipe open.
                await registry.KillAllAsync(TimeSpan.FromSeconds(5));
            }
        });

        r.Add("process: list, output, wait and kill reach only the caller's session and its subagents", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("scope");
            using var pctx = new FakePluginContext(dir);
            var store = pctx.Sessions;
            store.CreateSession(new SessionInfo { Id = "ses_a", Kind = "chat" });
            store.CreateSession(new SessionInfo { Id = "ses_sub", Kind = "subagent", ParentSessionId = "ses_a" });
            store.CreateSession(new SessionInfo { Id = "ses_sub2", Kind = "subagent", ParentSessionId = "ses_sub" });
            store.CreateSession(new SessionInfo { Id = "ses_b", Kind = "chat" });
            store.CreateSession(new SessionInfo { Id = "ses_bsub", Kind = "subagent", ParentSessionId = "ses_b" });
            using (T.Services.Register<ISessionStore>(store))
            {
                ToolContext Ctx(string sessionId) => new()
                {
                    SessionId = sessionId, AgentId = "agt", CallId = "call", Cwd = dir,
                    Services = T.Services, Events = new FakeBus(),
                };
                var bash = Bash(svc);
                async Task<string> Bg(string sessionId, string tag)
                {
                    var res = await bash.ExecuteAsync(Ctx(sessionId), T.Args(new { command = $"echo job-{tag}; sleep 30", background = true }), default);
                    Check.Ok(res);
                    return T.D(res).Str("processId");
                }
                var idA = await Bg("ses_a", "a");
                var idSub = await Bg("ses_sub", "sub");
                var idSub2 = await Bg("ses_sub2", "sub2");
                var idB = await Bg("ses_b", "b");
                var idBSub = await Bg("ses_bsub", "bsub");
                try
                {
                    var tool = new ProcessTool(registry);

                    // list: the caller's own and its subagents' jobs (both levels); none of another chat's —
                    // not even that chat's subagents'.
                    var list = await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "list" }), default);
                    Check.Ok(list);
                    Check.Contains(list.Content, idA);
                    Check.Contains(list.Content, idSub);
                    Check.Contains(list.Content, idSub2);
                    Check.NotContains(list.Content, idB);
                    Check.NotContains(list.Content, idBSub);

                    // list all:true is read-only and shows every session's, with the session on each line.
                    var all = await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "list", all = true }), default);
                    Check.Ok(all);
                    Check.Contains(all.Content, idA);
                    Check.Contains(all.Content, idB);
                    Check.Contains(all.Content, "[ses_a]");
                    Check.Contains(all.Content, "[ses_b]");

                    // output and wait on another chat's job are refused, and it is left running.
                    Check.Error(await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "output", id = idB }), default), "another session");
                    Check.Error(await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "wait", id = idB, timeout = 1 }), default), "another session");
                    Check.Equal("running", registry.Get(idB)!.Status, "the foreign job was left running");

                    // own and subagent jobs still work.
                    var outSub = await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "output", id = idSub }), default);
                    Check.Ok(outSub);
                    Check.Contains(outSub.Content, "job-sub");
                    var waitSub2 = await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "wait", id = idSub2, timeout = 1 }), default);
                    Check.Ok(waitSub2);
                    Check.Equal("running", T.D(waitSub2).Str("status"), "a subagent's subagent's job is waitable");

                    // kill is refused for the foreign job and works on its own.
                    Check.Error(await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "kill", id = idB }), default), "another session");
                    Check.Equal("running", registry.Get(idB)!.Status, "the foreign job was left running after a refused kill");
                    var killA = await tool.ExecuteAsync(Ctx("ses_a"), T.Args(new { action = "kill", id = idA }), default);
                    Check.Ok(killA);
                    Check.Equal("killed", registry.Get(idA)!.Status);
                }
                finally
                {
                    // A failed assertion must not leave sleep holding the suite's output pipe open.
                    await registry.KillAllAsync(TimeSpan.FromSeconds(5));
                }
            }
        });

        r.Add("process wait: returns the moment the job exits, with its code, duration and output", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("wait");
            var res = await T.Run(Bash(svc), dir, new { command = "for i in 1 2 3; do echo tick $i; sleep 0.2; done", background = true });
            var id = T.D(res).Str("processId");
            var tool = new ProcessTool(registry);

            var sw = Stopwatch.StartNew();
            var w = await T.Run(tool, dir, new { action = "wait", id, timeout = 60 });
            sw.Stop();
            Check.Ok(w);
            // The acceptance rule: it comes back when the job does, not at the end of the timeout window.
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"returned in {sw.ElapsedMilliseconds} ms, not after the 60 s timeout");
            var d = T.D(w);
            Check.Equal("exited", d.Str("status"));
            Check.Equal(0, d.Int("exitCode"));
            Check.True(d.Int("durationMs") >= 500, $"durationMs {d.Int("durationMs")} (the job slept for 0.6 s)");
            Check.Contains(w.Content, "tick 1\ntick 2\ntick 3");
            Check.Contains(d.Str("output"), "tick 3");
            Check.Contains(w.Content, "exited 0");

            // Already finished: a second wait is free and says the same, so it is safe to fire blind.
            sw.Restart();
            var again = await T.Run(tool, dir, new { action = "wait", id });
            sw.Stop();
            Check.Equal("exited", T.D(again).Str("status"));
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"a finished job returns at once ({sw.ElapsedMilliseconds} ms)");

            Check.Error(await T.Run(tool, dir, new { action = "wait", id = "proc_nope" }), "No process with id proc_nope");
            Check.Error(await T.Run(tool, dir, new { action = "wait" }), "Missing required argument 'id'");
            // A silly timeout clamps instead of failing: 0 means a second, not "no wait at all".
            var clamped = await T.Run(tool, dir, new { action = "wait", id, timeout = 0 });
            Check.Ok(clamped);
            Check.Equal("exited", T.D(clamped).Str("status"));
        });

        r.Add("process wait: a cancelled call says the wait was cancelled, not that the job timed out", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("waitc");
            var res = await T.Run(Bash(svc), dir, new { command = "echo start; sleep 30", background = true });
            var id = T.D(res).Str("processId");
            var tool = new ProcessTool(registry);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var w = await T.Run(tool, dir, new { action = "wait", id, timeout = 30 }, ct: cts.Token);
                Check.Ok(w);
                Check.Contains(w.Content, "cancelled", "says the wait was cancelled");
                Check.NotContains(w.Content, "Still running after", "does not claim the timeout ran out");
                Check.True(T.D(w).Bool("cancelled"));
                Check.Equal("running", registry.Get(id)!.Status, "the job is left running, not lost");
            }
            finally
            {
                await T.Run(tool, dir, new { action = "kill", id });
            }
        });

        r.Add("process wait: a timeout is not a lost job — it says still running, for how long, with the last lines", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("wait2");
            var res = await T.Run(Bash(svc), dir, new { command = "echo first; sleep 30", background = true });
            var id = T.D(res).Str("processId");
            var tool = new ProcessTool(registry);

            var sw = Stopwatch.StartNew();
            var w = await T.Run(tool, dir, new { action = "wait", id, timeout = 1 });
            sw.Stop();
            Check.Ok(w);
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"waited the timeout and no longer ({sw.ElapsedMilliseconds} ms)");
            var d = T.D(w);
            Check.Equal("running", d.Str("status"));
            Check.True(d.Int("elapsedMs") >= 900, $"elapsedMs {d.Int("elapsedMs")} — how long the job has been going");
            Check.Equal(1000, d.Int("waitedMs"));
            Check.Contains(w.Content, "Still running after 1s");
            Check.Contains(w.Content, "first");
            Check.Contains(w.Content, "wait again with a longer timeout, or kill it");
            Check.Equal(1, d.GetProperty("lastLines").GetArrayLength());
            Check.Equal("first", d.GetProperty("lastLines")[0].GetString());
            Check.Equal("running", registry.Get(id)!.Status);

            // And the job is still waitable: a longer wait gets the result.
            var w2 = await T.Run(new ProcessTool(registry), dir, new { action = "wait", id, timeout = 1 });
            Check.Equal("running", T.D(w2).Str("status"));
            await T.Run(tool, dir, new { action = "kill", id });
            Check.Equal("killed", registry.Get(id)!.Status);
            var w3 = await T.Run(tool, dir, new { action = "wait", id });
            Check.Equal("killed", T.D(w3).Str("status"));
        });

        r.Add("process: wait is read-only, the actions are recognised, and kill and list are unchanged", async () =>
        {
            var (_, registry, _) = NewService();
            var tool = new ProcessTool(registry);
            var dir = T.TempDir("wait3");
            Check.True(tool.IsReadOnly(T.Args(new { action = "wait", id = "proc_x" })), "wait changes nothing");
            Check.True(tool.IsReadOnly(T.Args(new { action = "list" })));
            Check.False(tool.IsReadOnly(T.Args(new { action = "kill", id = "proc_x" })));
            Check.Error(await T.Run(tool, dir, new { action = "snooze" }), "use list, output, wait or kill");
            Check.Error(await T.Run(tool, dir, new { action = "await", id = "proc_nope" }), "No process with id");
            Check.Contains(tool.Definition.Parameters!.ToJsonString(), "wait");
            Check.Contains(tool.Definition.Parameters!.ToJsonString(), "timeout");
            // The description carries the policy, not just the mechanics: background-then-wait must not become the default.
            var guidelines = string.Join(" ", tool.Definition.PromptGuidelines!);
            Check.Contains(guidelines, "block on one you already backgrounded");
            Check.Contains(guidelines, "known duration");
            Check.Contains(tool.Definition.Help!, "tells slow from hung");
        });

        r.Add("background: immediate exit is reported; timeout kills; StopAsync kills all", async () =>
        {
            var (svc, registry, _) = NewService();
            var dir = T.TempDir("bg");
            var res = await T.Run(Bash(svc), dir, new { command = "echo oops; exit 2", background = true });
            Check.Contains(res.Content, "exited immediately with code 2");
            Check.Contains(res.Content, "oops");

            res = await T.Run(Bash(svc), dir, new { command = "sleep 30", background = true, timeout = 1 });
            var id = T.D(res).Str("processId");
            await registry.Get(id)!.Completion.WaitAsync(TimeSpan.FromSeconds(8));
            Check.Equal("timeout", registry.Get(id)!.Status);

            // Plugin-level: StopAsync kills running background processes.
            using var ctx = new FakePluginContext(dir);
            var plugin = new ShellPlugin();
            await plugin.StartAsync(ctx, default);
            var expectedTools = ShellLocator.FindPwsh(null, out _) is null
                ? "bash,process"      // pwsh is only offered when PowerShell exists
                : "bash,pwsh,process";
            Check.Equal(expectedTools, string.Join(",", ctx.ToolsFake.Tools.Select(t => t.Definition.Name)));
            var bash = ctx.ToolsFake.Get("bash")!;
            res = await bash.ExecuteAsync(T.Ctx(dir), T.Args(new { command = "sleep 30", background = true }), default);
            var pid = T.D(res).Int("pid");
            var procs = (System.Collections.IEnumerable)(await ctx.RpcFake.InvokeAsync("processes.list"))!;
            Check.True(procs.Cast<ProcessInfo>().Any(p => p.Status == "running"));
            await plugin.StopAsync(default);
            await Task.Delay(200);
            Check.False(ProcessAlive(pid), "killed on plugin stop");
            var output = (string)(await ctx.RpcFake.InvokeAsync("processes.output", new { id = T.D(res).Str("processId") }))!;
            Check.Equal("", output);
        });

        r.Add("pwsh: encoding helpers; tool reports absence clearly or runs when present", async () =>
        {
            var script = ShellLaunch.PwshScript("Write-Output 'hé'");
            Check.True(script.StartsWith("$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8;"));
            Check.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(ShellLaunch.EncodePwsh(script))));
            var spec = ShellLaunch.Pwsh("pwsh", "Get-Date", T.TempDir("pwsh"));
            Check.Equal("-NoLogo,-NoProfile,-NonInteractive", string.Join(",", spec.Arguments.Take(3)));
            Check.True(spec.Arguments.Contains("-EncodedCommand"));

            var (svc, _, _) = NewService();
            var pwsh = new ShellTool("pwsh", svc);
            Check.Equal("pwsh", pwsh.Definition.Name);
            var found = ShellLocator.FindPwsh(null, out _);
            var res = await T.Run(pwsh, T.TempDir("pwsh"), new { command = "Write-Output 'hi ✓'; cmd_that_does_not_exist_xyz 2>$null; exit 3" });
            if (found is null)
            {
                Check.Error(res, "PowerShell was not found");
                Console.WriteLine("        (pwsh not installed: absence path verified)");
            }
            else
            {
                Check.Contains(res.Content, "hi ✓");
                Check.Contains(res.Content, "[exit code 3]");
                Check.Equal("pwsh", T.D(res).Str("shell"));
            }
            // Configured path that does not exist falls back to discovery (still absent or found consistently).
            var settings = new FakeSettings();
            settings.Set("shell.pwshPath", "/definitely/not/here/pwsh");
            Check.Equal(found, ShellLocator.FindPwsh(settings, out _));
        });

        r.Add("shell tool definitions: labels, categories, read-only flags", () =>
        {
            var (svc, _, _) = NewService();
            foreach (var t in ShellPlugin.CreateTools(svc))
            {
                var d = t.Definition;
                Check.Equal("shell", d.Category);
                Check.True(d.Label is { Length: > 0 });
                Check.True(d.PromptGuidelines is not null); // deduplicated: some tools have none
                Check.False(d.ReadOnly, d.Name);
            }
            // process: list and output only read (they may run in parallel), kill does not
            var process = (IReadOnlyCalls)ShellPlugin.CreateTools(svc).Single(t => t.Definition.Name == "process");
            Check.True(process.IsReadOnly(T.Args(new { action = "list" })));
            Check.True(process.IsReadOnly(T.Args(new { action = "output", id = "proc_1" })));
            Check.False(process.IsReadOnly(T.Args(new { action = "kill", id = "proc_1" })));
        });
    }

    private sealed class ProcEvt
    {
        public ProcessInfo Process { get; set; } = null!;
    }
}
